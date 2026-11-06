using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services
{
    public enum SyncState
    {
        Idle,
        Scanning,
        Analyzing,
        Syncing,
        Paused,
        OfflineWaiting,
        Completed,
        Failed
    }

    public class SyncProgressReport
    {
        public SyncState State { get; set; }
        public int TotalFiles { get; set; }
        public int ScannedFiles { get; set; }
        public int UploadedFiles { get; set; }
        public int UnchangedFiles { get; set; }
        public int LongNameFiles { get; set; }
        public int FailedFiles { get; set; }
        public long BytesTransferred { get; set; }
        public double SpeedBytesPerSec { get; set; }
        public string CurrentFile { get; set; } = string.Empty;
        public string StatusMessage { get; set; } = string.Empty;
    }

    public class SyncAnalysisResult
    {
        public int TotalFiles { get; set; }
        public long TotalSizeBytes { get; set; }
        public int NewFiles { get; set; }
        public int ModifiedFiles { get; set; }
        public int UnchangedFiles { get; set; }
        public int LongNameFiles { get; set; }
        public List<string> SampleLongNames { get; set; } = new();
    }

    public class FailedSyncItem
    {
        public string RelativePath { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public DateTime FailedTime { get; set; } = DateTime.Now;
        public int RetryCount { get; set; }
    }

    public class SyncEngine
    {
        private LongNameEngine _longNameEngine;
        private readonly ManifestManager _manifestManager;
        private readonly SmbAuthService _smbService;

        private readonly SemaphoreSlim _concurrencyLock = new(1, 1);
        private CancellationTokenSource? _syncCts;
        private FileSystemWatcher? _watcher;
        private readonly ConcurrentDictionary<string, DateTime> _pendingWatcherEvents = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _watcherDebounceTimer;

        private SyncState _currentState = SyncState.Idle;
        private readonly object _stateLock = new();

        public event Action<SyncProgressReport>? ProgressChanged;
        public event Action<string, string>? LogEmitted; // (level, message)
        public event Action<SyncState, string>? StateChanged;
        public event Action? RequestWatcherTriggeredSync;

        public List<FailedSyncItem> FailedItems { get; } = new();

        public SyncEngine(LongNameEngine longNameEngine, ManifestManager manifestManager, SmbAuthService smbService)
        {
            _longNameEngine = longNameEngine;
            _manifestManager = manifestManager;
            _smbService = smbService;
            _watcherDebounceTimer = new Timer(OnWatcherDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        public SyncState CurrentState
        {
            get { lock (_stateLock) return _currentState; }
            private set
            {
                lock (_stateLock)
                {
                    if (_currentState != value)
                    {
                        _currentState = value;
                        StateChanged?.Invoke(_currentState, GetStateDescription(_currentState));
                    }
                }
            }
        }

        private static string GetStateDescription(SyncState state) => state switch
        {
            SyncState.Idle => "空闲中",
            SyncState.Scanning => "正在扫描本地文件...",
            SyncState.Analyzing => "正在分析变动...",
            SyncState.Syncing => "正在同步备份到 NAS...",
            SyncState.Paused => "已暂停",
            SyncState.OfflineWaiting => "NAS 离线，等待网络恢复...",
            SyncState.Completed => "同步完成",
            SyncState.Failed => "同步出错中断",
            _ => state.ToString()
        };

        private int _lastScannedCount = 0;
        /// <summary>
        /// 最近一次扫描到的源文件总数（用于在任务初始化或早期故障时准确统计已扫描文件数）
        /// </summary>
        public int LastScannedCount => _lastScannedCount;

        private void Log(string level, string message)
        {
            LogEmitted?.Invoke(level, message);
        }

        private static bool IsTransientException(Exception ex)
        {
            if (ex is OperationCanceledException) return false;
            if (ex is ArgumentException) return false;
            if (ex is PathTooLongException) return false;
            if (ex is NotSupportedException) return false;
            if (ex is UnauthorizedAccessException) return false;

            if (ex is InvalidOperationException invEx)
            {
                if (invEx.Message.Contains("ASCII", StringComparison.OrdinalIgnoreCase) ||
                    invEx.Message.Contains("未登录", StringComparison.OrdinalIgnoreCase) ||
                    invEx.Message.Contains("越界", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (ex.Message.Contains("4104") ||
                ex.Message.Contains("Path Is Too Long", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("499") ||
                ex.Message.Contains("401") ||
                ex.Message.Contains("403") ||
                ex.Message.Contains("未授权") ||
                ex.Message.Contains("禁止访问") ||
                ex.Message.Contains("Trim-Path 包含非法控制字符"))
            {
                return false;
            }

            return true;
        }

        private static async Task<bool> WaitForFileSettlingAsync(string fullPath, int maxWaitSeconds, CancellationToken token)
        {
            if (!File.Exists(fullPath)) return false;
            var sw = Stopwatch.StartNew();
            long prevSize = -1;
            while (sw.Elapsed.TotalSeconds < maxWaitSeconds)
            {
                if (token.IsCancellationRequested) return false;
                try
                {
                    var fi = new FileInfo(fullPath);
                    if (!fi.Exists) return false;
                    long curSize = fi.Length;
                    using (var s = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (prevSize >= 0 && curSize == prevSize)
                        {
                            return true;
                        }
                        prevSize = curSize;
                    }
                    await Task.Delay(300, token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
            return File.Exists(fullPath);
        }

        private Dictionary<string, string> DetectRenamesAndProcessDeletions(
            string longSource,
            List<FileInfo> allLocalFiles,
            Manifest manifest,
            Dictionary<string, ManifestFileEntry> manifestFilesMap,
            BackupStrategy strategy,
            IDictionary<string, string>? pendingRenames)
        {
            var detectedRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var localRelMap = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var fi in allLocalFiles)
            {
                string rel = PathHelper.NormalizeRelPath(Path.GetRelativePath(longSource, fi.FullName));
                localRelMap[rel] = fi;
            }

            var missingManifestEntries = new List<ManifestFileEntry>();
            foreach (var entry in manifest.Files)
            {
                if (!localRelMap.ContainsKey(entry.OriginalRel))
                {
                    missingManifestEntries.Add(entry);
                }
            }

            var candidateNewRelList = new List<string>();
            foreach (var kv in localRelMap)
            {
                if (!manifestFilesMap.ContainsKey(kv.Key))
                {
                    candidateNewRelList.Add(kv.Key);
                }
            }

            var matchedNewRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 优先比对外部传入的文件系统重命名事件提示 (pendingRenames)
            if (pendingRenames != null && pendingRenames.Count > 0)
            {
                foreach (var missing in missingManifestEntries)
                {
                    if (pendingRenames.TryGetValue(missing.OriginalRel, out string? hintNewRel) &&
                        !string.IsNullOrEmpty(hintNewRel) &&
                        localRelMap.ContainsKey(hintNewRel) &&
                        !matchedNewRels.Contains(hintNewRel))
                    {
                        detectedRenames[missing.OriginalRel] = hintNewRel;
                        matchedNewRels.Add(hintNewRel);
                    }
                }
            }

            // 2. 基于文件大小与 SHA-256 内容哈希进行精准重命名比对
            foreach (var missing in missingManifestEntries)
            {
                if (detectedRenames.ContainsKey(missing.OriginalRel)) continue;

                var sameSizeCandidates = candidateNewRelList
                    .Where(newRel => !matchedNewRels.Contains(newRel) && localRelMap[newRel].Length == missing.Size)
                    .ToList();

                if (sameSizeCandidates.Count == 1)
                {
                    string candidate = sameSizeCandidates[0];
                    string candidateHash = HashHelper.ComputeFileSha256(localRelMap[candidate].FullName);
                    if (!string.IsNullOrEmpty(missing.Sha256) && string.Equals(candidateHash, missing.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        detectedRenames[missing.OriginalRel] = candidate;
                        matchedNewRels.Add(candidate);
                    }
                }
                else if (sameSizeCandidates.Count > 1)
                {
                    foreach (var candidate in sameSizeCandidates)
                    {
                        string candidateHash = HashHelper.ComputeFileSha256(localRelMap[candidate].FullName);
                        if (!string.IsNullOrEmpty(missing.Sha256) && string.Equals(candidateHash, missing.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            detectedRenames[missing.OriginalRel] = candidate;
                            matchedNewRels.Add(candidate);
                            break;
                        }
                    }
                }
            }

            manifest.History ??= new List<ManifestHistoryEntry>();

            // 3. 处理已确认的重命名项
            foreach (var kv in detectedRenames)
            {
                string oldRel = kv.Key;
                string newRel = kv.Value;
                var missing = missingManifestEntries.FirstOrDefault(m => m.OriginalRel.Equals(oldRel, StringComparison.OrdinalIgnoreCase));
                if (missing != null)
                {
                    manifestFilesMap.Remove(oldRel);
                    manifest.History.Add(new ManifestHistoryEntry
                    {
                        HistoryId = Guid.NewGuid().ToString("N")[..12],
                        OriginalRel = missing.OriginalRel,
                        StoredRel = missing.StoredRel,
                        Size = missing.Size,
                        Sha256 = missing.Sha256,
                        ArchivedAt = DateTimeOffset.UtcNow,
                        Reason = "renamed",
                        RenamedTo = newRel,
                        IsLongName = missing.IsLongName,
                        Utf8Bytes = missing.Utf8Bytes,
                        StoredUtf8Bytes = missing.StoredUtf8Bytes
                    });
                    Log("INFO", $"[重命名检测] 识别到文件重命名: 【{oldRel}】 -> 【{newRel}】，已更新活动目录树并将旧版本转入历史追溯清单。");
                }
            }

            // 4. 处理已删除项 (未匹配重命名的缺失文件)
            var deletedEntries = missingManifestEntries.Where(m => !detectedRenames.ContainsKey(m.OriginalRel)).ToList();
            foreach (var deleted in deletedEntries)
            {
                if (strategy == BackupStrategy.Smart || strategy == BackupStrategy.Mirror)
                {
                    manifestFilesMap.Remove(deleted.OriginalRel);
                    manifest.History.Add(new ManifestHistoryEntry
                    {
                        HistoryId = Guid.NewGuid().ToString("N")[..12],
                        OriginalRel = deleted.OriginalRel,
                        StoredRel = deleted.StoredRel,
                        Size = deleted.Size,
                        Sha256 = deleted.Sha256,
                        ArchivedAt = DateTimeOffset.UtcNow,
                        Reason = "deleted",
                        RenamedTo = null,
                        IsLongName = deleted.IsLongName,
                        Utf8Bytes = deleted.Utf8Bytes,
                        StoredUtf8Bytes = deleted.StoredUtf8Bytes
                    });
                    Log("INFO", $"[删除追溯] 本地文件已移除: 【{deleted.OriginalRel}】，已从活动清单移除并转入历史记录。");
                }
                else
                {
                    Log("INFO", $"[归档保留] 本地文件已移除: 【{deleted.OriginalRel}】，按归档策略在清单与远端保留。");
                }
            }

            manifest.BackupPolicy = strategy.ToString();
            return detectedRenames;
        }

        /// <summary>
        /// 预先分析同步任务（统计待上传、需修改、未修改、超长文件名及体积总量）
        /// </summary>
        public async Task<SyncAnalysisResult> AnalyzeSyncAsync(string sourceFolder, string targetNasFolder, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            {
                throw new DirectoryNotFoundException($"本地源文件夹不存在: {sourceFolder}");
            }
            if (PathHelper.ArePathsSameOrNested(sourceFolder, targetNasFolder, out string nestReason))
            {
                throw new InvalidOperationException($"路径安全校验失败: {nestReason}");
            }

            var result = new SyncAnalysisResult();
            Manifest manifest = await _manifestManager.LoadManifestAsync(targetNasFolder, token).ConfigureAwait(false);
            _longNameEngine.LoadExistingManifest(manifest);

            var existingMap = new Dictionary<string, ManifestFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files)
            {
                existingMap[f.OriginalRel] = f;
            }

            string longSource = PathHelper.EnsureLongPathPrefix(sourceFolder);
            var dirInfo = new DirectoryInfo(longSource);
            var files = ScanLocalSourceFiles(dirInfo, longSource);

            result.TotalFiles = files.Count;

            foreach (var fi in files)
            {
                if (token.IsCancellationRequested) break;
                result.TotalSizeBytes += fi.Length;

                string origRel = PathHelper.NormalizeRelPath(Path.GetRelativePath(longSource, fi.FullName));
                var (storedRel, isLong) = _longNameEngine.TranslateFilePath(origRel);

                if (isLong)
                {
                    result.LongNameFiles++;
                    if (result.SampleLongNames.Count < 5)
                    {
                        result.SampleLongNames.Add($"{origRel} -> {storedRel}");
                    }
                }

                if (existingMap.TryGetValue(origRel, out var existing))
                {
                    if (existing.StoredRel.Equals(storedRel, StringComparison.OrdinalIgnoreCase) &&
                        existing.Size == fi.Length &&
                        Math.Abs(existing.ModTimeUtcTicks - fi.LastWriteTimeUtc.Ticks) < TimeSpan.TicksPerSecond * 2)
                    {
                        result.UnchangedFiles++;
                    }
                    else
                    {
                        result.ModifiedFiles++;
                    }
                }
                else
                {
                    result.NewFiles++;
                }
            }

            return result;
        }

        /// <summary>
        /// 启动或触发一次完整的增量同步
        /// </summary>
        public Task<SyncProgressReport> RunSyncAsync(string sourceFolder, string targetNasFolder, CancellationToken externalToken = default)
        {
            return RunSyncInternalAsync(sourceFolder, targetNasFolder, null, BackupStrategy.Smart, null, externalToken);
        }

        /// <summary>
        /// 启动或触发一次完整的增量同步（支持传入 NAS 统一存储提供程序）
        /// </summary>
        public Task<SyncProgressReport> RunSyncAsync(
            string sourceFolder, 
            string targetNasFolder, 
            INasStorageProvider? storageProvider, 
            CancellationToken externalToken)
        {
            return RunSyncInternalAsync(sourceFolder, targetNasFolder, storageProvider, BackupStrategy.Smart, null, externalToken);
        }

        /// <summary>
        /// 启动或触发一次完整的增量同步（支持传入存储提供程序、备份策略与重命名提示）
        /// </summary>
        public Task<SyncProgressReport> RunSyncAsync(
            string sourceFolder, 
            string targetNasFolder, 
            INasStorageProvider? storageProvider,
            BackupStrategy strategy,
            IDictionary<string, string>? pendingRenames,
            CancellationToken externalToken)
        {
            return RunSyncInternalAsync(sourceFolder, targetNasFolder, storageProvider, strategy, pendingRenames, externalToken);
        }

        private async Task<SyncProgressReport> RunSyncInternalAsync(
            string sourceFolder,
            string targetNasFolder,
            INasStorageProvider? storageProvider,
            BackupStrategy strategy,
            IDictionary<string, string>? pendingRenames,
            CancellationToken externalToken)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            {
                throw new DirectoryNotFoundException($"本地源文件夹不存在: {sourceFolder}");
            }
            if (string.IsNullOrWhiteSpace(targetNasFolder))
            {
                throw new ArgumentException("目标 NAS 路径不能为空", nameof(targetNasFolder));
            }

            // 1. 严格检查源路径与目标路径是否相同或相互嵌套
            if (PathHelper.ArePathsSameOrNested(sourceFolder, targetNasFolder, out string nestReason))
            {
                Log("ERROR", nestReason);
                throw new InvalidOperationException(nestReason);
            }

            // 2. 并发防重入控制：确保手动与自动同步串行执行
            if (!await _concurrencyLock.WaitAsync(0, externalToken).ConfigureAwait(false))
            {
                Log("INFO", "已有同步任务正在执行中，本次请求已安全忽略。");
                return new SyncProgressReport { State = SyncState.Syncing, StatusMessage = "已有同步正在执行" };
            }

            ManifestLockHandle? targetLock = null;

            try
            {
                _syncCts?.Cancel();
                _syncCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
                var token = _syncCts.Token;

                // 若指定了飞牛官方原生个人空间提供程序，执行原生流式同步通道
                if (storageProvider != null && storageProvider.ProviderType == StorageProviderType.FnOsPersonal)
                {
                    return await RunSyncWithStorageProviderAsync(sourceFolder, targetNasFolder, storageProvider, strategy, pendingRenames, token).ConfigureAwait(false);
                }

                var report = new SyncProgressReport { State = SyncState.Scanning };
                CurrentState = SyncState.Scanning;
                Log("INFO", $"开始增量同步: 本地【{sourceFolder}】 -> NAS【{targetNasFolder}】 (策略: {strategy})");

                string longSource = PathHelper.EnsureLongPathPrefix(sourceFolder);
                string longTarget = PathHelper.EnsureLongPathPrefix(targetNasFolder);

                // 3. 验证或创建 NAS 目标根目录及 .fnsync 专用隔离目录
                if (!Directory.Exists(longTarget))
                {
                    try
                    {
                        Directory.CreateDirectory(longTarget);
                    }
                    catch (Exception ex)
                    {
                        CurrentState = SyncState.Failed;
                        Log("ERROR", $"无法创建 NAS 目标目录: {ex.Message}");
                        throw;
                    }
                }

                // 4. 尝试获取目标目录文件排他锁
                targetLock = await _manifestManager.TryAcquireTargetLockAsync(targetNasFolder).ConfigureAwait(false);
                if (targetLock == null)
                {
                    Log("WARN", "目标 NAS 目录已有其他同步实例在写入或处于锁定状态，将继续进行常规写入。");
                }

                // 清理超过 1 小时的历史残留 staging 文件
                CleanupStaleStagingFiles(targetNasFolder);

                // 5. 加载 NAS 隔离元数据清单 (.fnsync/manifest.json)
                Log("INFO", "正在加载 NAS 备份元数据清单 (.fnsync/manifest.json)...");
                Manifest manifest = await _manifestManager.LoadManifestAsync(targetNasFolder, token).ConfigureAwait(false);
                manifest.SourceRoot = PathHelper.RemoveLongPathPrefix(sourceFolder);
                manifest.TargetRoot = PathHelper.RemoveLongPathPrefix(targetNasFolder);
                _longNameEngine.LoadExistingManifest(manifest);

                // 维护全量文件清单映射字典：无论同步是否中断或某个文件失败，历史成功项决不丢失！
                var manifestFilesMap = new Dictionary<string, ManifestFileEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in manifest.Files)
                {
                    manifestFilesMap[f.OriginalRel] = f;
                }

                // 6. 扫描本地源文件集合，严格跳过内部元数据、临时文件与隐藏系统文件
                var allLocalFiles = new List<FileInfo>();
                try
                {
                    var dirInfo = new DirectoryInfo(longSource);
                    allLocalFiles = ScanLocalSourceFiles(dirInfo, longSource);
                }
                catch (Exception ex)
                {
                    CurrentState = SyncState.Failed;
                    Log("ERROR", $"扫描本地目录失败: {ex.Message}");
                    throw;
                }

                // 执行重命名分析与删除追踪
                var detectedRenames = DetectRenamesAndProcessDeletions(longSource, allLocalFiles, manifest, manifestFilesMap, strategy, pendingRenames);

                report.TotalFiles = allLocalFiles.Count;
                Log("INFO", $"本地扫描完成，共发现 {report.TotalFiles} 个有效源文件，开始增量比对...");

                CurrentState = SyncState.Syncing;
                report.State = SyncState.Syncing;

                var stopwatch = Stopwatch.StartNew();
                long totalBytesTransferred = 0;
                int uploadedCount = 0;
                int unchangedCount = 0;
                int longNameCount = 0;
                int failedCount = 0;

                string stagingDir = ManifestManager.GetStagingDirectory(targetNasFolder);
                if (!Directory.Exists(stagingDir))
                {
                    try { Directory.CreateDirectory(stagingDir); } catch { }
                }

                for (int i = 0; i < allLocalFiles.Count; i++)
                {
                    if (token.IsCancellationRequested)
                    {
                        Log("WARN", "同步任务已被用户取消。");
                        CurrentState = SyncState.Paused;
                        break;
                    }

                    var localFi = allLocalFiles[i];
                    string localFull = localFi.FullName;
                    string originalRel = PathHelper.NormalizeRelPath(Path.GetRelativePath(longSource, localFull));
                    report.CurrentFile = originalRel;
                    report.ScannedFiles = i + 1;

                    // 检查长文件名转换
                    var (storedRel, isLong) = _longNameEngine.TranslateFilePath(originalRel);
                    if (isLong)
                    {
                        longNameCount++;
                        Log("INFO", $"[长文件名无损映射] 原始: {originalRel} -> 映射为: {storedRel}");
                    }

                    string targetFull = Path.Combine(longTarget, storedRel.Replace('/', '\\'));
                    string targetDir = Path.GetDirectoryName(targetFull)!;

                    // 增量判定：大小 + 修改时间
                    bool isUnchanged = false;
                    if (manifestFilesMap.TryGetValue(originalRel, out var existingEntry))
                    {
                        if (existingEntry.StoredRel.Equals(storedRel, StringComparison.OrdinalIgnoreCase) &&
                            existingEntry.Size == localFi.Length &&
                            Math.Abs(existingEntry.ModTimeUtcTicks - localFi.LastWriteTimeUtc.Ticks) < TimeSpan.TicksPerSecond * 2 &&
                            File.Exists(targetFull))
                        {
                            isUnchanged = true;
                        }
                    }

                    if (isUnchanged)
                    {
                        unchangedCount++;
                        report.UnchangedFiles = unchangedCount;
                        report.LongNameFiles = longNameCount;
                        ProgressChanged?.Invoke(report);
                        continue;
                    }

                    // 上传文件：使用短固定长度 staging 临时文件，避免长文件名后缀超过 255 字节引发 1018 报错！
                    bool uploadSuccess = false;
                    string uploadError = "";

                    // 防写抖动安全检查：确保文件未被独占锁定且内容已完整写入
                    await WaitForFileSettlingAsync(localFull, 3, token).ConfigureAwait(false);

                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        // 产生极短的随机临时名 (仅 18~20 字符)，存放于 .fnsync/staging/
                        string stagingFile = Path.Combine(stagingDir, $"stg_{Guid.NewGuid():N}.tmp");

                        try
                        {
                            if (!Directory.Exists(targetDir))
                            {
                                Directory.CreateDirectory(targetDir);
                            }

                            string computedHash = "";
                            long bytesUploaded = 0;

                            // 尝试以共享读模式打开本地文件，捕获文件被独占占用的异常
                            FileStream srcStream;
                            try
                            {
                                srcStream = new FileStream(localFull, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, useAsync: true);
                            }
                            catch (IOException ioEx)
                            {
                                throw new IOException($"[文件被占用] 无法读取正在被其他程序编辑或锁定的文件: {ioEx.Message}", ioEx);
                            }

                            using (srcStream)
                            using (var destStream = new FileStream(stagingFile, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
                            using (var sha = SHA256.Create())
                            {
                                byte[] buffer = new byte[128 * 1024];
                                int read;
                                while ((read = await srcStream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                                {
                                    await destStream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                                    sha.TransformBlock(buffer, 0, read, null, 0);
                                    bytesUploaded += read;
                                    totalBytesTransferred += read;

                                    double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                                    report.BytesTransferred = totalBytesTransferred;
                                    report.SpeedBytesPerSec = elapsedSec > 0 ? totalBytesTransferred / elapsedSec : 0;
                                }
                                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                                computedHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                                await destStream.FlushAsync(token).ConfigureAwait(false);
                            }

                            // 验证大小
                            var stagingFi = new FileInfo(stagingFile);
                            if (stagingFi.Length != localFi.Length)
                            {
                                throw new IOException($"文件上传不完整: 预期 {localFi.Length} 字节, 实际 {stagingFi.Length} 字节");
                            }

                            // 同文件系统内原子移动覆盖
                            File.Move(stagingFile, targetFull, overwrite: true);

                            // 更新清单字典中的项
                            string? prevRel = detectedRenames.FirstOrDefault(x => x.Value.Equals(originalRel, StringComparison.OrdinalIgnoreCase)).Key;
                            var newEntry = new ManifestFileEntry
                            {
                                OriginalRel = originalRel,
                                StoredRel = storedRel,
                                Size = localFi.Length,
                                ModTimeUtcTicks = localFi.LastWriteTimeUtc.Ticks,
                                ModTimeIso = localFi.LastWriteTimeUtc,
                                Sha256 = computedHash,
                                IsLongName = isLong,
                                Utf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(originalRel)),
                                StoredUtf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(storedRel)),
                                IsActive = true,
                                PreviousRel = prevRel
                            };
                            manifestFilesMap[originalRel] = newEntry;

                            uploadedCount++;
                            uploadSuccess = true;
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            // 清理临时文件
                            try { if (File.Exists(stagingFile)) File.Delete(stagingFile); } catch { }
                            break;
                        }
                        catch (Exception ex)
                        {
                            uploadError = ex.Message;
                            try { if (File.Exists(stagingFile)) File.Delete(stagingFile); } catch { }

                            if (!IsTransientException(ex))
                            {
                                Log("ERROR", $"[不可重试异常] 同步 {originalRel} 遇到致命错误，立即终止重试: {ex.Message}");
                                break;
                            }

                            Log("WARN", $"[重试 {attempt}/3] 同步 {originalRel} 异常: {ex.Message}");
                            if (attempt < 3)
                            {
                                await Task.Delay(1000 * attempt, token).ConfigureAwait(false);
                            }
                        }
                    }

                    if (uploadSuccess)
                    {
                        report.UploadedFiles = uploadedCount;
                    }
                    else if (!token.IsCancellationRequested)
                    {
                        failedCount++;
                        report.FailedFiles = failedCount;
                        FailedItems.Add(new FailedSyncItem
                        {
                            RelativePath = originalRel,
                            ErrorMessage = uploadError,
                            FailedTime = DateTime.Now
                        });
                        Log("ERROR", $"[同步失败] {originalRel} : {uploadError}");
                    }

                    report.UnchangedFiles = unchangedCount;
                    report.LongNameFiles = longNameCount;
                    ProgressChanged?.Invoke(report);

                    // 定期（每 20 个文件）保存清单，防止意外中断
                    if ((uploadedCount > 0 && uploadedCount % 20 == 0) || i == allLocalFiles.Count - 1)
                    {
                        manifest.Files = manifestFilesMap.Values.ToList();
                        manifest.BackupPolicy = strategy.ToString();
                        await _manifestManager.SaveManifestAtomicAsync(targetNasFolder, manifest, token).ConfigureAwait(false);
                    }
                }

                // 7. 保存目录结构信息与最终完整清单
                try
                {
                    var dirList = new List<ManifestDirEntry>();
                    var srcDir = new DirectoryInfo(longSource);
                    foreach (var d in srcDir.EnumerateDirectories("*", SearchOption.AllDirectories))
                    {
                        string relDir = PathHelper.NormalizeRelPath(Path.GetRelativePath(longSource, d.FullName));
                        if (ShouldIgnoreRelativeDir(relDir)) continue;

                        var (storedDir, isLongDir) = _longNameEngine.TranslateDirPath(relDir);
                        dirList.Add(new ManifestDirEntry
                        {
                            OriginalRel = relDir,
                            StoredRel = storedDir,
                            IsLongName = isLongDir,
                            Utf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(relDir)),
                            StoredUtf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(storedDir))
                        });
                    }
                    manifest.Directories = dirList;
                    manifest.Files = manifestFilesMap.Values.ToList();
                    manifest.BackupPolicy = strategy.ToString();
                    await _manifestManager.SaveManifestAtomicAsync(targetNasFolder, manifest, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    CurrentState = SyncState.Failed;
                    Log("ERROR", $"保存最终清单失败: {ex.Message}");
                    throw;
                }

                CurrentState = failedCount > 0 ? SyncState.Failed : SyncState.Completed;
                report.State = CurrentState;
                report.StatusMessage = $"同步已完成！已上传: {uploadedCount}, 跳过未修改: {unchangedCount}, 长文件名映射: {longNameCount}, 失败: {failedCount}";
                Log("INFO", report.StatusMessage);
                ProgressChanged?.Invoke(report);

                return report;
            }
            finally
            {
                targetLock?.Dispose();
                _concurrencyLock.Release();
            }
        }

        private async Task<SyncProgressReport> RunSyncWithStorageProviderAsync(
            string sourceFolder,
            string targetNasFolder,
            INasStorageProvider storageProvider,
            BackupStrategy strategy,
            IDictionary<string, string>? pendingRenames,
            CancellationToken token)
        {
            var report = new SyncProgressReport { State = SyncState.Scanning };
            CurrentState = SyncState.Scanning;
            Log("INFO", $"开始飞牛原生个人空间增量同步: 本地【{sourceFolder}】 -> 飞牛「我的文件」【{targetNasFolder}】 (策略: {strategy})");

            string longSource = PathHelper.EnsureLongPathPrefix(sourceFolder);

            // 1. 先扫描本地源文件并确定总数与扫描指标
            var dirInfo = new DirectoryInfo(longSource);
            List<FileInfo> allLocalFiles;
            try
            {
                allLocalFiles = ScanLocalSourceFiles(dirInfo, longSource);
                report.TotalFiles = allLocalFiles.Count;
                _lastScannedCount = allLocalFiles.Count;
                Log("INFO", $"本地扫描完成，共发现 {report.TotalFiles} 个有效源文件，准备校验个人空间目标...");
            }
            catch (Exception ex)
            {
                CurrentState = SyncState.Failed;
                report.State = SyncState.Failed;
                report.StatusMessage = $"扫描本地源文件失败: {ex.Message}";
                Log("ERROR", report.StatusMessage);
                throw new IOException(report.StatusMessage, ex);
            }

            // 2. 确保远端目标根目录存在 (幂等检查与创建)
            var mkdirRes = await storageProvider.CreateDirectoryAsync(targetNasFolder, token).ConfigureAwait(false);
            if (!mkdirRes.Success && mkdirRes.ErrorCode != 17)
            {
                CurrentState = SyncState.Failed;
                report.State = SyncState.Failed;
                report.FailedFiles = Math.Max(1, report.TotalFiles);
                report.StatusMessage = $"无法在飞牛个人空间创建目标目录: {mkdirRes.ErrorMessage}";
                Log("ERROR", report.StatusMessage);
                throw new IOException(report.StatusMessage);
            }

            // 3. 加载 NAS 隔离元数据清单 (.fnsync/manifest.json)
            Log("INFO", "正在加载飞牛 NAS 备份元数据清单 (.fnsync/manifest.json)...");
            Manifest manifest;
            try
            {
                manifest = await _manifestManager.LoadManifestAsync(storageProvider, targetNasFolder, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CurrentState = SyncState.Failed;
                report.State = SyncState.Failed;
                report.FailedFiles = Math.Max(1, report.TotalFiles);
                report.StatusMessage = $"加载远端元数据清单失败: {ex.Message}";
                Log("ERROR", report.StatusMessage);
                throw new IOException(report.StatusMessage, ex);
            }
            if (storageProvider is FNSyncPlus.Core.Storage.FnOsPersonalStorageProvider && _longNameEngine.MaxFileUtf8Bytes > LongNameEngine.FnOsUploadMaxUtf8Bytes)
            {
                _longNameEngine = LongNameEngine.CreateForProvider(storageProvider);
            }
            manifest.SourceRoot = PathHelper.RemoveLongPathPrefix(sourceFolder);
            manifest.TargetRoot = targetNasFolder;
            _longNameEngine.LoadExistingManifest(manifest);

            var manifestFilesMap = new Dictionary<string, ManifestFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files)
            {
                manifestFilesMap[f.OriginalRel] = f;
            }

            // 执行重命名分析与删除追踪
            var detectedRenames = DetectRenamesAndProcessDeletions(longSource, allLocalFiles, manifest, manifestFilesMap, strategy, pendingRenames);

            CurrentState = SyncState.Syncing;
            report.State = SyncState.Syncing;

            var stopwatch = Stopwatch.StartNew();
            long totalBytesTransferred = 0;
            int uploadedCount = 0;
            int unchangedCount = 0;
            int longNameCount = 0;
            int failedCount = 0;

            for (int i = 0; i < allLocalFiles.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    Log("WARN", "同步任务已被用户取消。");
                    CurrentState = SyncState.Paused;
                    break;
                }

                var localFi = allLocalFiles[i];
                string localFull = localFi.FullName;
                string originalRel = PathHelper.NormalizeRelPath(Path.GetRelativePath(longSource, localFull));
                report.CurrentFile = originalRel;
                report.ScannedFiles = i + 1;

                var (storedRel, isLong) = _longNameEngine.TranslateFilePath(originalRel);
                if (isLong)
                {
                    longNameCount++;
                    Log("INFO", $"[长文件名无损映射] 原始: {originalRel} -> 映射为: {storedRel}");
                }

                // 增量判定：大小 + 修改时间
                bool isUnchanged = false;
                if (manifestFilesMap.TryGetValue(originalRel, out var existingEntry))
                {
                    if (existingEntry.StoredRel.Equals(storedRel, StringComparison.OrdinalIgnoreCase) &&
                        existingEntry.Size == localFi.Length &&
                        Math.Abs(existingEntry.ModTimeUtcTicks - localFi.LastWriteTimeUtc.Ticks) < TimeSpan.TicksPerSecond * 2)
                    {
                        isUnchanged = true;
                    }
                }

                if (isUnchanged)
                {
                    unchangedCount++;
                    report.UnchangedFiles = unchangedCount;
                    report.LongNameFiles = longNameCount;
                    ProgressChanged?.Invoke(report);
                    continue;
                }

                // 真正的流式上传
                string remoteRel = string.IsNullOrEmpty(targetNasFolder)
                    ? storedRel
                    : $"{targetNasFolder.TrimEnd('/', '\\')}/{storedRel}";

                string remoteDir = Path.GetDirectoryName(remoteRel.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
                if (!string.IsNullOrEmpty(remoteDir))
                {
                    try
                    {
                        var dirRes = await storageProvider.CreateDirectoryAsync(remoteDir, token).ConfigureAwait(false);
                        if (!dirRes.Success && dirRes.ErrorCode != 17)
                        {
                            throw new IOException($"创建远端子目录失败: {dirRes.ErrorMessage}");
                        }
                    }
                    catch (Exception dirEx)
                    {
                        failedCount++;
                        report.FailedFiles = failedCount;
                        FailedItems.Add(new FailedSyncItem
                        {
                            RelativePath = originalRel,
                            ErrorMessage = dirEx.Message
                        });
                        Log("ERROR", $"[同步失败] 创建父目录异常 {remoteDir} : {dirEx.Message}");
                        report.UnchangedFiles = unchangedCount;
                        report.LongNameFiles = longNameCount;
                        ProgressChanged?.Invoke(report);
                        continue;
                    }
                }

                bool uploadSuccess = false;
                string uploadError = "";

                // 防写抖动安全检查：确保文件未被独占锁定且内容已完整写入
                await WaitForFileSettlingAsync(localFull, 3, token).ConfigureAwait(false);

                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        FileStream srcStream;
                        try
                        {
                            srcStream = new FileStream(localFull, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
                        }
                        catch (IOException ioEx)
                        {
                            throw new IOException($"[文件被占用] 无法读取正在被其他程序编辑或锁定的文件: {ioEx.Message}", ioEx);
                        }

                        using (srcStream)
                        {
                            var uploadRes = await storageProvider.UploadFileAsync(
                                remoteRel,
                                srcStream,
                                localFi.Length,
                                localFi.LastWriteTimeUtc,
                                overwrite: true,
                                new Progress<long>(bytesCopied =>
                                {
                                    totalBytesTransferred += bytesCopied;
                                    double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                                    report.BytesTransferred = totalBytesTransferred;
                                    report.SpeedBytesPerSec = elapsedSec > 0 ? totalBytesTransferred / elapsedSec : 0;
                                    ProgressChanged?.Invoke(report);
                                }),
                                token).ConfigureAwait(false);

                            if (!uploadRes.Success)
                            {
                                throw new IOException(uploadRes.ErrorMessage);
                            }
                        }

                        // 计算 SHA-256 并更新清单字典
                        string sha256 = HashHelper.ComputeFileSha256(localFull);
                        string? prevRel = detectedRenames.FirstOrDefault(x => x.Value.Equals(originalRel, StringComparison.OrdinalIgnoreCase)).Key;
                        var newEntry = new ManifestFileEntry
                        {
                            OriginalRel = originalRel,
                            StoredRel = storedRel,
                            Size = localFi.Length,
                            ModTimeUtcTicks = localFi.LastWriteTimeUtc.Ticks,
                            ModTimeIso = localFi.LastWriteTimeUtc,
                            Sha256 = sha256,
                            IsLongName = isLong,
                            Utf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(originalRel)),
                            StoredUtf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(storedRel)),
                            IsActive = true,
                            PreviousRel = prevRel
                        };
                        manifestFilesMap[originalRel] = newEntry;

                        uploadedCount++;
                        uploadSuccess = true;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        uploadError = ex.Message;
                        if (!IsTransientException(ex))
                        {
                            Log("ERROR", $"[不可重试异常] 同步 {originalRel} 遇到致命错误，立即终止重试: {ex.Message}");
                            break;
                        }

                        Log("WARN", $"[重试 {attempt}/3] 个人空间上传 {originalRel} 异常: {ex.Message}");
                        if (attempt < 3)
                        {
                            await Task.Delay(1000 * attempt, token).ConfigureAwait(false);
                        }
                    }
                }

                if (!uploadSuccess && !token.IsCancellationRequested)
                {
                    failedCount++;
                    report.FailedFiles = failedCount;
                    FailedItems.Add(new FailedSyncItem
                    {
                        RelativePath = originalRel,
                        ErrorMessage = uploadError
                    });
                    Log("ERROR", $"[同步失败] {originalRel} : {uploadError}");
                }

                report.UploadedFiles = uploadedCount;
                report.UnchangedFiles = unchangedCount;
                report.LongNameFiles = longNameCount;
                ProgressChanged?.Invoke(report);

                if ((uploadedCount > 0 && uploadedCount % 20 == 0) || i == allLocalFiles.Count - 1)
                {
                    manifest.Files = manifestFilesMap.Values.ToList();
                    manifest.BackupPolicy = strategy.ToString();
                    await _manifestManager.SaveManifestAtomicAsync(storageProvider, targetNasFolder, manifest, token).ConfigureAwait(false);
                }
            }

            // 保存目录清单与最终完整清单
            try
            {
                var dirList = new List<ManifestDirEntry>();
                var srcDir = new DirectoryInfo(longSource);
                foreach (var d in srcDir.EnumerateDirectories("*", SearchOption.AllDirectories))
                {
                    string relDir = PathHelper.NormalizeRelPath(Path.GetRelativePath(longSource, d.FullName));
                    if (ShouldIgnoreRelativeDir(relDir)) continue;

                    var (storedDir, isLongDir) = _longNameEngine.TranslateDirPath(relDir);
                    dirList.Add(new ManifestDirEntry
                    {
                        OriginalRel = relDir,
                        StoredRel = storedDir,
                        IsLongName = isLongDir,
                        Utf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(relDir)),
                        StoredUtf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(storedDir))
                    });
                }
                manifest.Directories = dirList;
                manifest.Files = manifestFilesMap.Values.ToList();
                manifest.BackupPolicy = strategy.ToString();
                await _manifestManager.SaveManifestAtomicAsync(storageProvider, targetNasFolder, manifest, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CurrentState = SyncState.Failed;
                Log("ERROR", $"持久化最终清单失败: {ex.Message}");
                throw;
            }

            CurrentState = failedCount > 0 ? SyncState.Failed : SyncState.Completed;
            report.State = CurrentState;
            report.StatusMessage = $"飞牛个人空间同步完成！已上传: {uploadedCount}, 跳过未修改: {unchangedCount}, 长文件名映射: {longNameCount}, 失败: {failedCount}";
            Log("INFO", report.StatusMessage);
            ProgressChanged?.Invoke(report);

            return report;
        }

        private static List<FileInfo> ScanLocalSourceFiles(DirectoryInfo rootDir, string longSource)
        {
            var result = new List<FileInfo>();
            foreach (var fi in rootDir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string rel = PathHelper.NormalizeRelPath(Path.GetRelativePath(longSource, fi.FullName));
                if (ShouldIgnoreRelativeFile(fi.Name, rel)) continue;
                result.Add(fi);
            }
            return result;
        }

        private static bool ShouldIgnoreRelativeFile(string fileName, string relativePath)
        {
            // 忽略内部文件、临时文件、系统保留与元数据
            if (fileName.StartsWith("~$") ||
                fileName.StartsWith(".~") ||
                fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".fn_part_") ||
                fileName.Contains(".restore_tmp_") ||
                fileName.Equals(Manifest.IsolatedManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(Manifest.LegacyManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(Manifest.LegacyGoManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (relativePath.StartsWith(Manifest.MetadataDirName + "/", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(".svn/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool ShouldIgnoreRelativeDir(string relativePath)
        {
            if (relativePath.Equals(Manifest.MetadataDirName, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(Manifest.MetadataDirName + "/", StringComparison.OrdinalIgnoreCase) ||
                relativePath.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
                relativePath.Equals(".svn", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(".svn/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private static void CleanupStaleStagingFiles(string targetNasFolder)
        {
            try
            {
                string stagingDir = ManifestManager.GetStagingDirectory(targetNasFolder);
                if (Directory.Exists(stagingDir))
                {
                    var di = new DirectoryInfo(stagingDir);
                    var cutoff = DateTime.UtcNow.AddHours(-1);
                    foreach (var file in di.EnumerateFiles("*.tmp"))
                    {
                        if (file.LastWriteTimeUtc < cutoff)
                        {
                            try { file.Delete(); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public void PauseOrCancel()
        {
            _syncCts?.Cancel();
            CurrentState = SyncState.Paused;
            Log("INFO", "同步操作已请求暂停/取消。");
        }

        #region 实时监控 FileSystemWatcher

        public void StartWatcher(string sourceFolder, string targetNasFolder)
        {
            StopWatcher();
            if (!Directory.Exists(sourceFolder)) return;

            try
            {
                _watcher = new FileSystemWatcher(sourceFolder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };

                _watcher.Changed += (s, e) => QueueWatcherEvent(e.FullPath);
                _watcher.Created += (s, e) => QueueWatcherEvent(e.FullPath);
                _watcher.Renamed += (s, e) => QueueWatcherEvent(e.FullPath);

                // 缓冲区溢出保护：记录日志并触发全量重扫
                _watcher.Error += (s, e) =>
                {
                    Log("WARN", $"[文件监控缓冲区溢出] 变动频率过高 ({e.GetException().Message})，已自动排队全量重新扫描。");
                    QueueWatcherEvent(sourceFolder);
                };

                _watcher.EnableRaisingEvents = true;
                Log("INFO", "已开启本地文件夹实时变动自动监测");
            }
            catch (Exception ex)
            {
                Log("WARN", $"无法启动文件监视器: {ex.Message}");
            }
        }

        public void StopWatcher()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        private void QueueWatcherEvent(string fullPath)
        {
            _pendingWatcherEvents[fullPath] = DateTime.Now;
            _watcherDebounceTimer.Change(2000, Timeout.Infinite);
        }

        private void OnWatcherDebounceElapsed(object? state)
        {
            if (CurrentState == SyncState.Syncing || CurrentState == SyncState.Scanning) return;
            Log("INFO", "检测到本地文件变动，正在触发自动增量同步...");
            RequestWatcherTriggeredSync?.Invoke();
        }

        #endregion
    }
}
