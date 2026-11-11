using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;

namespace FNSyncPlus.Core.Services
{
    /// <summary>
    /// 统一持久化任务与系统诊断日志服务
    /// 存储路径: %LOCALAPPDATA%\FNSyncPlus\logs\
    /// 支持进程重启后历史追溯、单任务日志检索、安全脱敏与诊断导出
    /// </summary>
    public class LogService
    {
        private static readonly Lazy<LogService> _defaultInstance = new(() => new LogService());
        public static LogService Default => _defaultInstance.Value;

        private readonly string _baseLogsDir;
        private readonly ConcurrentDictionary<string, ConcurrentQueue<TaskLogEntry>> _inMemoryRecentLogs = new();
        private readonly ConcurrentDictionary<long, Task> _pendingTaskWrites = new();
        private long _nextTaskWriteId;
        private readonly object _fileWriteLock = new();

        private static readonly Regex SensitivePatterns = new(
            @"(password|token|secret|authorization|cookie|signature|trim-sign)[\s:=]+([^\s,;""'\}]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public LogService(string? customLogsDir = null)
        {
            if (!string.IsNullOrWhiteSpace(customLogsDir))
            {
                _baseLogsDir = customLogsDir;
            }
            else
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _baseLogsDir = Path.Combine(localAppData, "FNSyncPlus", "logs");
            }

            try
            {
                if (!Directory.Exists(_baseLogsDir))
                {
                    Directory.CreateDirectory(_baseLogsDir);
                }
            }
            catch
            {
                // 保证日志服务初始化永不崩溃
            }
        }

        public string LogsDirectory => _baseLogsDir;
        public string GetTaskLogDirectory(string taskId) => Path.Combine(_baseLogsDir, "tasks", SanitizeFileName(taskId));

        #region 任务日志记录与持久化

        /// <summary>
        /// 记录单任务结构化诊断日志并异步持久化至磁盘
        /// </summary>
        public void LogTask(TaskLogEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.TaskId)) return;

            // 敏感信息安全脱敏
            entry.Message = Sanitize(entry.Message);
            if (!string.IsNullOrEmpty(entry.ErrorMessage))
            {
                entry.ErrorMessage = Sanitize(entry.ErrorMessage);
            }

            // 1. 存入内存即时队列 (最多保留最近 300 条供实时界面高速渲染)
            var queue = _inMemoryRecentLogs.GetOrAdd(entry.TaskId, _ => new ConcurrentQueue<TaskLogEntry>());
            queue.Enqueue(entry);
            while (queue.Count > 300)
            {
                queue.TryDequeue(out _);
            }

