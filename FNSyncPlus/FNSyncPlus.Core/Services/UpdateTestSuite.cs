using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.Core.Services;

public sealed record UpdateSuiteCase(string Name, bool Passed, string Detail);

public sealed record UpdateSuiteReport(IReadOnlyList<UpdateSuiteCase> Cases, int Passed, int Failed, TimeSpan Duration)
{
    public bool AllPassed => Failed == 0;

    public string ToSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"自动更新引擎测试：{Passed} PASS / {Failed} FAIL，用时 {Duration.TotalSeconds:F1}s");
        foreach (var item in Cases.Where(c => !c.Passed))
            builder.AppendLine($"  FAIL {item.Name} -> {item.Detail}");
        return builder.ToString();
    }
}

/// <summary>
/// Exercises the update pipeline against a real loopback HTTPS server with a throw-away key pair.
/// Nothing here contacts the internet, a company update server or a real NAS; the signing key is
/// generated per run and never written to the repository.
/// </summary>
public sealed class UpdateTestSuite
{
    public async Task<UpdateSuiteReport> RunAsync(string workspace, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(workspace)) throw new ArgumentException("测试工作目录不能为空", nameof(workspace));
        Directory.CreateDirectory(workspace);
        var cases = new List<UpdateSuiteCase>();
        var clock = Stopwatch.StartNew();
        var (privatePem, publicPem) = UpdateManifestSigner.CreateKeyPair();
        using var signingKey = ECDsa.Create();
        signingKey.ImportFromPem(privatePem);
        var verifier = UpdateManifestVerifier.FromPem(publicPem);
        var identity = new ClientUpdateIdentity("lab", 5);

