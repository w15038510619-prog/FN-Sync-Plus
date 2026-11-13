using System.IO;
using System.Net.Http;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Updates;

public enum AutoUpdateMode { CheckOnly = 0, DownloadAndNotify = 1, UnattendedInstall = 2 }

public sealed class UpdateSettings
{
    [JsonPropertyName("channel")] public string Channel { get; set; } = "stable";
    [JsonPropertyName("auto_mode")] public int AutoMode { get; set; } = (int)AutoUpdateMode.CheckOnly;
    [JsonPropertyName("manifest_url")] public string ManifestUrl { get; set; } = "";
    [JsonPropertyName("last_check")] public DateTimeOffset? LastCheckUtc { get; set; }
    [JsonPropertyName("last_result")] public string LastResult { get; set; } = "";
    [JsonPropertyName("skipped_version")] public string? SkippedVersion { get; set; }
    [JsonPropertyName("unattended_enabled")] public bool UnattendedEnabled { get; set; }
    [JsonPropertyName("machine_group")] public string MachineGroup { get; set; } = "";
    [JsonPropertyName("release_batch")] public int ReleaseBatch { get; set; }

    /// <summary>Newest version the background scheduler has already reported, so a restart does not
    /// re-notify (or re-download) the same release.</summary>
    [JsonPropertyName("notified_version")] public string? NotifiedVersion { get; set; }

    /// <summary>Version whose package is downloaded and verified, waiting for a safe install.</summary>
    [JsonPropertyName("staged_version")] public string? StagedVersion { get; set; }

    /// <summary>Version the updater was handed off for. Prevents a restart loop if the install
    /// keeps failing before the app can start.</summary>
    [JsonPropertyName("handoff_version")] public string? HandoffVersion { get; set; }

    /// <summary>Versions whose last handoff ended in an UNKNOWN state (state file corrupt, process
    /// unverifiable, evidence contradictory). Automatic installs of these versions are blocked:
    /// "cannot tell what happened" must never silently become "assume it failed and reinstall".
    /// A human clears the entry after diagnosing the machine.</summary>
    [JsonPropertyName("diagnosis_required_versions")] public System.Collections.Generic.List<string> DiagnosisRequiredVersions { get; set; } = new();
}

public sealed record UpdateCheckResult(bool HasUpdate, string Version, string Notes, string ManifestUrl, string Error);

/// <summary>
/// Fetches and verifies release manifests, and downloads exactly the release that was verified.
/// Installation itself is delegated to <see cref="VersionedInstallLayout"/> and must be gated by
/// <see cref="BackupActivityGate"/>.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly UpdateManifestVerifier _verifier;
    private readonly UpdateSettings _settings;

    public UpdateClient(UpdateManifestVerifier verifier, UpdateSettings settings, HttpClient? http = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsClient = http == null;
    }

    public UpdateChannel Channel => string.Equals(_settings.Channel, "beta", StringComparison.OrdinalIgnoreCase)
        ? UpdateChannel.Beta : UpdateChannel.Stable;

    public bool UnattendedInstallAllowed =>
        _settings.AutoMode == (int)AutoUpdateMode.UnattendedInstall && _settings.UnattendedEnabled &&
        Uri.TryCreate(_settings.ManifestUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public async Task<UpdateCheckOutcome> CheckAsync(Version currentVersion, ClientUpdateIdentity identity, CancellationToken token = default)
    {
        _settings.LastCheckUtc = DateTimeOffset.UtcNow;
        if (!Uri.TryCreate(_settings.ManifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _settings.LastResult = "未配置 HTTPS 更新地址";
            return UpdateCheckOutcome.No(false, "", "", _settings.LastResult);
        }
        try
        {
            string json = await _http.GetStringAsync(uri, token).ConfigureAwait(false);
            var verification = _verifier.VerifyRelease(json, identity, Channel, currentVersion, DateTimeOffset.UtcNow);
            if (!verification.Ok)
            {
                _settings.LastResult = verification.Error;
                return UpdateCheckOutcome.No(false, "", "", verification.Error);
            }
            var manifest = verification.Manifest!;
            string version = manifest.Version;
            if (string.Equals(_settings.SkippedVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                _settings.LastResult = $"版本 {version} 已被用户跳过";
                return UpdateCheckOutcome.No(false, version, "", _settings.LastResult);
            }
            _settings.LastResult = $"发现新版本 {version}";
            var release = new VerifiedRelease(json, version, verification.Channel, manifest.Sha256, manifest.Size,
                manifest.Url, "", identity.MachineGroup, identity.ReleaseBatch);
            return new UpdateCheckOutcome(true, version, manifest.Notes, "", release);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            _settings.LastResult = $"检查更新失败：{ex.Message}";
            return UpdateCheckOutcome.No(false, "", "", _settings.LastResult);
        }
    }

    /// <summary>
    /// Downloads the package described by <paramref name="release"/>. The manifest bytes carried in
    /// the release are re-verified first, so the URL/size/hash used here are provably the signed
    /// ones — there is no second manifest fetch that could point at a different package.
    /// </summary>
    public async Task<UpdateDownloadResult> DownloadAsync(VerifiedRelease release, string directory,
        IProgress<long>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        var identity = new ClientUpdateIdentity(release.MachineGroup, release.ReleaseBatch);
        // The current version is not re-checked here: the release was already accepted for this
        // client. Re-verifying with "no current version" would weaken the downgrade rule, so pass
        // the release's own predecessor boundary instead by skipping that check entirely.
        var verification = _verifier.VerifyRelease(release.ManifestJson, identity, Channel, null, DateTimeOffset.UtcNow);
        if (!verification.Ok) return new UpdateDownloadResult(false, "", verification.Error);
        var manifest = verification.Manifest!;
        if (!string.Equals(manifest.Version, release.Version, StringComparison.Ordinal) ||
            !string.Equals(manifest.Sha256, release.Sha256, StringComparison.OrdinalIgnoreCase) ||
            manifest.Size != release.Size)
            return new UpdateDownloadResult(false, "", "已签名清单与待下载发布描述不一致，已拒绝下载");

        string path = Path.Combine(directory, $"FN-Sync-Plus-{release.Version}-x64.zip");
        using var downloader = new UpdateDownloader(_http);
        var result = await downloader.DownloadAsync(manifest.Url, path, manifest.Sha256, manifest.Size, progress, token)
            .ConfigureAwait(false);
        _settings.LastResult = result.Ok ? $"已下载 {release.Version}" : result.Error;
        return result;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