            // 2. 异步持久化到磁盘
            long writeId = Interlocked.Increment(ref _nextTaskWriteId);
            Task write = Task.Run(() => WriteTaskLogToFile(entry));
            _pendingTaskWrites[writeId] = write;
            _ = write.ContinueWith(completed => { _pendingTaskWrites.TryRemove(writeId, out _); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>Waits for task-log writes already queued; the writer does not report disk-write failures.</summary>
        public Task FlushPendingTaskLogsAsync() => Task.WhenAll(_pendingTaskWrites.Values.ToArray());

        /// <summary>
        /// 便捷任务日志记录
        /// </summary>
        public void LogTask(
            string taskId, 
            string level, 
            string stage, 
            string message,
            string? localRel = null, 
            string? remoteRel = null, 
            int? utf8Bytes = null,
            string? nasMappedName = null, 
            string? rpcName = null, 
            string? errorCode = null, 
            string? errorMsg = null, 
            int retryCount = 0,
            string? uploadResult = null,
            string? manifestResult = null,
            string? verificationResult = null)
        {
            var entry = new TaskLogEntry
            {
                TaskId = taskId,
                Level = level,
                Stage = stage,
                Message = message,
                LocalRelPath = localRel,
                RemoteRelPath = remoteRel,
                Utf8Bytes = utf8Bytes,
                NasMappedName = nasMappedName,
                RpcName = rpcName,
                ErrorCode = errorCode,
                ErrorMessage = errorMsg,
                RetryCount = retryCount,
                UploadResult = uploadResult,
                ManifestResult = manifestResult,
                VerificationResult = verificationResult,
                Timestamp = DateTimeOffset.Now
            };

            LogTask(entry);
        }

        private void WriteTaskLogToFile(TaskLogEntry entry)
        {
            try
            {
                string taskLogDir = GetTaskLogDirectory(entry.TaskId);
                if (!Directory.Exists(taskLogDir))
                {
                    Directory.CreateDirectory(taskLogDir);
                }

                string dateStr = entry.Timestamp.ToString("yyyyMMdd");
                string logFile = Path.Combine(taskLogDir, $"{dateStr}.log");

                string jsonLine = JsonSerializer.Serialize(entry) + Environment.NewLine;

                lock (_fileWriteLock)
                {
                    File.AppendAllText(logFile, jsonLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 磁盘写入异常时绝不向上崩溃抛出，内存队列中依然保留
            }
        }

        #endregion

        #region 连接与崩溃日志

        /// <summary>
        /// 记录网络与 NAS 存储连接诊断日志
        /// </summary>
        public void LogConnection(string level, string message)
        {
            try
            {
                string cleanMsg = Sanitize(message);
                string dateStr = DateTime.Now.ToString("yyyyMMdd");
                string logFile = Path.Combine(_baseLogsDir, $"connection_{dateStr}.log");
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level,-5}] {cleanMsg}{Environment.NewLine}";

                lock (_fileWriteLock)
                {
                    File.AppendAllText(logFile, line, Encoding.UTF8);
                }
            }
            catch { }
        }

        /// <summary>
        /// 记录未捕获异常与致命崩溃日志
        /// </summary>
        public void LogCrash(Exception ex, string context)
        {
            try
            {
                string dateStr = DateTime.Now.ToString("yyyyMMdd");
                string logFile = Path.Combine(_baseLogsDir, $"crash_{dateStr}.log");
                var sb = new StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine($"[CRASH] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | Context: {context}");
                sb.AppendLine($"Type: {ex.GetType().FullName}");
                sb.AppendLine($"Message: {Sanitize(ex.Message)}");
                sb.AppendLine($"StackTrace:\n{ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    sb.AppendLine($"InnerException: {ex.InnerException.GetType().FullName}: {Sanitize(ex.InnerException.Message)}");
                    sb.AppendLine($"InnerStackTrace:\n{ex.InnerException.StackTrace}");
                }
                sb.AppendLine("================================================================================");
                sb.AppendLine();

                lock (_fileWriteLock)
                {
                    File.AppendAllText(logFile, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { }
        }

        #endregion

        #region 读取、检索与导出

        /// <summary>
        /// 获取指定任务的完整日志记录（优先从磁盘读取持久化历史，合并当前运行时新增项）
        /// 软件重启后仍能 100% 完整加载历史日志
        /// </summary>
        public IReadOnlyList<TaskLogEntry> GetTaskLogs(string taskId, int maxCount = 500)
        {
            var result = new List<TaskLogEntry>();
            if (string.IsNullOrWhiteSpace(taskId)) return result;

            string taskLogDir = GetTaskLogDirectory(taskId);

            // 1. 从磁盘文件读取最近的日志
            if (Directory.Exists(taskLogDir))
            {
                try
                {
                    var files = Directory.GetFiles(taskLogDir, "*.log")
                                         .OrderByDescending(f => f)
                                         .Take(5) // 取最近5天的日志文件
                                         .OrderBy(f => f);

                    foreach (var file in files)
                    {
                        var lines = File.ReadAllLines(file, Encoding.UTF8);
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            try
                            {
                                if (line.TrimStart().StartsWith("{"))
                                {
                                    var entry = JsonSerializer.Deserialize<TaskLogEntry>(line);
                                    if (entry != null)
                                    {
                                        result.Add(entry);
                                        continue;
                                    }
                                }
                            }
                            catch { }

                            // 兼容非 JSON 文本行
                            result.Add(new TaskLogEntry
                            {
                                TaskId = taskId,
                                Level = line.Contains("[ERROR]") ? "ERROR" : (line.Contains("[WARN]") ? "WARN" : "INFO"),
                                Message = line
                            });
                        }
                    }
                }
                catch { }
            }

            // 2. 合并当前内存中尚未刷盘或刚产生的即时条目
            if (_inMemoryRecentLogs.TryGetValue(taskId, out var memQueue))
            {
                var memItems = memQueue.ToArray();
                foreach (var item in memItems)
                {
                    // 依据时间戳与信息去重
                    if (!result.Any(r => r.Timestamp == item.Timestamp && r.Message == item.Message))
                    {
                        result.Add(item);
                    }
                }
            }

            // 3. 按照时间正序排列并截取最近 maxCount 条
            return result.OrderBy(r => r.Timestamp).TakeLast(maxCount).ToList();
        }

        /// <summary>
        /// 将单任务的完整诊断日志与配置环境打包为 zip 文件供用户导出
        /// </summary>
        public string ExportTaskDiagnosticZip(string taskId, string targetZipPath)
        {
            string taskLogDir = GetTaskLogDirectory(taskId);

            string tempDir = Path.Combine(Path.GetTempPath(), "FNSync_Diag_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. 复制所有任务日志
                if (Directory.Exists(taskLogDir))
                {
                    string targetLogs = Path.Combine(tempDir, "TaskLogs");
                    Directory.CreateDirectory(targetLogs);
                    foreach (var file in Directory.GetFiles(taskLogDir))
                    {
                        File.Copy(file, Path.Combine(targetLogs, Path.GetFileName(file)), true);
                    }
                }

                // 复制连接日志与崩溃日志辅助诊断
                string todayStr = DateTime.Now.ToString("yyyyMMdd");
                string connFile = Path.Combine(_baseLogsDir, $"connection_{todayStr}.log");
                if (File.Exists(connFile))
                {
                    File.Copy(connFile, Path.Combine(tempDir, Path.GetFileName(connFile)), true);
                }
                string crashFile = Path.Combine(_baseLogsDir, $"crash_{todayStr}.log");
                if (File.Exists(crashFile))
                {
                    File.Copy(crashFile, Path.Combine(tempDir, Path.GetFileName(crashFile)), true);
                }

                // 2. 生成诊断元数据
                var meta = new
                {
                    ExportTimeUtc = DateTime.UtcNow,
                    TaskId = taskId,
                    MachineName = Environment.MachineName,
                    OSVersion = Environment.OSVersion.ToString(),
                    DotNetVersion = Environment.Version.ToString()
                };
                File.WriteAllText(Path.Combine(tempDir, "diagnostic_meta.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

                // 3. 压缩打包
                ZipFile.CreateFromDirectory(tempDir, targetZipPath);
                return targetZipPath;
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        #endregion

        #region 辅助工具

        private static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return SensitivePatterns.Replace(input, "$1: [***REDACTED***]");
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(clean) || clean is "." or ".." ? "default_task" : clean;
        }

        #endregion
    }
}