        async Task Case(string name, Func<Task<(bool Passed, string Detail)>> body)
        {
            try
            {
                var (passed, detail) = await body().ConfigureAwait(false);
                cases.Add(new UpdateSuiteCase(name, passed, detail));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                cases.Add(new UpdateSuiteCase(name, false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        string packageDir = Path.Combine(workspace, "packages");
        Directory.CreateDirectory(packageDir);
        byte[] package = BuildPackage(Path.Combine(packageDir, "payload-src"), "0.5.0");
        string packagePath = Path.Combine(packageDir, "FN-Sync-Plus-0.5.0-x64.zip");
        await File.WriteAllBytesAsync(packagePath, package, token);
        string packageHash = Convert.ToHexString(SHA256.HashData(package));

        using var server = new LoopbackHttpsServer(token);
        string baseUrl = server.BaseUrl;

        UpdateManifest BuildManifest(Action<UpdateManifest>? customise = null)
        {
            var manifest = new UpdateManifest
            {
                Channel = "stable", Version = "0.5.0", MinVersion = "0.1.0", ReleasedAt = DateTimeOffset.UtcNow,
                Notes = "test release", Url = baseUrl + "/pkg.zip", Sha256 = packageHash, Size = package.Length,
                Batch = 1, Audience = new List<string> { "*" }, MinCompatibleClient = "0.0.0"
            };
            customise?.Invoke(manifest);
            return manifest;
        }

        void Publish(UpdateManifest manifest)
        {
            server.Set("/manifest.json", Encoding.UTF8.GetBytes(UpdateManifestSigner.Sign(manifest, signingKey)));
            server.Set("/pkg.zip", package);
        }

        await Case("签名正确的清单被接受", () =>
        {
            Publish(BuildManifest());
            var result = verifier.Verify(Encoding.UTF8.GetString(server.Get("/manifest.json")), identity, UpdateChannel.Stable,
                new Version(0, 4, 0), DateTimeOffset.UtcNow);
            return Task.FromResult((result.Ok, result.Error));
        });

        await Case("清单发布后被篡改版本号则签名校验失败", () =>
        {
            string json = Encoding.UTF8.GetString(server.Get("/manifest.json")).Replace("\"0.5.0\"", "\"0.5.1\"");
            var result = verifier.Verify(json, identity, UpdateChannel.Stable, new Version(0, 4, 0), DateTimeOffset.UtcNow);
            return Task.FromResult((!result.Ok && result.Error.Contains("签名", StringComparison.Ordinal), result.Error));
        });

        await Case("清单发布后被篡改更新包哈希则拒绝", () =>
        {
            string json = Encoding.UTF8.GetString(server.Get("/manifest.json")).Replace(packageHash, new string('A', 64));
            var result = verifier.Verify(json, identity, UpdateChannel.Stable, new Version(0, 4, 0), DateTimeOffset.UtcNow);
            return Task.FromResult((!result.Ok, result.Error));
        });

        await Case("缺少签名的清单被拒绝", () =>
        {
            string json = System.Text.Json.JsonSerializer.Serialize(BuildManifest());
            var result = verifier.Verify(json, identity, UpdateChannel.Stable, new Version(0, 4, 0), DateTimeOffset.UtcNow);
            return Task.FromResult((!result.Ok && result.Error.Contains("签名", StringComparison.Ordinal), result.Error));
        });

        await Case("降级或重复安装被拒绝", () =>
        {
            var result = verifier.Verify(Encoding.UTF8.GetString(server.Get("/manifest.json")), identity, UpdateChannel.Stable,
                new Version(0, 5, 0), DateTimeOffset.UtcNow);
            return Task.FromResult((!result.Ok && result.Error.Contains("降级", StringComparison.Ordinal), result.Error));
        });

        await Case("Stable 客户端不接收 Beta 清单", () =>
        {
            Publish(BuildManifest(m => m.Channel = "beta"));
            var result = verifier.Verify(Encoding.UTF8.GetString(server.Get("/manifest.json")), identity, UpdateChannel.Stable,
                new Version(0, 4, 0), DateTimeOffset.UtcNow);
            return Task.FromResult((!result.Ok && result.Error.Contains("通道", StringComparison.Ordinal), result.Error));
        });

        await Case("Beta 客户端可以接收 Beta 清单", () =>
        {
            var result = verifier.Verify(Encoding.UTF8.GetString(server.Get("/manifest.json")), identity, UpdateChannel.Beta,
                new Version(0, 4, 0), DateTimeOffset.UtcNow);
            return Task.FromResult((result.Ok, result.Error));
        });

        await Case("非 HTTPS 更新地址被拒绝", () =>
        {
            Publish(BuildManifest(m => m.Url = "http://example.invalid/pkg.zip"));
            var result = verifier.Verify(Encoding.UTF8.GetString(server.Get("/manifest.json")), identity, UpdateChannel.Stable,
                new Version(0, 4, 0), DateTimeOffset.UtcNow);
            return Task.FromResult((!result.Ok && result.Error.Contains("HTTPS", StringComparison.Ordinal), result.Error));
        });

        await Case("发布批次未覆盖当前客户端时拒绝", () =>
        {
            Publish(BuildManifest(m => m.Batch = 9));
            var result = verifier.Verify(Encoding.UTF8.GetString(server.Get("/manifest.json")), identity, UpdateChannel.Stable,
                new Version(0, 4, 0), DateTimeOffset.UtcNow);
            return Task.FromResult((!result.Ok && result.Error.Contains("批次", StringComparison.Ordinal), result.Error));
        });

        await Case("受众不匹配时拒绝", () =>
        {
            Publish(BuildManifest(m => m.Audience = new List<string> { "other-group" }));
            var result = verifier.Verify(Encoding.UTF8.GetString(server.Get("/manifest.json")), identity, UpdateChannel.Stable,
                new Version(0, 4, 0), DateTimeOffset.UtcNow);
            return Task.FromResult((!result.Ok && result.Error.Contains("受众", StringComparison.Ordinal) ||
                result.Error.Contains("批次", StringComparison.Ordinal), result.Error));
        });

        await Case("当前客户端过旧时要求手动安装", () =>
        {
            Publish(BuildManifest(m => m.MinCompatibleClient = "0.4.5"));
            var result = verifier.Verify(Encoding.UTF8.GetString(server.Get("/manifest.json")), identity, UpdateChannel.Stable,
                new Version(0, 4, 0), DateTimeOffset.UtcNow);
            return Task.FromResult((!result.Ok && result.Error.Contains("过旧", StringComparison.Ordinal), result.Error));
        });

        await Case("下载并校验 SHA-256 成功后落地", async () =>
        {
            Publish(BuildManifest());
            string downloadDir = Path.Combine(workspace, "download-ok");
            using var client = new UpdateClient(verifier, Settings(baseUrl), server.CreateClient());
            var download = await client.DownloadAsync(new Version(0, 4, 0), identity, downloadDir, token: token)
                .ConfigureAwait(false);
            bool match = download.Ok && File.Exists(download.FilePath) &&
                         Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(download.FilePath, token))) == packageHash;
            return (match, download.Error);
        });

        await Case("下载内容与清单哈希不符时删除临时文件", async () =>
        {
            var tampered = (byte[])package.Clone();
            tampered[^1] ^= 0xFF;
            Publish(BuildManifest());
            server.Set("/pkg.zip", tampered);   // after Publish: it also sets /pkg.zip
            string downloadDir = Path.Combine(workspace, "download-bad");
            using var client = new UpdateClient(verifier, Settings(baseUrl), server.CreateClient());
            var download = await client.DownloadAsync(new Version(0, 4, 0), identity, downloadDir, token: token)
                .ConfigureAwait(false);
            bool cleaned = !File.Exists(download.FilePath + ".partial");
            server.Set("/pkg.zip", package);
            return (!download.Ok && cleaned && download.Error.Contains("SHA-256", StringComparison.Ordinal), download.Error);
        });

        await Case("断点续传从已有部分文件继续并发送 Range", async () =>
        {
            Publish(BuildManifest());
            string downloadDir = Path.Combine(workspace, "download-resume");
            Directory.CreateDirectory(downloadDir);
            string target = Path.Combine(downloadDir, "FN-Sync-Plus-0.5.0-x64.zip");
            int half = package.Length / 2;
            await File.WriteAllBytesAsync(target + ".partial", package[..half], token);
            server.SawRangeRequest = false;
            using var client = new UpdateClient(verifier, Settings(baseUrl), server.CreateClient());
            var download = await client.DownloadAsync(new Version(0, 4, 0), identity, downloadDir, token: token)
                .ConfigureAwait(false);
            bool ok = download.Ok && server.SawRangeRequest &&
                      Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(download.FilePath, token))) == packageHash;
            return (ok, $"ok={download.Ok} range={server.SawRangeRequest} {download.Error}");
        });

