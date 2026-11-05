using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services
{
    public enum SyncState
    {
        Idle,
        Scanning,
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

    public class FailedSyncItem
    {
        public string RelativePath { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public DateTime FailedTime { get; set; } = DateTime.Now;
        public int RetryCount { get; set; }
    }

    public class SyncEngine
    {
        private readonly LongNameEngine _longNameEngine;
        private readonly ManifestManager _manifestManager;
        private readonly SmbAuthService _smbService;

        private CancellationTokenSource? _syncCts;
        private FileSystemWatcher? _watcher;
        private readonly ConcurrentDictionary<string, DateTime> _pendingWatcherEvents = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _watcherDebounceTimer;

        private SyncState _currentState = SyncState.Idle;
        private readonly object _stateLock = new();

        public event Action<SyncProgressReport>? ProgressChanged;
        public event Action<string, string>? LogEmitted; // (level, message)
        public event Action<SyncState, string>? StateChanged;

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
            SyncState.Syncing => "正在同步备份到 NAS...",
            SyncState.Paused => "已暂停",
            SyncState.OfflineWaiting => "NAS 离线，等待网络恢复...",
            SyncState.Completed => "同步完成",
            SyncState.Failed => "同步出错中断",
            _ => state.ToString()
        };

        private void Log(string level, string message)
        {
            LogEmitted?.Invoke(level, $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        }

        /// <summary>
        /// 启动或触发一次完整的增量同步
        /// </summary>
        public async Task<SyncProgressReport> RunSyncAsync(string sourceFolder, string targetNasFolder, CancellationToken externalToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            {
                throw new DirectoryNotFoundException($"本地源文件夹不存在: {sourceFolder}");
            }
            if (string.IsNullOrWhiteSpace(targetNasFolder))
            {
                throw new ArgumentException("目标 NAS 路径不能为空", nameof(targetNasFolder));
            }

            _syncCts?.Cancel();
            _syncCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            var token = _syncCts.Token;

            var report = new SyncProgressReport { State = SyncState.Scanning };
            CurrentState = SyncState.Scanning;
            Log("INFO", $"开始扫描任务: 本地【{sourceFolder}】 -> NAS【{targetNasFolder}】");

            string longSource = PathHelper.EnsureLongPathPrefix(sourceFolder);
            string longTarget = PathHelper.EnsureLongPathPrefix(targetNasFolder);

            // 1. 验证或创建 NAS 目标根目录
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

            // 2. 加载目标 NAS 上的已有元数据清单与长文件名映射历史
            Log("INFO", "正在加载 NAS 备份元数据清单...");
            Manifest manifest = await _manifestManager.LoadManifestAsync(targetNasFolder, token).ConfigureAwait(false);
            manifest.SourceRoot = PathHelper.RemoveLongPathPrefix(sourceFolder);
            manifest.TargetRoot = PathHelper.RemoveLongPathPrefix(targetNasFolder);
            _longNameEngine.LoadExistingManifest(manifest);

            var existingFilesMap = new Dictionary<string, ManifestFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files)
            {
                existingFilesMap[f.OriginalRel] = f;
            }

            // 3. 收集所有待同步文件
            var allLocalFiles = new List<FileInfo>();
            try
            {
                var dirInfo = new DirectoryInfo(longSource);
                foreach (var fi in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    // 忽略临时文件、回收站、git 等
                    if (fi.Name.StartsWith("~$") || fi.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
                        continue;
                    allLocalFiles.Add(fi);
                }
            }
            catch (Exception ex)
            {
                CurrentState = SyncState.Failed;
                Log("ERROR", $"扫描本地目录失败: {ex.Message}");
                throw;
            }

            report.TotalFiles = allLocalFiles.Count;
            Log("INFO", $"扫描完成，本地共发现 {report.TotalFiles} 个文件，开始增量比对...");

            CurrentState = SyncState.Syncing;
            report.State = SyncState.Syncing;

            var stopwatch = Stopwatch.StartNew();
            long totalBytesTransferred = 0;
            int uploadedCount = 0;
            int unchangedCount = 0;
            int longNameCount = 0;
            int failedCount = 0;

            var updatedManifestFiles = new List<ManifestFileEntry>();
            var updatedManifestDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                // 检查映射
                var (storedRel, isLong) = _longNameEngine.TranslateFilePath(originalRel);
                if (isLong)
                {
                    longNameCount++;
                    Log("INFO", $"[长文件名自动映射] 原始: {originalRel} -> 映射为: {storedRel}");
                }

                string targetFull = Path.Combine(longTarget, storedRel.Replace('/', '\\'));
                string targetDir = Path.GetDirectoryName(targetFull)!;

                // 增量判定：对比大小和修改时间
                bool isUnchanged = false;
                if (existingFilesMap.TryGetValue(originalRel, out var existingEntry))
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
                    updatedManifestFiles.Add(existingEntry!);
                    report.UnchangedFiles = unchangedCount;
                    report.LongNameFiles = longNameCount;
                    ProgressChanged?.Invoke(report);
                    continue;
                }

                // 需要上传该文件（新文件或已修改）
                bool uploadSuccess = false;
                string uploadError = "";

                // 重试机制（最多重试 3 次）
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        // 确保目标父目录在 NAS 上存在
                        if (!Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        // 执行可靠的原子写入：先写临时文件 .fn_part_xxx，校验完成后原子替换
                        string partFile = targetFull + ".fn_part_" + Guid.NewGuid().ToString("N");
                        string computedHash = "";
                        long bytesUploaded = 0;

                        using (var srcStream = new FileStream(localFull, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, useAsync: true))
                        using (var destStream = new FileStream(partFile, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
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

                                // 更新速度与传输进度
                                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                                report.BytesTransferred = totalBytesTransferred;
                                report.SpeedBytesPerSec = elapsedSec > 0 ? totalBytesTransferred / elapsedSec : 0;
                            }
                            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            computedHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                            await destStream.FlushAsync(token).ConfigureAwait(false);
                        }

                        // 检验文件大小
                        var partFi = new FileInfo(partFile);
                        if (partFi.Length != localFi.Length)
                        {
                            throw new IOException($"文件上传不完整: 预期 {localFi.Length} 字节, 实际 {partFi.Length} 字节");
                        }

                        // 原子移动覆盖为目标文件名
                        File.Move(partFile, targetFull, overwrite: true);

                        // 记录新元数据
                        var newEntry = new ManifestFileEntry
                        {
                            OriginalRel = originalRel,
                            StoredRel = storedRel,
                            Size = localFi.Length,
                            ModTimeUtcTicks = localFi.LastWriteTimeUtc.Ticks,
                            ModTimeIso = localFi.LastWriteTimeUtc,
                            Sha256 = computedHash,
                            IsLongName = isLong
                        };
                        updatedManifestFiles.Add(newEntry);
                        existingFilesMap[originalRel] = newEntry;

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
                        Log("WARN", $"[重试 {attempt}/3] 上传 {originalRel} 失败: {ex.Message}");
                        await Task.Delay(1000 * attempt, token).ConfigureAwait(false);
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

                // 每同步 20 个文件，将清单落盘一次，保证突发断电时进度不丢失
                if ((uploadedCount > 0 && uploadedCount % 20 == 0) || i == allLocalFiles.Count - 1)
                {
                    manifest.Files = updatedManifestFiles;
                    await _manifestManager.SaveManifestAtomicAsync(targetNasFolder, manifest, token).ConfigureAwait(false);
                }
            }

            // 同步目录信息记录
            try
            {
                var dirList = new List<ManifestDirEntry>();
                var srcDir = new DirectoryInfo(longSource);
                foreach (var d in srcDir.EnumerateDirectories("*", SearchOption.AllDirectories))
                {
                    string relDir = PathHelper.NormalizeRelPath(Path.GetRelativePath(longSource, d.FullName));
                    var (storedDir, isLongDir) = _longNameEngine.TranslateDirPath(relDir);
                    dirList.Add(new ManifestDirEntry
                    {
                        OriginalRel = relDir,
                        StoredRel = storedDir,
                        IsLongName = isLongDir
                    });
                }
                manifest.Directories = dirList;
                manifest.Files = updatedManifestFiles;
                await _manifestManager.SaveManifestAtomicAsync(targetNasFolder, manifest, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("WARN", $"保存目录清单时出现提示: {ex.Message}");
            }

            CurrentState = failedCount > 0 ? SyncState.Completed : SyncState.Completed;
            report.State = CurrentState;
            report.StatusMessage = $"同步已完成！成功上传: {uploadedCount}, 跳过未修改: {unchangedCount}, 长文件名映射: {longNameCount}, 失败: {failedCount}";
            Log("INFO", report.StatusMessage);
            ProgressChanged?.Invoke(report);

            return report;
        }

        public void PauseOrCancel()
        {
            _syncCts?.Cancel();
            CurrentState = SyncState.Paused;
            Log("INFO", "同步已被暂停。");
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
            // 防抖 2 秒
            _pendingWatcherEvents[fullPath] = DateTime.Now;
            _watcherDebounceTimer.Change(2000, Timeout.Infinite);
        }

        public event Action? RequestWatcherTriggeredSync;

        private void OnWatcherDebounceElapsed(object? state)
        {
            if (CurrentState == SyncState.Syncing) return;
            Log("INFO", "检测到本地文件变动，正在触发自动增量同步...");
            RequestWatcherTriggeredSync?.Invoke();
        }

        #endregion
    }
}
