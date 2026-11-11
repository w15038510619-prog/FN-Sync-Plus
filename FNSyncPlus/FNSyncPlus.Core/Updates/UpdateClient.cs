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
}

public sealed record UpdateCheckResult(bool HasUpdate, string Version, string Notes, string ManifestUrl, string Error);

/// <summary>
/// Orchestrates check → verify → download → staged install. Installation itself is delegated to
/// <see cref="VersionedInstallLayout"/> and must be gated by <see cref="BackupActivityGate"/>.
/// Unattended installs require both a configured HTTPS manifest URL and an explicit opt-in.
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

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, ClientUpdateIdentity identity, CancellationToken token = default)
    {
        _settings.LastCheckUtc = DateTimeOffset.UtcNow;
        if (!Uri.TryCreate(_settings.ManifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _settings.LastResult = "未配置 HTTPS 更新地址";
            return new UpdateCheckResult(false, "", "", "", _settings.LastResult);
        }
        try
        {
            string json = await _http.GetStringAsync(uri, token).ConfigureAwait(false);
            var verification = _verifier.Verify(json, identity, Channel, currentVersion, DateTimeOffset.UtcNow);
            if (!verification.Ok)
            {
                _settings.LastResult = verification.Error;
                return new UpdateCheckResult(false, "", "", "", verification.Error);
            }
            string version = verification.Version!.ToString();
            if (string.Equals(_settings.SkippedVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                _settings.LastResult = $"版本 {version} 已被用户跳过";
                return new UpdateCheckResult(false, version, "", "", _settings.LastResult);
            }
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            string notes = doc.RootElement.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
            _settings.LastResult = $"发现新版本 {version}";
            return new UpdateCheckResult(true, version, notes, _settings.ManifestUrl, "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            _settings.LastResult = $"检查更新失败：{ex.Message}";
            return new UpdateCheckResult(false, "", "", "", _settings.LastResult);
        }
    }

    /// <summary>Re-fetches and re-verifies the manifest, then downloads the package to <paramref name="directory"/>.</summary>
    public async Task<UpdateDownloadResult> DownloadAsync(Version currentVersion, ClientUpdateIdentity identity, string directory,
        IProgress<long>? progress = null, CancellationToken token = default)
    {
        if (!Uri.TryCreate(_settings.ManifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new UpdateDownloadResult(false, "", "未配置 HTTPS 更新地址");
        string json = await _http.GetStringAsync(uri, token).ConfigureAwait(false);
        var verification = _verifier.Verify(json, identity, Channel, currentVersion, DateTimeOffset.UtcNow);
        if (!verification.Ok) return new UpdateDownloadResult(false, "", verification.Error);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        string url = doc.RootElement.GetProperty("url").GetString() ?? "";
        string hash = doc.RootElement.GetProperty("sha256").GetString() ?? "";
        long size = doc.RootElement.GetProperty("size").GetInt64();
        string version = verification.Version!.ToString();
        string path = Path.Combine(directory, $"FN-Sync-Plus-{version}-x64.zip");
        using var downloader = new UpdateDownloader(_http);
        var result = await downloader.DownloadAsync(url, path, hash, size, progress, token).ConfigureAwait(false);
        _settings.LastResult = result.Ok ? $"已下载 {version}" : result.Error;
        return result;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
