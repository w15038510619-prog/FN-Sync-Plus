using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services
{
    /// <summary>
    /// 多任务调度与生命周期协调器：
    /// 统一调度多文件夹同步、全局并发控制、独立文件监控与定时轮询、共享 NAS 会话、任务状态持久化
    /// </summary>
    public class TaskManager : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly LongNameEngine _longNameEngine;
        private readonly ManifestManager _manifestManager;
        private readonly SmbAuthService _smbService;

        private readonly ConcurrentQueue<string> _taskQueue = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningCts = new();
        private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
        private readonly ConcurrentDictionary<string, Timer> _watcherDebounceTimers = new();
        private readonly ConcurrentDictionary<string, Timer> _intervalTimers = new();
        private readonly ConcurrentDictionary<string, List<string>> _taskLogBuffers = new();
        private readonly object _logLock = new();

        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly SemaphoreSlim _queueSignal = new(0);
        private readonly CancellationTokenSource _managerCts = new();
        private readonly Task _queueWorkerTask;

        private bool _isNasOnline = true;
        public bool IsNasOnline => _isNasOnline;

        public event Action<SyncTask, SyncProgressReport>? TaskProgressChanged;
        public event Action<SyncTask, string, string>? TaskLogEmitted; // (task, level, msg)
        public event Action<SyncTask>? TaskStateChanged;
        public event Action? TaskListChanged;

        /// <summary>
        /// 统一 NAS 存储提供程序委托（由 NasConnectionService 或工厂提供）
        /// </summary>
        public Func<INasStorageProvider?>? StorageProviderSupplier { get; set; }

        public IReadOnlyList<SyncTask> Tasks => _settings.Tasks;

        public TaskManager(
            AppSettings settings,
            SmbAuthService smbService)
            : this(settings, new LongNameEngine(), new ManifestManager(), smbService)
        {
        }

        public TaskManager(
            AppSettings settings,
            LongNameEngine longNameEngine,
            ManifestManager manifestManager,
            SmbAuthService smbService)
        {
            _settings = settings;
            _longNameEngine = longNameEngine;
            _manifestManager = manifestManager;
            _smbService = smbService;

            int maxConcurrent = Math.Max(1, _settings.MaxConcurrentSyncs);
            _concurrencySemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);

            // 启动后台队列消费工作线程
            _queueWorkerTask = Task.Run(() => ProcessQueueLoopAsync(_managerCts.Token));

            // 初始化并启动现有任务的文件监控和定时器
            InitializeAllTaskAutomations();
        }

        #region 任务集合增删改查

        /// <summary>
        /// 添加新同步任务，并执行严格的安全冲突防重叠检查
        /// </summary>
        public void AddTask(SyncTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            // 1. 检查本地源是否与已有任务冲突（禁止同源、禁止父子嵌套）
            foreach (var existing in _settings.Tasks)
            {
                if (PathHelper.ArePathsSameOrNested(task.SourceFolder, existing.SourceFolder, out string reason))
                {
                    throw new InvalidOperationException($"无法添加任务【{task.TaskName}】：本地源文件夹与已有任务【{existing.TaskName}】冲突！\n{reason}");
                }

                // 2. 检查 NAS 目标是否与已有任务重叠
                if (PathHelper.ArePathsSameOrNested(task.DestinationFolder, existing.DestinationFolder, out string destReason))
                {
                    throw new InvalidOperationException($"无法添加任务【{task.TaskName}】：NAS 目标备份目录与已有任务【{existing.TaskName}】重叠！\n不同任务必须享有独立的存储子目录。");
                }
            }

            _settings.Tasks.Add(task);
            _settings.Save();

            ApplyTaskAutomation(task);
            TaskListChanged?.Invoke();
            AppendTaskLog(task, "INFO", $"任务【{task.TaskName}】已创建成功 (源: {task.SourceFolder} -> 目标: {task.DestinationFolder})");
        }

        /// <summary>
        /// 异步安全删除同步任务（仅删除本地配置与调度，绝对不删除本地源文件或 NAS 已有备份数据）
        /// </summary>
        public async Task<bool> RemoveTaskAsync(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return false;

            SyncTask? task;
            lock (_settings.Tasks)
            {
                task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            }
            if (task == null) return false;

            // 1. 标记状态为正在安全移除，通知界面
            task.State = TaskSyncState.Removing;
            task.LastSyncStatus = "正在移除任务配置...";
            TaskStateChanged?.Invoke(task);

            // 2. 停止文件系统监听与定时器
            StopTaskAutomation(taskId);

            // 3. 安全停止正在运行的同步进程（等待工作线程优雅退出，杜绝提前 Dispose CTS 引发的崩溃）
            if (_runningCts.TryRemove(taskId, out var cts))
            {
                try
                {
                    cts.Cancel();
                    for (int i = 0; i < 20; i++)
                    {
                        if (!task.IsRunning) break;
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                }
                catch { }
                finally
                {
                    _ = Task.Delay(3000).ContinueWith(_ =>
                    {
                        try { cts.Dispose(); } catch { }
                    });
                }
            }

            // 4. 清除任务运行日志
            ClearTaskLogs(taskId);

            // 5. 从本地配置中移除并持久化（绝对不删除本地源文件或 NAS 备份文件）
            bool removed;
            lock (_settings.Tasks)
            {
                removed = _settings.Tasks.Remove(task);
                if (removed)
                {
                    _settings.Save();
                }
            }

            if (removed)
            {
                TaskListChanged?.Invoke();
            }

            return removed;
        }

        /// <summary>
        /// 删除同步任务（仅删除本地配置与调度，绝对不删除本地源文件或 NAS 已有备份数据）
        /// </summary>
        public void RemoveTask(string taskId)
        {
            _ = RemoveTaskAsync(taskId);
        }

        /// <summary>
        /// 更新任务配置并立即生效
        /// </summary>
        public void UpdateTask(SyncTask task)
        {
            var existing = _settings.Tasks.FirstOrDefault(t => t.TaskId == task.TaskId);
            if (existing != null)
            {
                existing.TaskName = task.TaskName;
                existing.Enabled = task.Enabled;
                existing.AutoSyncEnabled = task.AutoSyncEnabled;
                existing.RealtimeWatchEnabled = task.RealtimeWatchEnabled;
                existing.IntervalMinutes = task.IntervalMinutes;
                existing.Mode = task.Mode;
                _settings.Save();

                ApplyTaskAutomation(existing);
                TaskListChanged?.Invoke();
            }
        }

        #endregion

        #region 任务调度与队列执行

        /// <summary>
        /// 触发指定任务立即执行同步
        /// </summary>
        public void TriggerSync(string taskId)
        {
            var task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task == null || !task.Enabled || task.State == TaskSyncState.Paused || task.State == TaskSyncState.Removing)
            {
                if (task != null && task.State == TaskSyncState.Paused)
                {
                    AppendTaskLog(task, "INFO", "任务处于暂停状态，已忽略触发。");
                }
                return;
            }

            // 任务配置合法性校验拦截（杜绝自同步死循环、盘符越界或无效路径伪造成功）
            if (!task.ValidateConfiguration(out string configError))
            {
                task.State = TaskSyncState.ConfigurationError;
                task.LastError = configError;
                task.LastSyncStatus = "配置异常";
                AppendTaskLog(task, "ERROR", $"任务配置异常，无法执行同步：{configError}");
                TaskStateChanged?.Invoke(task);
                return;
            }

            if (task.IsRunning)
            {
                AppendTaskLog(task, "INFO", "任务正在执行中，已忽略重复触发。");
                return;
            }

            if (!_taskQueue.Contains(taskId))
            {
                task.State = TaskSyncState.Queued;
                TaskStateChanged?.Invoke(task);
                _taskQueue.Enqueue(taskId);
                _queueSignal.Release();
                AppendTaskLog(task, "INFO", "任务已加入执行队列，等待调度...");
            }
        }

        /// <summary>
        /// 批量触发所有已启用的同步任务
        /// </summary>
        public void TriggerSyncAll()
        {
            foreach (var task in _settings.Tasks.Where(t => t.Enabled && t.State != TaskSyncState.Paused))
            {
                TriggerSync(task.TaskId);
            }
        }

        public void PauseTask(string taskId)
        {
            var task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task == null) return;

            CancelTaskSync(taskId);
            StopTaskAutomation(taskId); // 彻底注销并停止文件变动监听与定时轮询
            task.State = TaskSyncState.Paused;
            task.LastSyncStatus = "已暂停";
            _settings.Save();
            TaskStateChanged?.Invoke(task);
            AppendTaskLog(task, "INFO", "任务已手动暂停，本地变动监控与定时器已彻底停止。");
        }

        public void ResumeTask(string taskId)
        {
            var task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task == null) return;

            task.State = TaskSyncState.Idle;
            task.LastSyncStatus = "已恢复就绪";
            ApplyTaskAutomation(task); // 重新挂载监听与定时轮询
            _settings.Save();
            TaskStateChanged?.Invoke(task);
            AppendTaskLog(task, "INFO", "任务已恢复，已重新激活变动监听，即将触发首次检查。");
            TriggerSync(taskId);
        }

        private void CancelTaskSync(string taskId)
        {
            if (_runningCts.TryRemove(taskId, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch { }
                _ = Task.Delay(3000).ContinueWith(_ =>
                {
                    try { cts.Dispose(); } catch { }
                });
            }
        }

        private async Task ProcessQueueLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _queueSignal.WaitAsync(token).ConfigureAwait(false);

                    if (_taskQueue.TryDequeue(out string? taskId))
                    {
                        var task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId);
                        if (task == null || !task.Enabled || task.State == TaskSyncState.Paused || task.State == TaskSyncState.Removing || task.State == TaskSyncState.ConfigurationError) continue;

                        // 等待获取全局并发信号量槽位
                        await _concurrencySemaphore.WaitAsync(token).ConfigureAwait(false);

                        // 异步执行单个任务
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await ExecuteSingleTaskSyncAsync(task, token).ConfigureAwait(false);
                            }
                            finally
                            {
                                _concurrencySemaphore.Release();
                            }
                        }, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
            }
        }

        private async Task ExecuteSingleTaskSyncAsync(SyncTask task, CancellationToken managerToken)
        {
            // 运行时二次安全配置校验拦截
            if (!task.ValidateConfiguration(out string valErr))
            {
                task.State = TaskSyncState.ConfigurationError;
                task.LastError = valErr;
                task.LastSyncStatus = "配置异常";
                AppendTaskLog(task, "ERROR", $"任务配置异常，无法执行同步：{valErr}");
                _settings.Save();
                TaskStateChanged?.Invoke(task);
                return;
            }

            var taskCts = CancellationTokenSource.CreateLinkedTokenSource(managerToken);
            _runningCts[task.TaskId] = taskCts;

            // 每个任务独立实例化 LongNameEngine 与 ManifestManager，彻底隔离多任务字典与临时映射
            var taskLongNameEngine = new LongNameEngine(maxUtf8Bytes: 255);
            var taskManifestManager = new ManifestManager();
            var syncEngine = new SyncEngine(taskLongNameEngine, taskManifestManager, _smbService);

            syncEngine.ProgressChanged += rep =>
            {
                task.CurrentProgress = rep.TotalFiles > 0 ? (double)rep.ScannedFiles / rep.TotalFiles * 100.0 : 0;
                task.CurrentFile = rep.CurrentFile;
                task.CurrentSpeedBytesPerSec = rep.SpeedBytesPerSec;
                TaskProgressChanged?.Invoke(task, rep);
            };

            syncEngine.LogEmitted += (level, msg) =>
            {
                AppendTaskLog(task, level, msg);
            };

            try
            {
                task.State = TaskSyncState.Syncing;
                task.LastError = null;
                TaskStateChanged?.Invoke(task);

                var provider = StorageProviderSupplier?.Invoke();
                var report = await syncEngine.RunSyncAsync(task.SourceFolder, task.DestinationFolder, provider, taskCts.Token).ConfigureAwait(false);

                task.LastSyncTime = DateTime.Now;
                task.LastScannedFiles = report.TotalFiles;
                task.LastUploadedFiles = report.UploadedFiles;
                task.LastUnchangedFiles = report.UnchangedFiles;
                task.LastLongNameFiles = report.LongNameFiles;
                task.LastFailedFiles = report.FailedFiles;
                task.LastTransferredBytes = report.BytesTransferred;

                task.CurrentSpeedBytesPerSec = 0;

                if (report.FailedFiles > 0)
                {
                    task.State = TaskSyncState.Error;
                    if (report.UploadedFiles == 0)
                    {
                        task.CurrentFile = $"备份失败 (共 {report.FailedFiles} 个文件同步失败)";
                        task.LastSyncStatus = $"备份失败 ({report.FailedFiles} 个文件失败)";
                    }
                    else
                    {
                        task.CurrentFile = $"部分失败 ({report.UploadedFiles} 个成功, {report.FailedFiles} 个失败)";
                        task.LastSyncStatus = $"部分失败 ({report.UploadedFiles} 成功, {report.FailedFiles} 失败)";
                    }
                    task.LastError = $"有 {report.FailedFiles} 个文件同步失败，可在任务详情查看完整诊断日志";

                    task.CurrentProgress = report.TotalFiles > 0
                        ? (double)(report.UploadedFiles + report.UnchangedFiles) / report.TotalFiles * 100.0
                        : 0.0;

                    foreach (var item in syncEngine.FailedItems)
                    {
                        LogService.Default.LogTask(
                            taskId: task.TaskId,
                            level: "ERROR",
                            stage: "UPLOAD",
                            message: $"文件同步失败: {item.ErrorMessage}",
                            localRel: item.RelativePath,
                            errorMsg: item.ErrorMessage,
                            retryCount: item.RetryCount);
                    }
                }
                else
                {
                    task.State = TaskSyncState.Completed;
                    task.CurrentProgress = 100.0;
                    task.CurrentFile = "所有文件均已同步更新至最新状态";
                    task.LastSyncStatus = "同步成功完成 (已最新)";
                }

                _settings.Save();
                TaskStateChanged?.Invoke(task);
            }
            catch (OperationCanceledException)
            {
                task.State = TaskSyncState.Paused;
                task.LastSyncStatus = "已暂停/取消";
                TaskStateChanged?.Invoke(task);
            }
            catch (Exception ex)
            {
                task.State = TaskSyncState.Error;
                task.LastError = ex.Message;
                task.LastSyncStatus = "执行异常";
                AppendTaskLog(task, "ERROR", $"任务异常中断: {ex.Message}");
                TaskStateChanged?.Invoke(task);
            }
            finally
            {
                _runningCts.TryRemove(task.TaskId, out _);
                taskCts.Dispose();
            }
        }

        #endregion

        #region 独立监控与定时器管理

        private void InitializeAllTaskAutomations()
        {
            foreach (var task in _settings.Tasks)
            {
                // 首先校验任务配置是否合法
                if (!task.ValidateConfiguration(out string configError))
                {
                    task.State = TaskSyncState.ConfigurationError;
                    task.LastError = configError;
                    task.LastSyncStatus = "配置异常";
                    AppendTaskLog(task, "WARN", $"任务配置异常：{configError}");
                    continue;
                }

                // 若任务处于暂停状态，软件重启后坚决保持暂停，绝不擅自启动后台监控或定时器
                if (task.Enabled && task.State != TaskSyncState.Paused)
                {
                    ApplyTaskAutomation(task);
                }
            }
        }

        private void ApplyTaskAutomation(SyncTask task)
        {
            StopTaskAutomation(task.TaskId);

            // 暂停状态或禁用状态绝对不启动变动监听与定时轮询
            if (!task.Enabled || task.State == TaskSyncState.Paused) return;

            // 1. 本地目录变动实时监听
            if (task.RealtimeWatchEnabled && Directory.Exists(task.SourceFolder))
            {
                try
                {
                    var watcher = new FileSystemWatcher(task.SourceFolder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };

                    var debounceTimer = new Timer(_ =>
                    {
                        if (task.Enabled && !task.IsRunning)
                        {
                            AppendTaskLog(task, "INFO", "检测到本地文件变动，正在触发自动增量同步...");
                            TriggerSync(task.TaskId);
                        }
                    }, null, Timeout.Infinite, Timeout.Infinite);

                    Action queueEvent = () => debounceTimer.Change(2000, Timeout.Infinite);

                    watcher.Changed += (s, e) => queueEvent();
                    watcher.Created += (s, e) => queueEvent();
                    watcher.Renamed += (s, e) => queueEvent();
                    watcher.Error += (s, e) =>
                    {
                        AppendTaskLog(task, "WARN", "本地变动监视器缓冲区溢出，正在排队全量重新扫描。");
                        queueEvent();
                    };

                    watcher.EnableRaisingEvents = true;
                    _watchers[task.TaskId] = watcher;
                    _watcherDebounceTimers[task.TaskId] = debounceTimer;
                }
                catch (Exception ex)
                {
                    AppendTaskLog(task, "WARN", $"无法启动文件变动监视: {ex.Message}");
                }
            }

            // 2. 定时轮询增量同步
            if (task.AutoSyncEnabled && task.IntervalMinutes > 0)
            {
                var timer = new Timer(_ =>
                {
                    if (task.Enabled && !task.IsRunning)
                    {
                        AppendTaskLog(task, "INFO", "例行定时增量备份检查...");
                        TriggerSync(task.TaskId);
                    }
                }, null, TimeSpan.FromMinutes(task.IntervalMinutes), TimeSpan.FromMinutes(task.IntervalMinutes));

                _intervalTimers[task.TaskId] = timer;
            }
        }

        private void StopTaskAutomation(string taskId)
        {
            if (_watchers.TryRemove(taskId, out var watcher))
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch { }
            }

            if (_watcherDebounceTimers.TryRemove(taskId, out var debTimer))
            {
                try { debTimer.Dispose(); } catch { }
            }

            if (_intervalTimers.TryRemove(taskId, out var timer))
            {
                try { timer.Dispose(); } catch { }
            }
        }

        #endregion

        #region 日志记录

        private void AppendTaskLog(SyncTask task, string level, string message)
        {
            string formatted = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";

            // 1. 存入持久化日志服务 (磁盘文件 + 内存)
            try
            {
                LogService.Default.LogTask(new TaskLogEntry
                {
                    TaskId = task.TaskId,
                    Level = level,
                    Stage = "SYNC",
                    Message = message,
                    Timestamp = DateTimeOffset.Now
                });
            }
            catch { }

            // 2. 存入即时环形缓冲
            lock (_logLock)
            {
                if (!_taskLogBuffers.TryGetValue(task.TaskId, out var list))
                {
                    list = new List<string>();
                    _taskLogBuffers[task.TaskId] = list;
                }

                if (list.Count > 300)
                {
                    list.RemoveRange(0, 100);
                }
                list.Add(formatted);
            }

            TaskLogEmitted?.Invoke(task, level, formatted);
        }

        public IReadOnlyList<string> GetTaskLogs(string taskId)
        {
            // 优先从磁盘持久化日志系统读取历史日志，确保重启后日志不丢失
            try
            {
                var persistentLogs = LogService.Default.GetTaskLogs(taskId);
                if (persistentLogs != null && persistentLogs.Count > 0)
                {
                    return persistentLogs.Select(p => p.FormattedDisplay).ToList();
                }
            }
            catch { }

            lock (_logLock)
            {
                if (_taskLogBuffers.TryGetValue(taskId, out var list))
                {
                    return list.ToList();
                }
                return Array.Empty<string>();
            }
        }

        public void ClearTaskLogs(string taskId)
        {
            lock (_logLock)
            {
                if (_taskLogBuffers.TryGetValue(taskId, out var list))
                {
                    list.Clear();
                }
            }
        }

        #endregion

        #region 网络连通性联动

        public void SetNasOnlineState(bool isOnline)
        {
            _isNasOnline = isOnline;
            if (!_isNasOnline)
            {
                foreach (var task in _settings.Tasks.Where(t => t.IsRunning))
                {
                    task.State = TaskSyncState.OfflineWaiting;
                    task.LastSyncStatus = "NAS离线等待恢复";
                    TaskStateChanged?.Invoke(task);
                }
            }
            else
            {
                // 网络恢复时唤醒
                foreach (var task in _settings.Tasks.Where(t => t.State == TaskSyncState.OfflineWaiting))
                {
                    task.State = TaskSyncState.Idle;
                    task.LastSyncStatus = "网络已恢复";
                    TaskStateChanged?.Invoke(task);
                    TriggerSync(task.TaskId);
                }
            }
        }

        #endregion

        public void Dispose()
        {
            _managerCts.Cancel();
            _queueSignal.Dispose();
            _concurrencySemaphore.Dispose();

            foreach (var w in _watchers.Values) { try { w.Dispose(); } catch { } }
            foreach (var t in _watcherDebounceTimers.Values) { try { t.Dispose(); } catch { } }
            foreach (var t in _intervalTimers.Values) { try { t.Dispose(); } catch { } }
            foreach (var cts in _runningCts.Values) { try { cts.Cancel(); cts.Dispose(); } catch { } }

            _watchers.Clear();
            _watcherDebounceTimers.Clear();
            _intervalTimers.Clear();
            _runningCts.Clear();
        }
    }
}
