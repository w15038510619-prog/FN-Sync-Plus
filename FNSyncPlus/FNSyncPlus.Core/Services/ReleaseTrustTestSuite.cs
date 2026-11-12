using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Attack matrix for the update trust chain (P0-3).
///
/// The updater runs as its own process, so it cannot rely on the UI having validated anything. Each
/// case here hands <see cref="ReleaseHandoff"/> a descriptor that an attacker could plausibly write
/// (wrong key, swapped package, mismatched version, traversal path, ...) and asserts it is refused.
/// A refusal must never depend on a command-line flag.
/// </summary>
public sealed class ReleaseTrustTestSuite
{
    public Task<IReadOnlyList<UpdateSuiteCase>> RunAsync(string workspace, CancellationToken token = default)
    {
        Directory.CreateDirectory(workspace);
        var cases = new List<UpdateSuiteCase>();

        var (pinnedPrivate, pinnedPublic) = UpdateManifestSigner.CreateKeyPair();
        var (otherPrivate, _) = UpdateManifestSigner.CreateKeyPair();
        using var pinnedKey = ECDsa.Create();
        pinnedKey.ImportFromPem(pinnedPrivate);
        using var otherKey = ECDsa.Create();
        otherKey.ImportFromPem(otherPrivate);
        var verifier = UpdateManifestVerifier.FromPem(pinnedPublic);

        string root = Path.Combine(workspace, "install");
        Directory.CreateDirectory(root);
        var layout = new VersionedInstallLayout(root);
        string seed = Path.Combine(workspace, "seed-0.4.0");
        Directory.CreateDirectory(seed);
        File.WriteAllText(Path.Combine(seed, "FNSyncPlus.WinUI.exe"), "seed");
        layout.InstallAsync("0.4.0", seed, _ => Task.FromResult(true), CancellationToken.None).GetAwaiter().GetResult();

        byte[] package = Encoding.UTF8.GetBytes("payload for 0.5.0");
        string packagePath = Path.Combine(root, "FN-Sync-Plus-0.5.0-x64.zip");
        File.WriteAllBytes(packagePath, package);
        string packageHash = Convert.ToHexString(SHA256.HashData(package));

        UpdateManifest Manifest(Action<UpdateManifest>? customise = null)
        {
            var manifest = new UpdateManifest
            {
                Channel = "stable", Version = "0.5.0", MinVersion = "0.1.0", ReleasedAt = DateTimeOffset.UtcNow,
                Notes = "trust test", Url = "https://update.example.invalid/pkg.zip", Sha256 = packageHash,
                Size = package.Length, Batch = 1, Audience = new List<string> { "*" }, MinCompatibleClient = "0.0.0"
            };
            customise?.Invoke(manifest);
            return manifest;
        }

        string Signed(UpdateManifest manifest, ECDsa? key = null) => UpdateManifestSigner.Sign(manifest, key ?? pinnedKey);

        ReleaseDescriptor Descriptor(string json, Action<ReleaseDescriptor>? customise = null)
        {
            var descriptor = new ReleaseDescriptor
            {
                ManifestJson = json, Version = "0.5.0", PackagePath = packagePath,
                PackageSha256 = packageHash, PackageSize = package.Length,
                MachineGroup = "lab", ReleaseBatch = 5, RequestedChannelName = "stable"
            };
            customise?.Invoke(descriptor);
            return descriptor;
        }

        void Case(string name, Func<(bool Passed, string Detail)> body)
        {
            try
            {
                var (passed, detail) = body();
                cases.Add(new UpdateSuiteCase(name, passed, detail));
            }
            catch (Exception ex)
            {
                cases.Add(new UpdateSuiteCase(name, false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        ReleaseDescriptor DescriptorClone(ReleaseDescriptor source) => new()
        {
            ManifestJson = source.ManifestJson, Version = source.Version, PackagePath = source.PackagePath,
            PackageSha256 = source.PackageSha256, PackageSize = source.PackageSize, MachineGroup = source.MachineGroup,
            ReleaseBatch = source.ReleaseBatch, RequestedChannelName = source.RequestedChannelName
        };

        string goodJson = Signed(Manifest());
        var installed = new Version(0, 4, 0);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Case("正确签名且包一致时接受安装", () =>
        {
            var result = ReleaseHandoff.Verify(Descriptor(goodJson), verifier, installed, root, now);
            return (result.Ok && result.Release is { Version: "0.5.0" }, result.Error);
        });

        Case("清单版本号被篡改后签名失效", () =>
        {
            string tampered = goodJson.Replace("\"0.5.0\"", "\"0.6.0\"");
            var result = ReleaseHandoff.Verify(Descriptor(tampered), verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("签名", StringComparison.Ordinal), result.Error);
        });

        Case("未签名清单被拒绝", () =>
        {
            string unsigned = JsonSerializer.Serialize(Manifest());
            var result = ReleaseHandoff.Verify(Descriptor(unsigned), verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("签名", StringComparison.Ordinal), result.Error);
        });

        Case("使用其他密钥签名的清单被拒绝", () =>
        {
            string foreign = Signed(Manifest(), otherKey);
            var result = ReleaseHandoff.Verify(Descriptor(foreign), verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("签名", StringComparison.Ordinal), result.Error);
        });

        Case("空签名被拒绝", () =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(goodJson)!;
            node["signature"] = "";
            string json = node.ToJsonString();
            var result = ReleaseHandoff.Verify(Descriptor(json), verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("签名", StringComparison.Ordinal), result.Error);
        });

        Case("描述版本与已签名清单不一致时拒绝", () =>
        {
            var result = ReleaseHandoff.Verify(Descriptor(goodJson, d => d.Version = "0.5.1"), verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("版本号", StringComparison.Ordinal), result.Error);
        });

        Case("描述哈希与已签名清单不一致时拒绝", () =>
        {
            var result = ReleaseHandoff.Verify(Descriptor(goodJson, d => d.PackageSha256 = new string('A', 64)),
                verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("SHA-256", StringComparison.Ordinal), result.Error);
        });

        Case("描述大小与已签名清单不一致时拒绝", () =>
        {
            var result = ReleaseHandoff.Verify(Descriptor(goodJson, d => d.PackageSize = package.Length + 10),
                verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("大小", StringComparison.Ordinal), result.Error);
        });

        Case("下载后包被替换（长度相同）时拒绝", () =>
        {
            var descriptor = Descriptor(goodJson);
            byte[] swapped = (byte[])package.Clone();
            swapped[0] ^= 0xFF;
            string path = Path.Combine(root, "swapped-same-length.zip");
            File.WriteAllBytes(path, swapped);
            descriptor.PackagePath = path;
            var result = ReleaseHandoff.Verify(descriptor, verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("SHA-256", StringComparison.Ordinal), result.Error);
        });

        Case("下载后包被替换（长度不同）时拒绝", () =>
        {
            var descriptor = Descriptor(goodJson);
            string path = Path.Combine(root, "swapped-other-length.zip");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("short"));
            descriptor.PackagePath = path;
            var result = ReleaseHandoff.Verify(descriptor, verifier, installed, root, now);
            return (!result.Ok, result.Error);
        });

        Case("非 HTTPS 更新地址被拒绝", () =>
        {
            string json = Signed(Manifest(m => m.Url = "http://update.example.invalid/pkg.zip"));
            var result = ReleaseHandoff.Verify(Descriptor(json), verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("HTTPS", StringComparison.Ordinal), result.Error);
        });

        Case("Beta 包伪装成 Stable 通道时拒绝", () =>
        {
            string json = Signed(Manifest(m => m.Channel = "beta"));
            var result = ReleaseHandoff.Verify(Descriptor(json), verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("通道", StringComparison.Ordinal), result.Error);
        });

        Case("同版本覆盖被拒绝", () =>
        {
            string json = Signed(Manifest(m => m.Version = "0.4.0"));
            var descriptor = Descriptor(json, d => d.Version = "0.4.0");
            var result = ReleaseHandoff.Verify(descriptor, verifier, new Version(0, 4, 0), root, now);
            return (!result.Ok && result.Error.Contains("降级", StringComparison.Ordinal), result.Error);
        });

        Case("降级安装被拒绝", () =>
        {
            var result = ReleaseHandoff.Verify(Descriptor(goodJson), verifier, new Version(0, 6, 0), root, now);
            return (!result.Ok && result.Error.Contains("降级", StringComparison.Ordinal), result.Error);
        });

        Case("当前版本未知时拒绝（不能默认允许）", () =>
        {
            var result = ReleaseHandoff.Verify(Descriptor(goodJson), verifier, null, root, now);
            return (!result.Ok && result.Error.Contains("当前已安装版本", StringComparison.Ordinal), result.Error);
        });

        Case("更新包位于安装根目录之外时拒绝", () =>
        {
            string outside = Path.Combine(workspace, "outside.zip");
            File.WriteAllBytes(outside, package);
            var result = ReleaseHandoff.Verify(Descriptor(goodJson, d => d.PackagePath = outside),
                verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("安装目录内", StringComparison.Ordinal), result.Error);
        });

        Case("包路径指向上级目录穿越时拒绝", () =>
        {
            var result = ReleaseHandoff.Verify(
                Descriptor(goodJson, d => d.PackagePath = Path.Combine(root, "..", "escape.zip")),
                verifier, installed, root, now);
            return (!result.Ok, result.Error);
        });

        Case("包路径指向目录时拒绝", () =>
        {
            string directory = Path.Combine(root, "a-directory");
            Directory.CreateDirectory(directory);
            var result = ReleaseHandoff.Verify(Descriptor(goodJson, d => d.PackagePath = directory),
                verifier, installed, root, now);
            return (!result.Ok, result.Error);
        });

        Case("版本号含路径穿越字符时被拒绝", () =>
        {
            string json = Signed(Manifest(m => m.Version = "../0.5.0"));
            var descriptor = Descriptor(json, d => d.Version = "../0.5.0");
            var result = ReleaseHandoff.Verify(descriptor, verifier, installed, root, now);
            return (!result.Ok, result.Error);
        });

        Case("描述文件结构缺失时加载失败", () =>
        {
            string path = Path.Combine(workspace, "bad-release.json");
            File.WriteAllText(path, "{\"version\":\"0.5.0\"}");
            var loaded = ReleaseDescriptor.TryLoad(path);
            return (loaded == null, loaded == null ? "已拒绝加载" : "不应加载成功");
        });

        Case("清单包含重复字段时签名校验失败", () =>
        {
            // Append a second "version" after signing: canonical bytes change, so the signature
            // no longer matches. An attacker cannot add fields to a signed manifest.
            string duplicated = goodJson.TrimEnd().TrimEnd('}') + ",\"version\":\"9.9.9\"}";
            var result = ReleaseHandoff.Verify(Descriptor(duplicated), verifier, installed, root, now);
            return (!result.Ok, result.Error);
        });

        Case("产品标识不匹配时拒绝", () =>
        {
            string json = Signed(Manifest(m => m.Product = "Other Product"));
            var result = ReleaseHandoff.Verify(Descriptor(json), verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("产品标识", StringComparison.Ordinal), result.Error);
        });

        Case("架构不匹配时拒绝", () =>
        {
            string json = Signed(Manifest(m => m.Arch = "arm64"));
            var result = ReleaseHandoff.Verify(Descriptor(json), verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("架构", StringComparison.Ordinal), result.Error);
        });

        Case("清单架构版本不支持时拒绝", () =>
        {
            string json = Signed(Manifest(m => m.Schema = 2));
            var result = ReleaseHandoff.Verify(Descriptor(json), verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("架构 2", StringComparison.Ordinal), result.Error);
        });

        Case("发布批次未覆盖时拒绝", () =>
        {
            string json = Signed(Manifest(m => m.Batch = 9));
            var result = ReleaseHandoff.Verify(Descriptor(json), verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("批次", StringComparison.Ordinal), result.Error);
        });

        Case("受众不匹配时拒绝", () =>
        {
            string json = Signed(Manifest(m => m.Audience = new List<string> { "other" }));
            var result = ReleaseHandoff.Verify(Descriptor(json), verifier, installed, root, now);
            return (!result.Ok, result.Error);
        });

        Case("描述被磁盘篡改后重新加载仍以签名清单为准", () =>
        {
            string path = Path.Combine(workspace, "release.json");
            Descriptor(goodJson).Save(path);
            string text = File.ReadAllText(path);
            File.WriteAllText(path, text.Replace(packageHash, new string('B', 64)));
            var loaded = ReleaseDescriptor.TryLoad(path)!;
            var result = ReleaseHandoff.Verify(loaded, verifier, installed, root, now);
            return (!result.Ok, result.Error);
        });

        Case("交接描述可往返读写且字段一致", () =>
        {
            string path = Path.Combine(workspace, "roundtrip.json");
            var original = Descriptor(goodJson);
            original.Save(path);
            var loaded = ReleaseDescriptor.TryLoad(path);
            bool same = loaded != null && DescriptorClone(loaded).Version == original.Version &&
                        loaded.PackageSha256 == original.PackageSha256 && loaded.PackageSize == original.PackageSize &&
                        loaded.RequestedChannelName == original.RequestedChannelName &&
                        loaded.ManifestJson.Length == original.ManifestJson.Length;
            return (same, same ? "一致" : "字段不一致");
        });

        Case("旧接口形态的裸哈希无法构造有效交接", () =>
        {
            // Simulates the previous CLI: only a package path plus a hash supplied by the caller.
            // There is no signed manifest, so verification must fail rather than trust the hash.
            var descriptor = new ReleaseDescriptor
            {
                ManifestJson = "", Version = "0.5.0", PackagePath = packagePath,
                PackageSha256 = packageHash, PackageSize = package.Length, MachineGroup = "lab", ReleaseBatch = 5
            };
            var result = ReleaseHandoff.Verify(descriptor, verifier, installed, root, now);
            return (!result.Ok && result.Error.Contains("清单", StringComparison.Ordinal), result.Error);
        });

        return Task.FromResult<IReadOnlyList<UpdateSuiteCase>>(cases);
    }
}