        await Case("服务器返回超出清单大小的数据时中止", async () =>
        {
            var bigger = new byte[package.Length + 4096];
            Array.Copy(package, bigger, package.Length);
            Publish(BuildManifest());
            server.Set("/pkg.zip", bigger);     // after Publish: it also sets /pkg.zip
            using var downloader = new UpdateDownloader(server.CreateClient());
            var result = await downloader.DownloadAsync(baseUrl + "/pkg.zip", Path.Combine(workspace, "oversize.zip"),
                packageHash, package.Length, null, token).ConfigureAwait(false);
            server.Set("/pkg.zip", package);
            return (!result.Ok && result.Error.Contains("超过", StringComparison.Ordinal), result.Error);
        });

        await Case("解压拒绝 ../ 路径穿越条目", async () =>
        {
            string zip = Path.Combine(workspace, "zip-slip.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escape.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("escape");
            }
            var result = await UpdatePackageExtractor.ExtractAsync(zip, Path.Combine(workspace, "extract-slip"), token)
                .ConfigureAwait(false);
            return (!result.Ok && !File.Exists(Path.Combine(workspace, "escape.txt")), result.Error);
        });

        await Case("解压拒绝盘符或绝对路径条目", async () =>
        {
            string zip = Path.Combine(workspace, "zip-absolute.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("C:/Windows/evil.dll");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("evil");
            }
            var result = await UpdatePackageExtractor.ExtractAsync(zip, Path.Combine(workspace, "extract-abs"), token)
                .ConfigureAwait(false);
            return (!result.Ok, result.Error);
        });

        await Case("解压拒绝缺少主程序的包", async () =>
        {
            string zip = Path.Combine(workspace, "zip-incomplete.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("readme.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("no payload");
            }
            var result = await UpdatePackageExtractor.ExtractAsync(zip, Path.Combine(workspace, "extract-incomplete"), token)
                .ConfigureAwait(false);
            return (!result.Ok && result.Error.Contains("主程序", StringComparison.Ordinal), result.Error);
        });

