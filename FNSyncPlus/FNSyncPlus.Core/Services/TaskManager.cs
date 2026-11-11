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
        private const string IsolatedVersionedFormat = "isolated-versioned-v1";
        private readonly AppSettings _settings;
        private readonly LongNameEngine _longNameEngine;
        private readonly ManifestManager _manifestManager;
        private readonly SmbAuthService _smbService;
        private readonly LogService _logService;

        private readonly ConcurrentQueue<string> _taskQueue = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningCts = new();
        private readonly ConcurrentDictionary<string, string> _runningBatchIds = new();
        private readonly object _taskLifecycleLock = new();
        private readonly Dictionary<string, TaskCompletionSource> _runningCompletions = new();
        private readonly Dictionary<string, TaskCompletionSource> _recoverySessions = new();
        private readonly HashSet<string> _removingTasks = new();
        private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
        private readonly ConcurrentDictionary<string, Timer> _watcherDebounceTimers = new();
        private readonly ConcurrentDictionary<string, Timer> _intervalTimers = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _pendingRenames = new();
        private sealed record IsolatedProjectionState(ExperimentalPhysicalSnapshot.Result Plan, ExperimentalFirstProjection.Published Publication);
        private readonly ConcurrentDictionary<string, IsolatedProjectionState> _isolatedProjections = new();
        private readonly ConcurrentDictionary<string, List<string>> _taskLogBuffers = new();
        private readonly object _logLock = new();

        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly SemaphoreSlim _queueSignal = new(0);
        private readonly CancellationTokenSource _managerCts = new();
        private readonly Task _queueWorkerTask;

        private volatile bool _isNasOnline = true;
        private bool _nasStateInitialized;
        private bool _stopping;
        private Task? _shutdownTask;
        private int _resourcesDisposed;
        public bool IsNasOnline => _isNasOnline;

        public event Action<SyncTask, SyncProgressReport>? TaskProgressChanged;
        public event Action<SyncTask, string, string>? TaskLogEmitted; // (task, level, msg)
        public event Action<SyncTask>? TaskStateChanged;
        public event Action? TaskListChanged;

        /// <summary>
        /// 统一 NAS 存储提供程序委托（由 NasConnectionService 或工厂提供）
        /// </summary>
        public Func<INasStorageProvider?>? StorageProviderSupplier { get; set; }

        private bool _useExperimentalVersionedBackup;
        public bool UseExperimentalVersionedBackup
        {
            get => _useExperimentalVersionedBackup;
            set
            {
                if (value && !_settings.IsInMemoryOnly && !_settings.IsVersionedBetaProfile)
                    throw new InvalidOperationException("版本化备份只能用于隔离测试或独立新版试验配置");
                _useExperimentalVersionedBackup = value;
            }
        }

        public IReadOnlyList<SyncTask> Tasks => _settings.Tasks;

        public TaskManager(
            AppSettings settings,
            SmbAuthService smbService,
            LogService? logService = null,
            bool experimentalVersionedBackup = false)
            : this(settings, new LongNameEngine(), new ManifestManager(), smbService, logService, experimentalVersionedBackup)
        {
        }

        public TaskManager(
            AppSettings settings,
            LongNameEngine longNameEngine,
            ManifestManager manifestManager,
            SmbAuthService smbService,
            LogService? logService = null,
            bool experimentalVersionedBackup = false)
        {
            _settings = settings;
            _longNameEngine = longNameEngine;
            _manifestManager = manifestManager;
            _smbService = smbService;
            _logService = logService ?? LogService.Default;
            if (experimentalVersionedBackup && !settings.IsInMemoryOnly && !settings.IsVersionedBetaProfile)
                throw new InvalidOperationException("版本化备份只能用于隔离测试或独立新版试验配置");
            foreach (var task in settings.Tasks)
            {
                if (task.ExperimentalInitializationId != null &&
                    (!Guid.TryParseExact(task.ExperimentalInitializationId, "N", out var initializationId) || initializationId == Guid.Empty))
                    throw new InvalidOperationException("首次备份归属标识无效，已停止任务。");
                if (settings.IsVersionedBetaProfile && (task.ProviderType != StorageProviderType.FnOsPersonal ||
                    !string.Equals(task.BackupStorageFormat, IsolatedVersionedFormat, StringComparison.Ordinal)))
                    throw new InvalidOperationException("新版配置包含未明确迁移的旧任务，已阻止自动启动。");
                if (task.BackupStorageFormat != null &&
                    (!experimentalVersionedBackup || task.ProviderType != StorageProviderType.FnOsPersonal ||
                     !string.Equals(task.BackupStorageFormat, IsolatedVersionedFormat, StringComparison.Ordinal)))
                    throw new InvalidOperationException("任务包含不支持的备份格式，已阻止旧同步引擎运行；请保留配置并使用对应版本处理。");
            }
            _useExperimentalVersionedBackup = experimentalVersionedBackup;
            if (experimentalVersionedBackup)
            {
                foreach (var task in settings.Tasks.Where(t => t.ProviderType == StorageProviderType.FnOsPersonal))
                {
                    task.IsExperimentalBackup = true;
                    task.BackupStorageFormat = IsolatedVersionedFormat;
                    task.RequiresNewExperimentalBackup = task.ExperimentalPendingInitialization;
                }
            }

            int maxConcurrent = Math.Max(1, _settings.MaxConcurrentSyncs);
            _concurrencySemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);

            // 启动后台队列消费工作线程
            _queueWorkerTask = Task.Run(() => ProcessQueueLoopAsync(_managerCts.Token));

            // 初始化并启动现有任务的文件监控和定时器
            InitializeAllTaskAutomations();
        }

        #region 任务集合增删改查

        private string CanonicalFnDestination(string path)
        {
            static bool LooksLikeVolumePath(string value)
            {
                int slash = value.IndexOf('/');
                int volumeEnd = slash < 0 ? value.Length : slash;
                return volumeEnd > 3 && value.StartsWith("vol", StringComparison.OrdinalIgnoreCase) && value[3..volumeEnd].All(char.IsAsciiDigit);
            }
            var fn = StorageProviderSupplier?.Invoke() as FnOsPersonalStorageProvider;
            string value = PathHelper.NormalizeRelPath(path);
            if (fn?.IsAuthenticated == true)
            {
                string resolved = fn.NormalizeRelativePath(path);
                if (LooksLikeVolumePath(value) && resolved.Equals(value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("飞牛卷路径不属于当前账号，无法安全确认目标");
                return resolved;
            }
            if (value.Equals("我的文件", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (value.StartsWith("我的文件/", StringComparison.OrdinalIgnoreCase)) value = value["我的文件/".Length..];
            if (LooksLikeVolumePath(value))
                throw new InvalidOperationException("无法离线确认飞牛目标路径别名，请连接后再操作");
            return value;
        }

        private void EnsureSeparateFromLegacyTasks(SyncTask task)
        {
            if (!_settings.IsVersionedBetaProfile) return;
            var legacy = _settings.ReadVersionedLegacySettings();
            if (legacy == null) return;
            if (!string.IsNullOrWhiteSpace(legacy.LegacySourceFolder) || !string.IsNullOrWhiteSpace(legacy.LegacyTargetShare))
                throw new InvalidOperationException("旧程序存在尚未确认的单任务配置，不能判断目标是否重叠。");
            if (!PathHelper.IsSafeRelativePath(task.DestinationFolder)) throw new InvalidOperationException("新版备份目标路径无效");
            string target = CanonicalFnDestination(task.DestinationFolder);
            if (!PathHelper.IsSafeRelativePath(target)) throw new InvalidOperationException("新版备份目标路径无效");
            foreach (var oldTask in legacy.Tasks)
            {
                if (oldTask == null) throw new InvalidOperationException("旧程序配置包含无效任务，无法检查目标重叠。");
                if (oldTask.ProviderType != StorageProviderType.FnOsPersonal)
                    throw new InvalidOperationException("无法确认旧程序非飞牛个人空间任务与当前目标是否重叠。");
                if (!PathHelper.IsSafeRelativePath(oldTask.DestinationFolder))
                    throw new InvalidOperationException("旧程序任务目标无效，无法检查目标重叠。");
                string oldTarget = CanonicalFnDestination(oldTask.DestinationFolder);
                if (string.IsNullOrEmpty(oldTarget)) throw new InvalidOperationException("旧程序任务使用个人空间根目录，无法安全建立新版目标。");
                if (!PathHelper.IsSafeRelativePath(oldTarget)) throw new InvalidOperationException("旧程序任务目标无效，无法检查目标重叠。");
                if (target.Equals(oldTarget, StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith(oldTarget + "/", StringComparison.OrdinalIgnoreCase) ||
                    oldTarget.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("新版备份目标与旧程序任务相同或嵌套，请选择独立目标。");
            }
        }

        /// <summary>
        /// 添加新同步任务，并执行严格的安全冲突防重叠检查
        /// </summary>
        public void AddTask(SyncTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            _settings.EnsureIsolatedTestTask(task);
            if (_settings.IsVersionedBetaProfile && (!UseExperimentalVersionedBackup || task.ProviderType != StorageProviderType.FnOsPersonal))
                throw new InvalidOperationException("新版试验配置仅接受飞牛版本化备份任务。");
            EnsureSeparateFromLegacyTasks(task);
            if (task.BackupStorageFormat != null || task.ExperimentalInitializationId != null)
                throw new InvalidOperationException("不能把已有备份格式的任务作为新任务添加");
            task.IsExperimentalBackup = UseExperimentalVersionedBackup && task.ProviderType == StorageProviderType.FnOsPersonal;
            task.RequiresNewExperimentalBackup = task.IsExperimentalBackup;
            task.ExperimentalPendingInitialization = task.IsExperimentalBackup;
            if (!string.IsNullOrWhiteSpace(task.ExperimentalExistingRoot) && !task.IsExperimentalBackup)
                throw new InvalidOperationException("复用已有目录目前只能在隔离试验会话使用");

            lock (_taskLifecycleLock)
            {
                if (_stopping) throw new InvalidOperationException("程序正在停止，不能添加任务");
                lock (_settings.Tasks)
                {
                    foreach (var existing in _settings.Tasks)
                    {
                        if (string.Equals(task.TaskId, existing.TaskId, StringComparison.Ordinal))
                            throw new InvalidOperationException("任务标识已存在，不能重复添加");
                        if (PathHelper.ArePathsSameOrNested(task.SourceFolder, existing.SourceFolder, out string reason))
                            throw new InvalidOperationException($"无法添加任务【{task.TaskName}】：本地源文件夹与已有任务【{existing.TaskName}】冲突！\n{reason}");
                        if (task.ProviderType == StorageProviderType.FnOsPersonal && existing.ProviderType == StorageProviderType.FnOsPersonal)
                        {
                            string candidate = CanonicalFnDestination(task.DestinationFolder), prior = CanonicalFnDestination(existing.DestinationFolder);
                            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(prior) || candidate.Equals(prior, StringComparison.OrdinalIgnoreCase) ||
                                candidate.StartsWith(prior + "/", StringComparison.OrdinalIgnoreCase) ||
                                prior.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException($"无法添加任务【{task.TaskName}】：飞牛目标与已有任务【{existing.TaskName}】相同或嵌套。");
                        }
                        if (PathHelper.ArePathsSameOrNested(task.DestinationFolder, existing.DestinationFolder, out string destReason))
                            throw new InvalidOperationException($"无法添加任务【{task.TaskName}】：NAS 目标备份目录与已有任务【{existing.TaskName}】重叠！\n不同任务必须享有独立的存储子目录。\n{destReason}");
                    }
                    task.BackupStorageFormat = task.IsExperimentalBackup ? IsolatedVersionedFormat : null;
                    task.ExperimentalInitializationId = task.IsExperimentalBackup ? Guid.NewGuid().ToString("N") : null;
                    _settings.Tasks.Add(task);
                    try { _settings.Save(); }
                    catch { _settings.Tasks.Remove(task); task.BackupStorageFormat = null; task.ExperimentalInitializationId = null; throw; }
                }
                ApplyTaskAutomation(task);
            }
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
            CancellationTokenSource? cts;
            Task? completion;
            lock (_taskLifecycleLock)
            {
                task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId);
                if (task == null || _recoverySessions.ContainsKey(taskId) || !_removingTasks.Add(taskId)) return false;
                task.State = TaskSyncState.Removing;
                task.LastSyncStatus = "正在停止同步并移除任务配置...";
                _runningCts.TryGetValue(taskId, out cts);
                completion = _runningCompletions.TryGetValue(taskId, out var done) ? done.Task : null;
            }
            bool releaseRemovalGate = true;
            try
            {
                TaskStateChanged?.Invoke(task);
                StopTaskAutomation(taskId);
                if (cts != null)
                {
                    try { cts.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
                if (completion != null && await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) != completion)
                {
                    task.State = TaskSyncState.Paused;
                    task.LastSyncStatus = "同步尚未停止，任务配置已保留";
                    task.LastError = "等待同步停止超时，请稍后重试移除任务";
                    TaskStateChanged?.Invoke(task);
                    releaseRemovalGate = false;
                    _ = completion.ContinueWith(_ =>
                    {
                        lock (_taskLifecycleLock)
                        {
                            task.State = TaskSyncState.Paused;
                            task.LastSyncStatus = "同步已停止，任务配置已保留";
                            _removingTasks.Remove(taskId);
                        }
                        TaskStateChanged?.Invoke(task);
                    }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    return false;
                }

                lock (_taskLifecycleLock)
                {
                    lock (_settings.Tasks)
                    {
                        int index = _settings.Tasks.IndexOf(task);
                        if (index < 0) return false;
                        _settings.Tasks.RemoveAt(index);
                        try { _settings.Save(); }
                        catch
                        {
                            _settings.Tasks.Insert(index, task);
                            task.State = TaskSyncState.Paused;
                            task.LastSyncStatus = "保存失败，任务配置已保留";
                            throw;
                        }
                    }
                }
                ClearTaskLogs(taskId);
                _isolatedProjections.TryRemove(taskId, out _);
                TaskListChanged?.Invoke();
                return true;
            }
            finally
            {
                if (releaseRemovalGate) lock (_taskLifecycleLock) _removingTasks.Remove(taskId);
            }
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
            lock (_taskLifecycleLock)
            {
                if (_stopping || _removingTasks.Contains(task.TaskId) || _recoverySessions.ContainsKey(task.TaskId) || _runningCompletions.ContainsKey(task.TaskId))
                    throw new InvalidOperationException("任务正在同步、停止或恢复，请等待完成后再修改设置。");
                var existing = _settings.Tasks.FirstOrDefault(t => t.TaskId == task.TaskId);
                if (existing == null) throw new InvalidOperationException("任务不存在，请关闭设置后重新查看任务列表。");
                lock (_settings.Tasks)
                {
                    var previous = (existing.TaskName, existing.Enabled, existing.AutoSyncEnabled, existing.RealtimeWatchEnabled,
                        existing.IntervalMinutes, existing.Mode, existing.ScheduleMode, existing.Strategy, existing.SettlingDelaySeconds, existing.MirrorDeletionEnabled);
                    existing.TaskName = task.TaskName;
                    existing.Enabled = task.Enabled;
                    existing.AutoSyncEnabled = task.AutoSyncEnabled;
                    existing.RealtimeWatchEnabled = task.RealtimeWatchEnabled;
                    existing.IntervalMinutes = task.IntervalMinutes;
                    existing.Mode = task.Mode;
                    existing.ScheduleMode = task.ScheduleMode;
                    existing.Strategy = task.Strategy;
                    existing.SettlingDelaySeconds = task.SettlingDelaySeconds;
                    existing.MirrorDeletionEnabled = task.MirrorDeletionEnabled;
                    try { _settings.Save(); }
                    catch
                    {
                        (existing.TaskName, existing.Enabled, existing.AutoSyncEnabled, existing.RealtimeWatchEnabled,
                            existing.IntervalMinutes, existing.Mode, existing.ScheduleMode, existing.Strategy, existing.SettlingDelaySeconds, existing.MirrorDeletionEnabled) = previous;
                        throw;
                    }

                    ApplyTaskAutomation(existing);
                    TaskListChanged?.Invoke();
                }
            }
        }

        #endregion

        #region 任务调度与队列执行

        /// <summary>
        /// 触发指定任务立即执行同步
        /// </summary>
        public void TriggerSync(string taskId)
        {
            lock (_taskLifecycleLock)
            {
                if (_stopping || _removingTasks.Contains(taskId) || _recoverySessions.ContainsKey(taskId) || _runningCompletions.ContainsKey(taskId)) return;
                var task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId);
                if (task == null || !task.Enabled || task.IsUserPaused || task.State == TaskSyncState.Paused || task.State == TaskSyncState.Removing)
                {
                    if (task != null && (task.IsUserPaused || task.State == TaskSyncState.Paused))
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
        }

        /// <summary>
        /// 批量触发所有已启用的同步任务
        /// </summary>
        public void TriggerSyncAll()
        {
            SyncTask[] tasks;
            lock (_taskLifecycleLock) tasks = _settings.Tasks.ToArray();
            foreach (var task in tasks.Where(t => t.Enabled && !t.IsUserPaused && t.State != TaskSyncState.Paused))
            {
                TriggerSync(task.TaskId);
            }
        }

        public void PauseTask(string taskId)
        {
            lock (_taskLifecycleLock)
            {
                if (_removingTasks.Contains(taskId)) return;
                var task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId);
                if (task == null) return;

                CancelTaskSync(taskId);
                StopTaskAutomation(taskId); // 彻底注销并停止文件变动监听与定时轮询
                task.IsUserPaused = true;
                task.State = TaskSyncState.Paused;
                task.LastSyncStatus = "已暂停";
                _settings.Save();
                TaskStateChanged?.Invoke(task);
                AppendTaskLog(task, "INFO", "任务已手动暂停，本地变动监控与定时器已彻底停止。");
            }
        }

        public void ResumeTask(string taskId)
        {
            lock (_taskLifecycleLock)
            {
                if (_recoverySessions.ContainsKey(taskId)) throw new InvalidOperationException("正在检查或恢复该任务，请等待操作结束");
                if (_stopping || _removingTasks.Contains(taskId) || _runningCompletions.ContainsKey(taskId)) return;
                var task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId);
                if (task == null) return;

                bool wasUserPaused = task.IsUserPaused;
                var oldState = task.State;
                string oldStatus = task.LastSyncStatus;
                task.IsUserPaused = false;
                task.State = TaskSyncState.Idle;
                task.LastSyncStatus = "已恢复就绪";
                try { _settings.Save(); }
                catch { task.IsUserPaused = wasUserPaused; task.State = oldState; task.LastSyncStatus = oldStatus; throw; }
                ApplyTaskAutomation(task); // 恢复状态保存成功后才重新挂载监听与定时轮询
                TaskStateChanged?.Invoke(task);
                AppendTaskLog(task, "INFO", "任务已恢复，自动化设置已重新生效。");
                if (task.ScheduleMode != TaskSyncScheduleMode.ManualOnly) TriggerSync(taskId);
            }
        }

        private void CancelTaskSync(string taskId)
        {
            if (_runningCts.TryGetValue(taskId, out var cts))
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>
        /// Pauses an isolated task, waits for its writer to stop, and blocks scheduling,
        /// removal and resume until the returned recovery session is disposed.
        /// </summary>
        public async Task<IsolatedRecoverySession?> BeginIsolatedRecoveryAsync(string taskId, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            SyncTask task;
            Task? running;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_taskLifecycleLock)
            {
                task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId)
                    ?? throw new InvalidOperationException("任务不存在");
                if ((!_settings.IsInMemoryOnly && !(_settings.IsVersionedBetaProfile &&
                    (!_settings.IsVersionedBetaTestProfile || _settings.HasIsolatedTestBoundary))) ||
                    !UseExperimentalVersionedBackup || !task.IsExperimentalBackup)
                    throw new InvalidOperationException("中断恢复仅支持新版备份任务");
                if (_stopping || _removingTasks.Contains(taskId) || _recoverySessions.ContainsKey(taskId))
                    throw new InvalidOperationException("任务正在停止、移除或执行其他恢复操作");
                _settings.EnsureIsolatedTestTask(task);
                EnsureSeparateFromLegacyTasks(task);
                _recoverySessions.Add(taskId, gate);
                try
                {
                    PauseTask(taskId);
                    running = _runningCompletions.TryGetValue(taskId, out var done) ? done.Task : null;
                }
                catch
                {
                    _recoverySessions.Remove(taskId);
                    gate.TrySetResult();
                    throw;
                }
            }
            bool handedOff = false;
            try
            {
                if (running != null && await Task.WhenAny(running, Task.Delay(timeout)).ConfigureAwait(false) != running) return null;
                lock (_taskLifecycleLock)
                {
                    if (_stopping || !_settings.Tasks.Contains(task) || !task.IsUserPaused || _runningCompletions.ContainsKey(taskId))
                        throw new InvalidOperationException("任务状态已变化，无法安全开始恢复检查");
                    task.State = TaskSyncState.Paused;
                    task.LastSyncStatus = "已暂停，等待中断恢复检查";
                    handedOff = true;
                    return new IsolatedRecoverySession(this, task, gate);
                }
            }
            finally { if (!handedOff) EndIsolatedRecovery(taskId, gate); }
        }

        private void EndIsolatedRecovery(string taskId, TaskCompletionSource gate)
        {
            lock (_taskLifecycleLock)
            {
                if (_recoverySessions.TryGetValue(taskId, out var existing) && ReferenceEquals(existing, gate))
                {
                    _recoverySessions.Remove(taskId);
                    _isolatedProjections.TryRemove(taskId, out _);
                }
            }
            gate.TrySetResult();
        }

        public sealed class IsolatedRecoverySession : IAsyncDisposable
        {
            private readonly TaskManager _owner;
            private readonly SyncTask _task;
            private readonly TaskCompletionSource _gate;
            private readonly string _destination;
            private readonly SemaphoreSlim _operation = new(1, 1);
            private int _closing;

            internal IsolatedRecoverySession(TaskManager owner, SyncTask task, TaskCompletionSource gate)
            {
                _owner = owner; _task = task; _gate = gate; _destination = task.DestinationFolder;
            }

            private INasStorageProvider CheckedProvider()
            {
                lock (_owner._taskLifecycleLock)
                {
                    if (_owner._stopping || !_owner._settings.Tasks.Contains(_task) || !_task.IsUserPaused ||
                        _task.DestinationFolder != _destination ||
                        !_owner._recoverySessions.TryGetValue(_task.TaskId, out var gate) || !ReferenceEquals(gate, _gate))
                        throw new InvalidOperationException("恢复会话已结束或任务状态发生变化");
                    _owner._settings.EnsureIsolatedTestTask(_task);
                    _owner.EnsureSeparateFromLegacyTasks(_task);
                }
                var provider = _owner.StorageProviderSupplier?.Invoke();
                if (provider == null || !provider.IsAuthenticated || provider.ProviderType != StorageProviderType.FnOsPersonal)
                    throw new InvalidOperationException("飞牛未连接，不能检查中断状态");
                if (provider is FnOsPersonalStorageProvider fn &&
                    ((!string.IsNullOrWhiteSpace(_task.NasUsername) && !string.Equals(_task.NasUsername, fn.CurrentUsername, StringComparison.Ordinal)) ||
                     (string.IsNullOrWhiteSpace(_task.NasUsername) && _owner._settings.IsVersionedBetaProfile && !_owner._settings.IsVersionedBetaTestProfile)))
                    throw new InvalidOperationException("当前飞牛账号与任务账号不一致或任务缺少账号信息");
                if (provider is FnOsPersonalStorageProvider device &&
                    ((!string.IsNullOrWhiteSpace(_task.NasDeviceId) && !device.MatchesServerAddress(_task.NasDeviceId)) ||
                     (string.IsNullOrWhiteSpace(_task.NasDeviceId) && _owner._settings.IsVersionedBetaProfile && !_owner._settings.IsVersionedBetaTestProfile)))
                    throw new InvalidOperationException("当前飞牛地址与任务设备不一致或任务缺少设备地址，已停止恢复检查。");
                return provider;
            }

            private async Task<T> RunAsync<T>(Func<INasStorageProvider, CancellationToken, Task<T>> operation)
            {
                if (Volatile.Read(ref _closing) != 0) throw new ObjectDisposedException(nameof(IsolatedRecoverySession));
                await _operation.WaitAsync(_owner._managerCts.Token).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref _closing) != 0) throw new ObjectDisposedException(nameof(IsolatedRecoverySession));
                    return await operation(CheckedProvider(), _owner._managerCts.Token).ConfigureAwait(false);
                }
                finally { _operation.Release(); }
            }

            public Task<ExperimentalProjectionRecoveryInspector.Inspection?> InspectAsync() =>
                RunAsync((provider, token) => new ExperimentalProjectionRecoveryLocator().LocateAsync(provider, _destination, token));

            public Task<ExperimentalStableLockRecovery.Inspection?> InspectStableLockAsync() =>
                RunAsync((provider, token) => new ExperimentalStableLockRecovery().InspectAsync(provider, _destination, token));

            public Task<ExperimentalInitializationLockRecovery.Inspection?> InspectInitializationLockAsync() =>
                RunAsync((provider, token) => new ExperimentalInitializationLockRecovery().InspectAsync(provider, _task, token));

            public Task<ExperimentalProjectionRecovery.Recovered> RecoverAsync(
                ExperimentalProjectionRecoveryInspector.Inspection expected, string acknowledgement) =>
                RunAsync((provider, token) => new ExperimentalProjectionRecovery().RecoverAfterOwnerStoppedAsync(provider, _destination, expected, acknowledgement, token));

            public Task ReleaseStableLockAsync(ExperimentalStableLockRecovery.Inspection expected, string acknowledgement) =>
                RunAsync(async (provider, token) => { await new ExperimentalStableLockRecovery().ReleaseAfterOwnerStoppedAsync(provider, _destination, expected, acknowledgement, token).ConfigureAwait(false); return true; });

            public Task ReleaseInitializationLockAsync(ExperimentalInitializationLockRecovery.Inspection expected, string acknowledgement) =>
                RunAsync(async (provider, token) => { await new ExperimentalInitializationLockRecovery().ReleaseAfterOwnerStoppedAsync(provider, _task, expected, acknowledgement, token).ConfigureAwait(false); return true; });

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _closing, 1) != 0) return;
                await _operation.WaitAsync().ConfigureAwait(false);
                try { _owner.EndIsolatedRecovery(_task.TaskId, _gate); }
                finally { _operation.Release(); }
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
                        SyncTask? task;
                        lock (_taskLifecycleLock)
                        {
                            task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId);
                            if (_stopping || task == null || _removingTasks.Contains(taskId) || _recoverySessions.ContainsKey(taskId) || !task.Enabled || task.IsUserPaused || task.State == TaskSyncState.Paused || task.State == TaskSyncState.Removing || task.State == TaskSyncState.ConfigurationError) continue;
                        }

                        // 等待获取全局并发信号量槽位
                        await _concurrencySemaphore.WaitAsync(token).ConfigureAwait(false);

                        CancellationTokenSource taskCts;
                        TaskCompletionSource completion;
                        lock (_taskLifecycleLock)
                        {
                            if (_stopping || !_settings.Tasks.Contains(task) || _removingTasks.Contains(taskId) || _recoverySessions.ContainsKey(taskId) ||
                                _runningCompletions.ContainsKey(taskId) || !task.Enabled || task.IsUserPaused ||
                                task.State == TaskSyncState.Paused || task.State == TaskSyncState.Removing ||
                                task.State == TaskSyncState.ConfigurationError)
                            {
                                _concurrencySemaphore.Release();
                                continue;
                            }
                            taskCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            _runningCts[taskId] = taskCts;
                            _runningCompletions[taskId] = completion;
                            _runningBatchIds[taskId] = Guid.NewGuid().ToString("N");
                        }

                        // 异步执行单个任务
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await ExecuteSingleTaskSyncAsync(task, taskCts.Token).ConfigureAwait(false);
                            }
                            finally
                            {
                                bool notifyPaused = false;
                                lock (_taskLifecycleLock)
                                {
                                    _runningCts.TryRemove(taskId, out _);
                                    _runningCompletions.Remove(taskId);
                                    _runningBatchIds.TryRemove(taskId, out _);
                                    if (task.IsUserPaused && !_removingTasks.Contains(taskId) && task.State != TaskSyncState.Paused)
                                    {
                                        task.State = TaskSyncState.Paused;
                                        task.LastSyncStatus = "已暂停";
                                        notifyPaused = true;
                                    }
                                }
                                taskCts.Dispose();
                                try { _concurrencySemaphore.Release(); }
                                finally { completion.TrySetResult(); }
                                if (notifyPaused) TaskStateChanged?.Invoke(task);
                            }
                        });
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

        private async Task ExecuteSingleTaskSyncAsync(SyncTask task, CancellationToken taskToken)
        {
            taskToken.ThrowIfCancellationRequested();
            if ((_settings.IsVersionedBetaProfile && !task.IsExperimentalBackup) || (task.IsExperimentalBackup
                ? !UseExperimentalVersionedBackup || task.ProviderType != StorageProviderType.FnOsPersonal ||
                  !string.Equals(task.BackupStorageFormat, IsolatedVersionedFormat, StringComparison.Ordinal)
                : task.BackupStorageFormat != null))
            {
                task.State = TaskSyncState.ConfigurationError;
                task.LastSyncStatus = "备份格式不受支持";
                task.LastError = "任务的备份格式与当前同步引擎不匹配，已阻止同步。";
                AppendTaskLog(task, "ERROR", task.LastError);
                TaskStateChanged?.Invoke(task);
                return;
            }
            if (task.Strategy == BackupStrategy.Mirror &&
                (!task.IsExperimentalBackup || !task.MirrorDeletionEnabled || task.ScheduleMode != TaskSyncScheduleMode.ManualOnly || !(_settings.IsInMemoryOnly ||
                    (_settings.IsVersionedBetaTestProfile && _settings.HasIsolatedTestBoundary) ||
                    (_settings.IsVersionedBetaProfile && !_settings.IsVersionedBetaTestProfile))))
            {
                task.State = TaskSyncState.ConfigurationError;
                task.LastSyncStatus = "镜像未授权或不支持当前设置";
                task.LastError = "镜像备份需要新版备份任务、明确授权并使用仅手动模式。";
                AppendTaskLog(task, "ERROR", task.LastError);
                TaskStateChanged?.Invoke(task);
                return;
            }
            try { EnsureSeparateFromLegacyTasks(task); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                task.State = TaskSyncState.ConfigurationError;
                task.LastSyncStatus = "旧任务目标冲突或无法确认";
                task.LastError = ex.Message;
                AppendTaskLog(task, "ERROR", ex.Message);
                TaskStateChanged?.Invoke(task);
                return;
            }
            try { _settings.EnsureIsolatedTestTask(task); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                task.State = TaskSyncState.ConfigurationError;
                task.LastError = ex.Message;
                task.LastSyncStatus = "隔离测试路径异常";
                AppendTaskLog(task, "ERROR", ex.Message);
                TaskStateChanged?.Invoke(task);
                return;
            }
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

            // The active fnOS connection must never redirect a local/SMB task into personal storage.
            var provider = task.ProviderType == StorageProviderType.FnOsPersonal ? StorageProviderSupplier?.Invoke() : null;
            if (task.ProviderType == StorageProviderType.FnOsPersonal &&
                (!_isNasOnline || provider == null || provider.ProviderType != StorageProviderType.FnOsPersonal || !provider.IsAuthenticated))
            {
                task.State = TaskSyncState.OfflineWaiting;
                task.LastSyncStatus = "NAS 未连接，等待重新连接";
                task.LastError = "飞牛个人空间未连接，本次未执行备份";
                AppendTaskLog(task, "WARN", task.LastError);
                _settings.Save();
                TaskStateChanged?.Invoke(task);
                return;
            }
            if (provider is FnOsPersonalStorageProvider fn &&
                ((!string.IsNullOrWhiteSpace(task.NasUsername) && !string.Equals(task.NasUsername, fn.CurrentUsername, StringComparison.Ordinal)) ||
                 (task.IsExperimentalBackup && string.IsNullOrWhiteSpace(task.NasUsername) && _settings.IsVersionedBetaProfile && !_settings.IsVersionedBetaTestProfile)))
            {
                task.State = TaskSyncState.ConfigurationError;
                task.LastSyncStatus = "NAS 账号不匹配";
                task.LastError = "当前连接的账号与任务账号不一致或任务缺少账号信息，本次未执行备份";
                AppendTaskLog(task, "ERROR", task.LastError);
                _settings.Save();
                TaskStateChanged?.Invoke(task);
                return;
            }
            if (task.IsExperimentalBackup && provider is FnOsPersonalStorageProvider device &&
                ((!string.IsNullOrWhiteSpace(task.NasDeviceId) && !device.MatchesServerAddress(task.NasDeviceId)) ||
                 (string.IsNullOrWhiteSpace(task.NasDeviceId) && _settings.IsVersionedBetaProfile && !_settings.IsVersionedBetaTestProfile)))
            {
                task.State = TaskSyncState.ConfigurationError;
                task.LastSyncStatus = "NAS 设备地址不匹配";
                task.LastError = "当前飞牛地址与任务设备不一致或任务缺少设备地址，本次未执行备份";
                AppendTaskLog(task, "ERROR", task.LastError);
                _settings.Save();
                TaskStateChanged?.Invoke(task);
                return;
            }

            // 每个任务独立实例化 LongNameEngine 与 ManifestManager，彻底隔离多任务字典与临时映射
            // 根据任务实际的存储提供程序动态配置安全阈值 (fnOS 个人空间上传接口安全阈值 251 字节，SMB 共享保持 255 字节)
            var taskLongNameEngine = LongNameEngine.CreateForProvider(provider ?? (task.ProviderType == FNSyncPlus.Core.Storage.StorageProviderType.FnOsPersonal ? new FNSyncPlus.Core.Storage.FnOsPersonalStorageProvider() : null));
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
                taskToken.ThrowIfCancellationRequested();
                task.State = TaskSyncState.Syncing;
                task.CurrentProgress = 0;
                task.CurrentFile = string.Empty;
                task.CurrentSpeedBytesPerSec = 0;
                task.LastError = null;
                task.QueuedChangesCount = 0;
                task.LastDetectedChange = null;
                TaskStateChanged?.Invoke(task);

                _pendingRenames.TryRemove(task.TaskId, out var renames);

                SyncProgressReport report;
                int reusedFiles = 0;
                if (task.IsExperimentalBackup)
                {
                    if (!UseExperimentalVersionedBackup || provider == null)
                        throw new InvalidOperationException("实验性备份必须使用隔离会话和已连接的飞牛存储");
                    if (!task.RequiresNewExperimentalBackup &&
                        (!_isolatedProjections.ContainsKey(task.TaskId) || task.Strategy == BackupStrategy.Mirror))
                    {
                        task.CurrentFile = "正在核验隔离备份的已有物理目录及发布记录";
                        TaskStateChanged?.Invoke(task);
                        var discovery = new ExperimentalProjectionDiscovery();
                        var found = task.ExperimentalFirstPublicationPending
                            ? await discovery.DiscoverFirstPublicationAsync(provider, task.DestinationFolder, taskToken).ConfigureAwait(false)
                            : await discovery.DiscoverAsync(provider, task.DestinationFolder, taskToken).ConfigureAwait(false);
                        if (found != null) _isolatedProjections[task.TaskId] = new IsolatedProjectionState(found.Plan, found.Publication);
                    }
                    AppendTaskLog(task, "INFO", "隔离试验：正在准备版本化内容及物理候选目录。");
                    var experimental = new ExperimentalVersionedBackup();
                    int settlingDelay = Math.Clamp(task.SettlingDelaySeconds > 0 ? task.SettlingDelaySeconds : 3, 1, 60);
                    experimental.ProgressChanged += message =>
                    {
                        task.CurrentFile = message;
                        TaskStateChanged?.Invoke(task);
                    };
                    bool initializing = task.RequiresNewExperimentalBackup;
                    var result = initializing
                        ? string.IsNullOrWhiteSpace(task.ExperimentalExistingRoot)
                            ? await experimental.CreateNewAsync(provider, task.SourceFolder, task.DestinationFolder, taskToken, task.Strategy, settlingDelay, task.ExperimentalInitializationId, task.MirrorDeletionEnabled).ConfigureAwait(false)
                            : await experimental.CreateNewFromExistingAsync(provider, task.SourceFolder, task.DestinationFolder, task.ExperimentalExistingRoot, taskToken, task.Strategy, settlingDelay, task.ExperimentalInitializationId, task.MirrorDeletionEnabled).ConfigureAwait(false)
                        : await experimental.RunAsync(provider, task.SourceFolder, task.DestinationFolder, taskToken, task.Strategy, settlingDelay, task.MirrorDeletionEnabled).ConfigureAwait(false);
                    task.RequiresNewExperimentalBackup = false;
                    if (initializing)
                    {
                        task.ExperimentalPendingInitialization = false;
                        task.ExperimentalFirstPublicationPending = true;
                        _settings.Save(); // A committed first version can now be reattached after restart.
                    }
                    reusedFiles = result.ReusedFiles;
                    if (result.ResumedInitialization && !_isolatedProjections.ContainsKey(task.TaskId))
                    {
                        var existingPublication = await new ExperimentalProjectionDiscovery()
                            .DiscoverFirstPublicationAsync(provider, task.DestinationFolder, taskToken).ConfigureAwait(false);
                        if (existingPublication != null)
                            _isolatedProjections[task.TaskId] = new IsolatedProjectionState(existingPublication.Plan, existingPublication.Publication);
                    }
                    bool hasPrevious = _isolatedProjections.TryGetValue(task.TaskId, out var previousProjection);
                    if (hasPrevious && previousProjection!.Plan.VersionId == result.Snapshot.Id)
                    {
                        task.CurrentFile = "文件无变化，正在核验现有隔离物理目录";
                        TaskStateChanged?.Invoke(task);
                        await new ExperimentalFirstProjection().VerifyPublishedAsync(provider, task.DestinationFolder,
                            previousProjection!.Plan, previousProjection.Publication, taskToken).ConfigureAwait(false);
                    }
                    else
                    {
                        task.CurrentFile = "正在准备并核验隔离候选目录";
                        TaskStateChanged?.Invoke(task);
                        var plan = await new ExperimentalPhysicalSnapshot().PrepareAsync(provider, task.DestinationFolder, taskToken).ConfigureAwait(false);
                        if (plan.VersionId != result.Snapshot.Id)
                            throw new IOException("准备物理目录期间备份版本已变化，拒绝发布");
                        task.CurrentFile = "正在核验并发布隔离物理目录";
                        TaskStateChanged?.Invoke(task);
                        ExperimentalFirstProjection.Published publication;
                        if (hasPrevious)
                        {
                            var next = await new ExperimentalNextProjection().PublishAsync(provider, task.DestinationFolder,
                                previousProjection!.Plan, previousProjection.Publication, plan, taskToken).ConfigureAwait(false);
                            publication = next.Current;
                        }
                        else
                        {
                            publication = await new ExperimentalFirstProjection().PublishAsync(provider, task.DestinationFolder, plan, taskToken).ConfigureAwait(false);
                        }
                        _isolatedProjections[task.TaskId] = new IsolatedProjectionState(plan, publication);
                    }
                    task.ExperimentalPendingInitialization = false;
                    task.ExperimentalFirstPublicationPending = false;
                    if (reusedFiles > 0) AppendTaskLog(task, "INFO", $"隔离试验：已复用 {reusedFiles} 个 NAS 文件，上传 {result.UploadedFiles} 个文件。");
                    report = new SyncProgressReport {
                        State = SyncState.Completed, TotalFiles = result.Snapshot.Manifest.Files.Count,
                        ScannedFiles = result.Snapshot.Manifest.Files.Count, UploadedFiles = result.UploadedFiles,
                        UnchangedFiles = result.UnchangedFiles,
                        BytesTransferred = result.UploadedBytes,
                        LongNameFiles = result.Snapshot.Manifest.Files.Count(f => f.IsLongName)
                    };
                }
                else
                {
                    report = await syncEngine.RunSyncAsync(
                        task.SourceFolder,
                        task.DestinationFolder,
                        provider,
                        task.Strategy,
                        renames,
                        taskToken).ConfigureAwait(false);
                }

                if (report.State != SyncState.Completed && report.FailedFiles == 0)
                    throw new IOException($"同步未完成（状态：{report.State}）：{report.StatusMessage}");

                task.LastSyncTime = DateTime.Now;
                task.LastScannedFiles = report.TotalFiles;
                task.LastUploadedFiles = report.UploadedFiles;
                task.LastUnchangedFiles = report.UnchangedFiles;
                task.LastLongNameFiles = report.LongNameFiles;
                task.LastFailedFiles = report.FailedFiles;
                task.LastTransferredBytes = report.BytesTransferred;
                if (task.IsExperimentalBackup) task.LastVersionedUploadedBytes = report.BytesTransferred;

                task.CurrentSpeedBytesPerSec = 0;

                if (report.FailedFiles > 0)
                {
                    task.State = TaskSyncState.Error;
                    task.LastFailureTime = DateTime.Now;
                    if (report.UploadedFiles == 0)
                    {
                        task.CurrentFile = $"备份失败 (共 {report.FailedFiles} 个文件同步失败)";
                        task.LastSyncStatus = $"备份失败 ({report.FailedFiles} 个文件失败)";
                    }
                    else
                    {
                        task.CurrentFile = $"部分失败 ({report.UploadedFiles} 个已上传, {report.FailedFiles} 个失败；未提交清单)";
                        task.LastSyncStatus = $"部分失败 ({report.UploadedFiles} 已上传, {report.FailedFiles} 失败；未提交清单)";
                    }
                    task.LastError = $"有 {report.FailedFiles} 个文件同步失败，未提交新清单；可在任务详情查看完整诊断日志";

                    task.CurrentProgress = report.TotalFiles > 0
                        ? (double)(report.UploadedFiles + report.UnchangedFiles) / report.TotalFiles * 100.0
                        : 0.0;

                    foreach (var item in syncEngine.FailedItems)
                    {
                        _logService.LogTask(
                            taskId: task.TaskId,
                            level: "ERROR",
                            stage: "UPLOAD",
                            message: $"文件同步失败: {item.ErrorMessage}",
                            localRel: item.RelativePath,
                            errorMsg: item.ErrorMessage,
                            retryCount: item.RetryCount,
                            syncBatchId: _runningBatchIds.TryGetValue(task.TaskId, out var batchId) ? batchId : null);
                    }
                }
                else
                {
                    task.State = TaskSyncState.Completed;
                    task.LastSuccessTime = DateTime.Now;
                    task.CurrentProgress = 100.0;
                    string trialLabel = _settings.IsVersionedBetaProfile && !_settings.IsVersionedBetaTestProfile ? "新版备份试用" : "隔离试验";
                    task.CurrentFile = task.IsExperimentalBackup ? $"{trialLabel}已提交：复用 {reusedFiles}，上传 {report.UploadedFiles}；物理目录已发布" : "所有文件均已同步更新至最新状态";
                    task.LastSyncStatus = task.IsExperimentalBackup
                        ? $"{trialLabel}物理目录已发布（复用 {reusedFiles}，上传 {report.UploadedFiles}）"
                        : "同步成功完成 (已最新)";
                }

                _settings.Save();
                AppendTaskLog(task, report.FailedFiles > 0 ? "WARN" : "INFO", task.LastSyncStatus);
                TaskStateChanged?.Invoke(task);
            }
            catch (OperationCanceledException)
            {
                task.State = TaskSyncState.Paused;
                task.LastSyncStatus = "已暂停/取消";
                AppendTaskLog(task, "INFO", task.LastSyncStatus);
                TaskStateChanged?.Invoke(task);
            }
            catch (Exception ex)
            {
                task.State = TaskSyncState.Error;
                task.LastFailureTime = DateTime.Now;
                task.LastError = ex.Message;
                if (task.LastScannedFiles == 0 && syncEngine.LastScannedCount > 0)
                {
                    task.LastScannedFiles = syncEngine.LastScannedCount;
                }
                if (task.LastFailedFiles == 0)
                {
                    task.LastFailedFiles = Math.Max(1, task.LastScannedFiles);
                }
                bool isInit = ex.Message.Contains("初始化") ||
                              ex.Message.Contains("清单") ||
                              ex.Message.Contains("创建") ||
                              ex.Message.Contains("目标目录") ||
                              ex.Message.Contains("扫描");
                task.LastSyncStatus = isInit ? "备份初始化失败" : "备份失败";
                task.CurrentFile = $"{task.LastSyncStatus}: {ex.Message}";
                AppendTaskLog(task, "ERROR", $"任务异常中断: {ex.Message}");
                _settings.Save();
                TaskStateChanged?.Invoke(task);
            }
        }

        #endregion

        #region 独立监控与定时器管理

        private void InitializeAllTaskAutomations()
        {
            foreach (var task in _settings.Tasks)
            {
                if (task.IsUserPaused)
                {
                    task.State = TaskSyncState.Paused;
                    task.LastSyncStatus = "已暂停";
                    continue;
                }
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

                    // 启动补偿检查：如果是智能自动备份模式，延迟 5 秒触发一次查漏补缺
                    if (task.ScheduleMode == TaskSyncScheduleMode.SmartAuto)
                    {
                        _ = Task.Delay(5000).ContinueWith(_ =>
                        {
                            lock (_taskLifecycleLock)
                            {
                                if (!_stopping && _settings.Tasks.Contains(task) && task.ScheduleMode == TaskSyncScheduleMode.SmartAuto &&
                                    task.Enabled && !task.IsUserPaused && !task.IsRunning && task.State != TaskSyncState.Paused)
                                {
                                    AppendTaskLog(task, "INFO", "软件启动，执行智能自动备份初始补偿检查...");
                                    TriggerSync(task.TaskId);
                                }
                            }
                        });
                    }
                }
            }
        }

        private void ApplyTaskAutomation(SyncTask task)
        {
            StopTaskAutomation(task.TaskId);

            // 暂停状态或禁用状态绝对不启动变动监听与定时轮询
            if (!task.Enabled || task.IsUserPaused || task.State == TaskSyncState.Paused) return;

            // 根据 ScheduleMode 统一配置调度标记
            switch (task.ScheduleMode)
            {
                case TaskSyncScheduleMode.SmartAuto:
                    task.RealtimeWatchEnabled = true;
                    task.AutoSyncEnabled = true;
                    break;
                case TaskSyncScheduleMode.RealtimeOnly:
                    task.RealtimeWatchEnabled = true;
                    task.AutoSyncEnabled = false;
                    break;
                case TaskSyncScheduleMode.PeriodicOnly:
                    task.RealtimeWatchEnabled = false;
                    task.AutoSyncEnabled = true;
                    break;
                case TaskSyncScheduleMode.ManualOnly:
                    task.RealtimeWatchEnabled = false;
                    task.AutoSyncEnabled = false;
                    task.IsWatching = false;
                    task.NextScheduledTime = null;
                    TaskStateChanged?.Invoke(task);
                    return;
            }

            int settlingMs = Math.Max(1, task.SettlingDelaySeconds > 0 ? task.SettlingDelaySeconds : 3) * 1000;

            // 1. 本地目录变动实时监听 (具备防写抖动 Settling Delay 与重命名追踪)
            if (task.RealtimeWatchEnabled && Directory.Exists(task.SourceFolder))
            {
                try
                {
                    var watcher = new FileSystemWatcher(task.SourceFolder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };

                    Timer? debounceTimer = null;
                    debounceTimer = new Timer(_ =>
                    {
                        lock (_taskLifecycleLock)
                        {
                            if (_stopping || !_settings.Tasks.Contains(task) ||
                                task.ScheduleMode is not (TaskSyncScheduleMode.SmartAuto or TaskSyncScheduleMode.RealtimeOnly) ||
                                !_watcherDebounceTimers.TryGetValue(task.TaskId, out var activeTimer) || !ReferenceEquals(activeTimer, debounceTimer) ||
                                !task.Enabled || task.IsUserPaused || task.State == TaskSyncState.Paused ||
                                task.State == TaskSyncState.Removing || _removingTasks.Contains(task.TaskId)) return;
                            if (_runningCompletions.ContainsKey(task.TaskId))
                            {
                                // A change during a running scan must trigger another scan after it ends.
                                try { debounceTimer?.Change(settlingMs, Timeout.Infinite); }
                                catch (ObjectDisposedException) { }
                            }
                            else
                            {
                                AppendTaskLog(task, "INFO", "检测到本地文件变动且写入已平稳，正在触发自动增量同步...");
                                TriggerSync(task.TaskId);
                            }
                        }
                    }, null, Timeout.Infinite, Timeout.Infinite);

                    Action queueEvent = () =>
                    {
                        try { debounceTimer.Change(settlingMs, Timeout.Infinite); }
                        catch (ObjectDisposedException) { }
                    };

                    watcher.Changed += (s, e) =>
                    {
                        task.LastDetectedChange = $"修改: {e.Name}";
                        task.QueuedChangesCount++;
                        TaskStateChanged?.Invoke(task);
                        queueEvent();
                    };
                    watcher.Created += (s, e) =>
                    {
                        task.LastDetectedChange = $"新增: {e.Name}";
                        task.QueuedChangesCount++;
                        TaskStateChanged?.Invoke(task);
                        queueEvent();
                    };
                    watcher.Deleted += (s, e) =>
                    {
                        task.LastDetectedChange = $"删除: {e.Name}";
                        task.QueuedChangesCount++;
                        TaskStateChanged?.Invoke(task);
                        queueEvent();
                    };
                    watcher.Renamed += (s, e) =>
                    {
                        try
                        {
                            string oldRel = PathHelper.NormalizeRelPath(Path.GetRelativePath(task.SourceFolder, e.OldFullPath));
                            string newRel = PathHelper.NormalizeRelPath(Path.GetRelativePath(task.SourceFolder, e.FullPath));
                            var map = _pendingRenames.GetOrAdd(task.TaskId, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                            map[oldRel] = newRel;
                        }
                        catch { }

                        task.LastDetectedChange = $"重命名: {e.OldName} -> {e.Name}";
                        task.QueuedChangesCount++;
                        TaskStateChanged?.Invoke(task);
                        queueEvent();
                    };
                    watcher.Error += (s, e) =>
                    {
                        AppendTaskLog(task, "WARN", "本地变动监视器缓冲区溢出，正在排队全量重新扫描。");
                        queueEvent();
                    };

                    watcher.EnableRaisingEvents = true;
                    task.IsWatching = true;
                    _watchers[task.TaskId] = watcher;
                    _watcherDebounceTimers[task.TaskId] = debounceTimer;
                }
                catch (Exception ex)
                {
                    task.IsWatching = false;
                    AppendTaskLog(task, "WARN", $"无法启动文件变动监视: {ex.Message}");
                }
            }
            else
            {
                task.IsWatching = false;
            }

            // 2. 定时轮询增量同步与补偿检查
            if (task.AutoSyncEnabled && task.IntervalMinutes > 0)
            {
                task.NextScheduledTime = DateTime.Now.AddMinutes(task.IntervalMinutes);
                Timer? timer = null;
                timer = new Timer(_ =>
                {
                    lock (_taskLifecycleLock)
                    {
                        if (_stopping || !_settings.Tasks.Contains(task) || _removingTasks.Contains(task.TaskId) ||
                            task.ScheduleMode is not (TaskSyncScheduleMode.SmartAuto or TaskSyncScheduleMode.PeriodicOnly) ||
                            !_intervalTimers.TryGetValue(task.TaskId, out var activeTimer) || !ReferenceEquals(activeTimer, timer) ||
                            !task.Enabled || task.IsUserPaused || task.State == TaskSyncState.Paused) return;
                        task.NextScheduledTime = DateTime.Now.AddMinutes(task.IntervalMinutes);
                        TaskStateChanged?.Invoke(task);

                        if (!task.IsRunning)
                        {
                            string msg = task.ScheduleMode == TaskSyncScheduleMode.SmartAuto
                                ? "执行智能自动备份例行补偿检查..."
                                : "执行例行定时增量备份检查...";
                            AppendTaskLog(task, "INFO", msg);
                            TriggerSync(task.TaskId);
                        }
                    }
                }, null, TimeSpan.FromMinutes(task.IntervalMinutes), TimeSpan.FromMinutes(task.IntervalMinutes));

                _intervalTimers[task.TaskId] = timer;
            }
            else
            {
                task.NextScheduledTime = null;
            }

            TaskStateChanged?.Invoke(task);
        }

        private void StopTaskAutomation(string taskId)
        {
            var task = _settings.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task != null)
            {
                task.IsWatching = false;
                task.NextScheduledTime = null;
            }

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

        private static readonly System.Text.RegularExpressions.Regex PrefixRegex = new(
            @"^(?:\[\d{4}-\d{2}-\d{2}[^\]]*\]\s*|\[\d{2}:\d{2}:\d{2}(?:\.\d+)?\]\s*|\[(?:INFO|WARN|ERROR|DEBUG)\]\s*|\[SYNC\]\s*)+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string CleanLogMessage(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return string.Empty;
            return PrefixRegex.Replace(msg, "").TrimStart();
        }

        private void AppendTaskLog(SyncTask task, string level, string message)
        {
            string cleanMsg = CleanLogMessage(message);
            _runningBatchIds.TryGetValue(task.TaskId, out string? batchId);
            string formatted = $"[{DateTime.Now:HH:mm:ss}] [{level,-5}] [SYNC] {cleanMsg}" +
                (batchId == null ? string.Empty : $" [批次: {batchId}]");

            // 1. 存入持久化日志服务 (磁盘文件 + 内存)
            try
            {
                _logService.LogTask(new TaskLogEntry
                {
                    TaskId = task.TaskId,
                    Level = level,
                    Stage = "SYNC",
                    Message = cleanMsg,
                    SyncBatchId = batchId,
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
                var persistentLogs = _logService.GetTaskLogs(taskId);
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
            SyncTask[] tasks;
            lock (_taskLifecycleLock)
            {
                if (_stopping || (_nasStateInitialized && _isNasOnline == isOnline)) return;
                _nasStateInitialized = true;
                _isNasOnline = isOnline;
                tasks = _settings.Tasks.Where(t => t.ProviderType != StorageProviderType.LocalFileSystem).ToArray();
            }
            if (!isOnline)
            {
                foreach (var task in tasks)
                {
                    lock (_taskLifecycleLock)
                    {
                        if (_stopping || _isNasOnline) break;
                        if (!_settings.Tasks.Contains(task) || !task.IsRunning) continue;
                        task.State = TaskSyncState.OfflineWaiting;
                        task.LastSyncStatus = "NAS离线等待恢复";
                        TaskStateChanged?.Invoke(task);
                    }
                }
            }
            else
            {
                // 网络恢复时唤醒并触发重连补偿检查
                foreach (var task in tasks)
                {
                    lock (_taskLifecycleLock)
                    {
                        if (_stopping || !_isNasOnline) break;
                        if (!_settings.Tasks.Contains(task) || !task.Enabled || task.IsUserPaused || task.State == TaskSyncState.Paused) continue;
                        if (task.State == TaskSyncState.OfflineWaiting)
                        {
                            task.State = TaskSyncState.Idle;
                            task.LastSyncStatus = "网络已恢复";
                            TaskStateChanged?.Invoke(task);
                        }
                        if (task.ScheduleMode == TaskSyncScheduleMode.ManualOnly) continue;
                        AppendTaskLog(task, "INFO", "NAS 连接已恢复，触发重连补偿检查...");
                        if (_runningCompletions.TryGetValue(task.TaskId, out var running))
                        {
                            _ = running.Task.ContinueWith(_ =>
                            {
                                lock (_taskLifecycleLock)
                                {
                                    if (!_managerCts.IsCancellationRequested && _isNasOnline && task.Enabled &&
                                        !task.IsUserPaused && task.State != TaskSyncState.Paused && task.ScheduleMode != TaskSyncScheduleMode.ManualOnly)
                                        TriggerSync(task.TaskId);
                                }
                            }, TaskScheduler.Default);
                        }
                        else TriggerSync(task.TaskId);
                    }
                }
            }
        }

        #endregion

        private Task BeginShutdown()
        {
            Task completion;
            lock (_taskLifecycleLock)
            {
                if (_shutdownTask != null) return _shutdownTask;
                _stopping = true;
                completion = Task.WhenAll(_runningCompletions.Values.Select(v => v.Task)
                    .Concat(_recoverySessions.Values.Select(v => v.Task)).Append(_queueWorkerTask));
                _shutdownTask = completion;
            }
            _managerCts.Cancel();
            foreach (var w in _watchers.Values) { try { w.Dispose(); } catch { } }
            foreach (var t in _watcherDebounceTimers.Values) { try { t.Dispose(); } catch { } }
            foreach (var t in _intervalTimers.Values) { try { t.Dispose(); } catch { } }
            foreach (var cts in _runningCts.Values) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
            _watchers.Clear();
            _watcherDebounceTimers.Clear();
            _intervalTimers.Clear();
            _ = completion.ContinueWith(_ => DisposeResources(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return completion;
        }

        /// <summary>Stops scheduling and waits for all running tasks to actually finish. Timeout leaves the manager stopped.</summary>
        public async Task<bool> ShutdownAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            Task completion = BeginShutdown();
            Task deadline = Task.Delay(timeout);
            if (await Task.WhenAny(completion, deadline).ConfigureAwait(false) != completion) return false;
            try { await completion.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            Task logs = _logService.FlushPendingTaskLogsAsync();
            if (await Task.WhenAny(logs, deadline).ConfigureAwait(false) != logs) return false;
            await logs.ConfigureAwait(false);
            return true;
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
            _queueSignal.Dispose();
            _concurrencySemaphore.Dispose();
        }

        public void Dispose()
        {
            _ = BeginShutdown();
        }
    }
}
