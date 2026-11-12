namespace FNSyncPlus.Core.Updates;

public sealed record ApplyOutcome(bool Ok, string Version, string Error, bool RolledBack, string TargetDirectory);

/// <summary>
/// End-to-end staged installation of an already downloaded package: re-verify the archive hash,
/// extract it into a private staging area, install it as a new immutable version directory, and
/// only then switch the launch pointer. Nothing in this class touches NAS data or user settings;
/// updating must be requested through <see cref="UpdateService"/>, which gates on backup activity.
/// </summary>
public sealed class UpdateInstaller
{
    private readonly VersionedInstallLayout _layout;
    private readonly Func<CancellationToken, Task<bool>> _launchVerification;

    public UpdateInstaller(VersionedInstallLayout layout, Func<CancellationToken, Task<bool>> launchVerification)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _launchVerification = launchVerification ?? throw new ArgumentNullException(nameof(launchVerification));
    }

    /// <summary>
    /// Installs a release that was already verified against the pinned signing key. The local file
    /// is re-bound to the signed size and hash here, so a package swapped between download and
    /// install is rejected before anything is extracted.
    /// </summary>
    public async Task<ApplyOutcome> ApplyAsync(VerifiedRelease release, string workDirectory,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (string.IsNullOrWhiteSpace(release.LocalPackagePath))
            return new ApplyOutcome(false, release.Version, "更新包路径为空", false, "");
        long size;
        try { size = new FileInfo(release.LocalPackagePath).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ApplyOutcome(false, release.Version, $"无法读取更新包：{ex.Message}", false, "");
        }
        if (size != release.Size)
        {
            _layout.Journal($"refused {release.Version}: size mismatch (expected {release.Size}, got {size})");
            return new ApplyOutcome(false, release.Version, "更新包大小与已签名清单不一致，拒绝安装", false, "");
        }
        return await ApplyAsync(release.LocalPackagePath, release.Version, release.Sha256, workDirectory, token)
            .ConfigureAwait(false);
    }

    public async Task<ApplyOutcome> ApplyAsync(string packagePath, string version, string expectedSha256,
        string workDirectory, CancellationToken token = default)
    {
        if (!Version.TryParse(version, out _)) return new ApplyOutcome(false, version, "版本号无效", false, "");
        if (!Directory.Exists(workDirectory)) Directory.CreateDirectory(workDirectory);
        if (Directory.Exists(_layout.VersionDirectory(version)))
            return new ApplyOutcome(false, version, "该版本已安装，拒绝覆盖现有程序目录", false, _layout.VersionDirectory(version));
        string actual;
        try { actual = UpdatePackageExtractor.HashFile(packagePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ApplyOutcome(false, version, $"无法读取更新包：{ex.Message}", false, "");
        }
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _layout.Journal($"refused {version}: package hash mismatch (expected {Short(expectedSha256)}, got {Short(actual)})");
            return new ApplyOutcome(false, version, "更新包 SHA-256 与清单不一致，拒绝安装", false, "");
        }

        var extracted = await UpdatePackageExtractor.ExtractAsync(packagePath, workDirectory, token).ConfigureAwait(false);
        if (!extracted.Ok)
        {
            _layout.Journal($"refused {version}: extraction failed: {extracted.Error}");
            return new ApplyOutcome(false, version, extracted.Error, false, "");
        }

        _layout.Journal($"applying {version} from {extracted.FileCount} files / {extracted.TotalBytes} bytes");
        var outcome = await _layout.InstallAsync(version, extracted.PayloadDirectory, _launchVerification, token)
            .ConfigureAwait(false);
        _layout.Journal(outcome.Ok
            ? $"apply ok {version}"
            : $"apply failed {version}: {outcome.Error}{(outcome.RolledBack ? " (rolled back)" : "")}");
        if (outcome.Ok) TryDelete(extracted.PayloadDirectory);
        return new ApplyOutcome(outcome.Ok, outcome.VersionInstalled, outcome.Error, outcome.RolledBack, outcome.TargetDirectory);
    }

    /// <summary>
    /// Rolls the launch pointer back to the previous version. Refused when the on-disk data format
    /// is newer than the older binary understands — a downgrade write could corrupt user data, so
    /// leaving the current version in place is the safe outcome.
    /// </summary>
    public bool TryRollback(int currentDataFormat, int oldestSupportedFormat, out string reason)
    {
        if (!VersionedInstallLayout.CanDowngrade(currentDataFormat, oldestSupportedFormat))
        {
            reason = "当前数据格式比上一版本更新，禁止降级以免写坏数据";
            _layout.Journal($"rollback refused: data format {currentDataFormat} > supported {oldestSupportedFormat}");
            return false;
        }
        bool ok = _layout.RestorePointer();
        reason = ok ? "已回滚到上一版本" : "没有可回滚的上一版本";
        return ok;
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