        await Case("正常安装切换指针、写入日志并保留旧版本目录", async () =>
        {
            string root = Path.Combine(workspace, "layout-ok");
            var layout = new VersionedInstallLayout(root);
            await InstallSeedAsync(layout, "0.4.0", token);
            var installer = new UpdateInstaller(layout, _ => Task.FromResult(true));
            var outcome = await installer.ApplyAsync(packagePath, "0.5.0", packageHash, Path.Combine(workspace, "work-ok"), token)
                .ConfigureAwait(false);
            bool ok = outcome.Ok && layout.ActiveVersion == "0.5.0" && layout.PreviousVersion == "0.4.0" &&
                      Directory.Exists(layout.VersionDirectory("0.4.0")) && Directory.Exists(layout.VersionDirectory("0.5.0")) &&
                      File.ReadAllText(layout.JournalPath).Contains("installed 0.5.0", StringComparison.Ordinal);
            return (ok, outcome.Error);
        });

        await Case("启动验证失败时回滚指针但保留失败目录以便排查", async () =>
        {
            string root = Path.Combine(workspace, "layout-rollback");
            var layout = new VersionedInstallLayout(root);
            await InstallSeedAsync(layout, "0.4.0", token);
            var installer = new UpdateInstaller(layout, _ => Task.FromResult(false));
            var outcome = await installer.ApplyAsync(packagePath, "0.5.0", packageHash, Path.Combine(workspace, "work-rb"), token)
                .ConfigureAwait(false);
            bool ok = !outcome.Ok && outcome.RolledBack && layout.ActiveVersion == "0.4.0" &&
                      Directory.Exists(layout.VersionDirectory("0.5.0"));
            return (ok, $"ok={outcome.Ok} rolledBack={outcome.RolledBack} active={layout.ActiveVersion} err={outcome.Error}");
        });

        await Case("更新包哈希与清单不一致时拒绝安装", async () =>
        {
            string root = Path.Combine(workspace, "layout-hash");
            var layout = new VersionedInstallLayout(root);
            await InstallSeedAsync(layout, "0.4.0", token);
            var installer = new UpdateInstaller(layout, _ => Task.FromResult(true));
            var outcome = await installer.ApplyAsync(packagePath, "0.5.0", new string('B', 64), Path.Combine(workspace, "work-hash"), token)
                .ConfigureAwait(false);
            return (!outcome.Ok && layout.ActiveVersion == "0.4.0", outcome.Error);
        });

        await Case("同一版本目录已存在时拒绝覆盖", async () =>
        {
            string root = Path.Combine(workspace, "layout-dup");
            var layout = new VersionedInstallLayout(root);
            await InstallSeedAsync(layout, "0.4.0", token);
            var installer = new UpdateInstaller(layout, _ => Task.FromResult(true));
            var outcome = await installer.ApplyAsync(packagePath, "0.4.0", packageHash, Path.Combine(workspace, "work-dup"), token)
                .ConfigureAwait(false);
            return (!outcome.Ok && outcome.Error.Contains("已安装", StringComparison.Ordinal), outcome.Error);
        });

        await Case("数据格式比旧版本新时禁止降级", async () =>
        {
            string root = Path.Combine(workspace, "layout-downgrade");
            var layout = new VersionedInstallLayout(root);
            await InstallSeedAsync(layout, "0.4.0", token);
            var installer = new UpdateInstaller(layout, _ => Task.FromResult(true));
            await installer.ApplyAsync(packagePath, "0.5.0", packageHash, Path.Combine(workspace, "work-dg"), token)
                .ConfigureAwait(false);
            bool refused = !installer.TryRollback(currentDataFormat: 3, oldestSupportedFormat: 2, out string reason);
            bool allowed = installer.TryRollback(currentDataFormat: 2, oldestSupportedFormat: 2, out _);
            return (refused && allowed && layout.ActiveVersion == "0.4.0", reason);
        });

        await Case("配置格式标记更高时旧程序拒绝启动并保留标记", () =>
        {
            string path = Path.Combine(workspace, "data-format", "format.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"format\":5,\"app_version\":\"0.9.0\"}");
            var guard = new DataFormatGuard(path, supportedFormat: 2);
            bool claimed = guard.TryClaim("0.4.0", out string error);
            bool markerUntouched = File.ReadAllText(path).Contains("\"format\":5", StringComparison.Ordinal);
            var newer = new DataFormatGuard(path, supportedFormat: 6);
            return Task.FromResult((!claimed && markerUntouched && error.Contains("停止运行", StringComparison.Ordinal) && newer.TryClaim("0.9.0", out _),
                error));
        });

