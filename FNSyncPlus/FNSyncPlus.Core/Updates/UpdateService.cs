using System.Text.Json;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.Core.Updates;

public sealed record UpdateInstallPlan(bool Ready, string Executable, string Arguments, string Reason);

/// <summary>
/// UI-facing coordinator for update checks, downloads and gated installation. It owns no UI and no
/// network policy of its own: policy lives in <see cref="UpdateManifestVerifier"/>, safety in
/// <see cref="BackupActivityGate"/>, and filesystem layout in <see cref="VersionedInstallLayout"/>.
///
/// Installation is always delegated to the separate <c>FNSyncPlus.Updater.exe</c> process, because
/// the running program has to exit before its own files are replaced.
/// </summary>
public sealed class UpdateService
{
    public const string UpdaterFileName = "FNSyncPlus.Updater.exe";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly UpdateManifestVerifier? _verifier;
    private readonly string _settingsPath;
    private readonly TaskManager? _taskManager;
    private readonly BackupActivityGate? _gate;
    private readonly VersionedInstallLayout? _layout;

    public UpdateService(UpdateSettings settings, string settingsPath, string installRoot, TaskManager? taskManager = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        _taskManager = taskManager;
        var verifier = PinnedUpdateKey.TryCreateVerifier(out string keyError);
        _verifier = verifier;
        SupportMessage = verifier == null
            ? keyError
            : $"已就绪（通道 {CurrentChannelName}，当前版本 {CurrentVersion}）";
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            _layout = null;
            SupportMessage = "未配置安装目录，自动更新不可用";
            return;
        }
        string? managedRoot = VersionedInstallLayout.TryFindRootFromAppDirectory(installRoot);
        _layout = managedRoot == null ? null : new VersionedInstallLayout(managedRoot);
        if (_layout == null && verifier != null)
            SupportMessage = "当前为免安装（ZIP）运行，只能检查更新，不能原地升级。请使用安装程序版本。";
        if (_layout != null && taskManager != null)
        {
            _gate = new BackupActivityGate(
                hasRunningOrQueuedWork: () => taskManager.HasRunningOrQueuedWork,
                hasUnclearCommitOrLock: () => taskManager.HasUnclearCommitOrLock,
                blockNewWork: () => { taskManager.EnterMaintenanceHold(); return taskManager.IsMaintenanceHoldActive; },
                newWorkBlocked: () => taskManager.IsMaintenanceHoldActive,
                saveConfiguration: SaveAsync);
        }
    }

    public UpdateSettings Settings { get; }
    public string SupportMessage { get; }
    public bool CanCheck => _verifier != null;
    public bool CanInstall => _verifier != null && _layout != null && _gate != null;
    public string? InstallRoot => _layout?.Root;
    public string? ActiveVersion => _layout?.ActiveVersion;
    public string? PreviousVersion => _layout?.PreviousVersion;
    public string CurrentChannelName => string.Equals(Settings.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? "Beta" : "Stable";

    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 4, 0);

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token = default)
    {
        if (_verifier == null) return new UpdateCheckResult(false, "", "", "", SupportMessage);
        using var client = new UpdateClient(_verifier, Settings);
        var result = await client.CheckAsync(CurrentVersion, Identity(), token).ConfigureAwait(false);
        Save();
        return result;
    }

    public async Task<UpdateDownloadResult> DownloadAsync(IProgress<long>? progress = null, CancellationToken token = default)
    {
        if (_verifier == null) return new UpdateDownloadResult(false, "", SupportMessage);
        string directory = DownloadDirectory;
        using var client = new UpdateClient(_verifier, Settings);
        var result = await client.DownloadAsync(CurrentVersion, Identity(), directory, progress, token).ConfigureAwait(false);
        Save();
        return result;
    }

    /// <summary>Non-blocking readiness used to explain why an install is deferred in the UI.</summary>
    public InstallReadiness InspectInstallReadiness()
    {
        if (_gate == null) return new InstallReadiness(InstallDecision.Refuse, SupportMessage);
        return _gate.Inspect();
    }

    /// <summary>
    /// Blocks new backups, waits for running ones, saves configuration, and returns the command line
    /// to start the updater. The caller must shut the application down afterwards; this method never
    /// kills a running backup.
    /// </summary>
    public async Task<(InstallReadiness Readiness, UpdateInstallPlan Plan)> PrepareInstallAsync(
        string version, string packagePath, string sha256, TimeSpan budget, CancellationToken token = default)
    {
        if (_gate == null || _layout == null)
            return (new InstallReadiness(InstallDecision.Refuse, SupportMessage),
                new UpdateInstallPlan(false, "", "", SupportMessage));
        var readiness = await _gate.WaitForSafeInstallAsync(budget, token).ConfigureAwait(false);
        if (readiness.Decision != InstallDecision.Proceed)
            return (readiness, new UpdateInstallPlan(false, "", "", readiness.Reason));

        string updater = Path.Combine(_layout.Root, UpdaterFileName);
        if (!File.Exists(updater))
            return (readiness, new UpdateInstallPlan(false, "", "", $"安装目录缺少更新器：{updater}"));
        string arguments = $"--layout \"{_layout.Root}\" --apply --package \"{packagePath}\" --version \"{version}\" " +
                           $"--sha256 \"{sha256}\" --wait-pid {Environment.ProcessId} --wait-seconds 120";
        return (readiness, new UpdateInstallPlan(true, updater, arguments, "备份已安全停止，可以安装"));
    }

    public string DownloadDirectory =>
        Path.Combine(_layout?.Root ?? Path.GetTempPath(), ".downloads");

    public ClientUpdateIdentity Identity() => new(Settings.MachineGroup, Settings.ReleaseBatch);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A settings write failure must not break the caller; the UI surfaces the next check result.
        }
    }

    private Task SaveAsync()
    {
        Save();
        return Task.CompletedTask;
    }

    public static UpdateSettings LoadSettings(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path)) ?? new UpdateSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new UpdateSettings();
    }
}