        await Case("备份进行中时安装被推迟而不是终止备份", async () =>
        {
            bool running = true, blocked = false;
            var gate = new BackupActivityGate(() => running, () => false, () => blocked = true, () => blocked,
                () => Task.CompletedTask);
            var task = gate.WaitForSafeInstallAsync(TimeSpan.FromMilliseconds(400), token);
            await Task.Delay(120, token);
            running = false;
            var readiness = await task.ConfigureAwait(false);
            return (blocked && readiness.Decision == InstallDecision.Proceed, readiness.Reason);
        });

        await Case("存在不明确的提交或锁时拒绝安装", async () =>
        {
            var gate = new BackupActivityGate(() => false, () => true, () => true, () => true, () => Task.CompletedTask);
            var readiness = await gate.WaitForSafeInstallAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            bool inspect = gate.Inspect().Decision == InstallDecision.Refuse;
            return (readiness.Decision == InstallDecision.Refuse && inspect, readiness.Reason);
        });

        await Case("等待备份结束超时后推迟安装", async () =>
        {
            var gate = new BackupActivityGate(() => true, () => false, () => true, () => true, () => Task.CompletedTask);
            var readiness = await gate.WaitForSafeInstallAsync(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
            return (readiness.Decision == InstallDecision.Defer, readiness.Reason);
        });

        await Case("健康标记早于本次启动尝试时不算成功", () =>
        {
            string root = Path.Combine(workspace, "health-stale");
            var tracker = new LaunchHealthTracker(root);
            tracker.MarkHealthy("0.5.0", 111);          // marker from an older launch
            Thread.Sleep(20);
            tracker.RecordAttempt("0.5.0", 222);
            var stale = tracker.Evaluate("0.5.0", processStillRunning: true);
            tracker.MarkHealthy("0.5.0", 222);
            var fresh = tracker.Evaluate("0.5.0", processStillRunning: true);
            return Task.FromResult((!stale.Healthy && fresh.Healthy, $"{stale.Reason} / {fresh.Reason}"));
        });

        await Case("未配置 HTTPS 地址时检查更新返回明确错误", async () =>
        {
            using var client = new UpdateClient(verifier, new UpdateSettings { ManifestUrl = "" });
            var result = await client.CheckAsync(new Version(0, 4, 0), identity, token).ConfigureAwait(false);
            return (!result.HasUpdate && result.Error.Contains("HTTPS", StringComparison.Ordinal), result.Error);
        });

        await Case("检查更新可发现新版本并读取发布说明", async () =>
        {
            Publish(BuildManifest());
            using var client = new UpdateClient(verifier, Settings(baseUrl), server.CreateClient());
            var result = await client.CheckAsync(new Version(0, 4, 0), identity, token).ConfigureAwait(false);
            return (result.HasUpdate && result.Version == "0.5.0" && result.Notes == "test release", result.Error);
        });

        return new UpdateSuiteReport(cases, cases.Count(c => c.Passed), cases.Count(c => !c.Passed), clock.Elapsed);
    }

    private static UpdateSettings Settings(string baseUrl) => new()
    {
        ManifestUrl = baseUrl + "/manifest.json", Channel = "stable",
        AutoMode = (int)AutoUpdateMode.DownloadAndNotify
    };

    /// <summary>Creates an already-installed version so update tests have a "previous" pointer.</summary>
    private static async Task InstallSeedAsync(VersionedInstallLayout layout, string version, CancellationToken token)
    {
        string seed = Path.Combine(Path.GetTempPath(), "fnsync-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "FNSyncPlus.WinUI.exe"), "seed", token);
        await File.WriteAllTextAsync(Path.Combine(seed, "version.txt"), version, token);
        var outcome = await layout.InstallAsync(version, seed, _ => Task.FromResult(true), token).ConfigureAwait(false);
        if (!outcome.Ok) throw new InvalidOperationException("无法准备测试安装: " + outcome.Error);
    }

    private static byte[] BuildPackage(string sourceDirectory, string version)
    {
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "FNSyncPlus.WinUI.exe"), "app payload " + version);
        File.WriteAllText(Path.Combine(sourceDirectory, "FNSyncPlus.Updater.exe"), "updater payload");
        File.WriteAllText(Path.Combine(sourceDirectory, "FNSyncPlus.Core.dll"), "core");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "Assets"));
        File.WriteAllText(Path.Combine(sourceDirectory, "Assets", "readme.txt"), "assets");
        string zip = Path.Combine(sourceDirectory, "..", $"pkg-{version}-{Guid.NewGuid():N}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(sourceDirectory, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        byte[] bytes = File.ReadAllBytes(zip);
        File.Delete(zip);
        return bytes;
    }
}

/// <summary>
/// Minimal HTTPS server bound to 127.0.0.1 with an ephemeral self-signed certificate. Used only by
/// tests so the real download path (TLS, redirects, Range requests) is exercised without a real
/// update server. The client trusts exactly this certificate and nothing else.
/// </summary>
internal sealed class LoopbackHttpsServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public LoopbackHttpsServer(CancellationToken token)
    {
        _certificate = CreateCertificate();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"https://127.0.0.1:{port}";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _ = AcceptLoopAsync(_cts.Token);
    }

    public string BaseUrl { get; }
    public bool SawRangeRequest { get; set; }

    public void Set(string path, byte[] content)
    {
        lock (_sync) _routes[path] = content;
    }

    public byte[] Get(string path)
    {
        lock (_sync) return _routes.TryGetValue(path, out var content) ? content : Array.Empty<byte>();
    }

    public HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate != null && certificate.Thumbprint == _certificate.Thumbprint
        };
        return new HttpClient(handler);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = HandleAsync(client, token);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                using var ssl = new SslStream(client.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(_certificate, false, System.Security.Authentication.SslProtocols.Tls12, false)
                    .ConfigureAwait(false);
                string request = await ReadHeadersAsync(ssl, token).ConfigureAwait(false);
                string first = request.Split("\r\n")[0];
                string path = first.Split(' ')[1].Split('?')[0];
                long from = 0;
                bool range = false;
                foreach (string line in request.Split("\r\n"))
                {
                    if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                    string value = line["Range:".Length..].Trim();
                    if (value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        string spec = value["bytes=".Length..].Split('-')[0];
                        if (long.TryParse(spec, out long parsed)) { from = parsed; range = true; }
                    }
                }
                if (range) SawRangeRequest = true;
                byte[] body = Get(path);
                if (body.Length == 0)
                {
                    await WriteAsync(ssl, 404, "Not Found", Array.Empty<byte>(), null, token).ConfigureAwait(false);
                    return;
                }
                byte[] payload = range && from < body.Length ? body[(int)from..] : body;
                string? contentRange = range && from < body.Length ? $"bytes {from}-{body.Length - 1}/{body.Length}" : null;
                await WriteAsync(ssl, range && from < body.Length ? 206 : 200, "OK", payload, contentRange, token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or SocketException)
            {
                // Handshake or transport failure: the test asserts on client behaviour, not on this.
            }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        var buffer = new List<byte>(1024);
        var single = new byte[1];
        while (buffer.Count < 16384)
        {
            int read = await stream.ReadAsync(single, token).ConfigureAwait(false);
            if (read == 0) break;
            buffer.Add(single[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n') break;
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private static async Task WriteAsync(Stream stream, int status, string reason, byte[] body, string? contentRange,
        CancellationToken token)
    {
        var header = new StringBuilder();
        header.Append($"HTTP/1.1 {status} {reason}\r\n");
        header.Append("Content-Type: application/octet-stream\r\n");
        header.Append($"Content-Length: {body.Length}\r\n");
        header.Append("Accept-Ranges: bytes\r\n");
        if (contentRange != null) header.Append($"Content-Range: {contentRange}\r\n");
        header.Append("Connection: close\r\n\r\n");
        byte[] head = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(head, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=FN Sync Plus Test Update Server", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var eku = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }; // serverAuth
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _certificate.Dispose();
        _cts.Dispose();
    }
}
