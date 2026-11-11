using System.Security.Cryptography;
using System.Text.Json;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.UpdateTool;

/// <summary>
/// Offline release tool: creates the update signing key, signs manifests, and verifies what a
/// client would accept. It runs on a release machine, never on user machines and never in CI with
/// the production key. Usage:
///
///   keygen  --private &lt;path&gt; --public &lt;path&gt; [--force]
///   sign    --input &lt;descriptor.json&gt; --key &lt;private.pem&gt; --out &lt;manifest.json&gt;
///           [--package &lt;zip&gt;] [--url &lt;https://...&gt;] [--channel beta|stable]
///           [--version 0.5.0] [--notes "..."] [--batch N] [--audience a,b] [--min-version v]
///           [--min-compatible-client v]
///   verify  --manifest &lt;manifest.json&gt; --public &lt;public.pem&gt; [--current 0.4.0] [--channel stable]
///   inspect --manifest &lt;manifest.json&gt;
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();
        try
        {
            return args[0] switch
            {
                "keygen" => Keygen(Args.Parse(args)),
                "sign" => Sign(Args.Parse(args)),
                "verify" => Verify(Args.Parse(args)),
                "inspect" => Inspect(Args.Parse(args)),
                _ => Usage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 1;
        }
    }

    private static int Keygen(Args args)
    {
        string? privatePath = args.Get("private");
        string? publicPath = args.Get("public");
        if (privatePath == null || publicPath == null) return Usage();
        bool force = args.Has("force");
        if (!force && (File.Exists(privatePath) || File.Exists(publicPath)))
        {
            Console.Error.WriteLine("密钥文件已存在，拒绝覆盖（确认要替换请加 --force）。");
            return 1;
        }
        var (privatePem, publicPem) = UpdateManifestSigner.CreateKeyPair();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privatePath))!);
        File.WriteAllText(privatePath, privatePem);
        File.WriteAllText(publicPath, UpdateManifestSigner.ToPinnedPublicKeyFile(publicPem));
        Console.WriteLine($"已生成 P-256 更新签名密钥：\n  私钥：{privatePath}（不得提交到仓库或分发）\n  公钥：{publicPath}（编译进客户端）");
        return 0;
    }

    private static int Sign(Args args)
    {
        string? input = args.Get("input");
        string? keyPath = args.Get("key");
        string? outPath = args.Get("out");
        if (input == null || keyPath == null || outPath == null) return Usage();

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(input))
                       ?? throw new InvalidOperationException("描述文件无法解析为更新清单");
        if (args.Get("channel") is { } channel) manifest.Channel = channel;
        if (args.Get("version") is { } version) manifest.Version = version;
        if (args.Get("notes") is { } notes) manifest.Notes = notes;
        if (args.Get("min-version") is { } minVersion) manifest.MinVersion = minVersion;
        if (args.Get("min-compatible-client") is { } minClient) manifest.MinCompatibleClient = minClient;
        if (args.Get("url") is { } url) manifest.Url = url;
        if (int.TryParse(args.Get("batch"), out int batch)) manifest.Batch = batch;
        if (args.Get("audience") is { } audience)
            manifest.Audience = audience.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (args.Get("package") is { } packagePath)
        {
            var info = new FileInfo(packagePath);
            if (!info.Exists) throw new FileNotFoundException("更新包不存在", packagePath);
            manifest.Size = info.Length;
            using var stream = File.OpenRead(packagePath);
            manifest.Sha256 = Convert.ToHexString(SHA256.HashData(stream));
            Console.WriteLine($"已按更新包填充 size={manifest.Size} sha256={manifest.Sha256}");
        }
        if (manifest.ReleasedAt == default) manifest.ReleasedAt = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(manifest.Version)) throw new InvalidOperationException("缺少 version");
        if (string.IsNullOrWhiteSpace(manifest.Url)) throw new InvalidOperationException("缺少 url");
        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("url 必须是 HTTPS");

        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyPath));
        string signed = UpdateManifestSigner.Sign(manifest, key);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, signed);
        Console.WriteLine($"已签名：{outPath}（版本 {manifest.Version}，通道 {manifest.Channel}）");
        return 0;
    }

    private static int Verify(Args args)
    {
        string? manifestPath = args.Get("manifest");
        string? publicPath = args.Get("public");
        if (manifestPath == null || publicPath == null) return Usage();
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(publicPath));
        var verifier = new UpdateManifestVerifier(key.ExportSubjectPublicKeyInfo());
        string channel = args.Get("channel") ?? "stable";
        Version? current = Version.TryParse(args.Get("current"), out var parsed) ? parsed : null;
        var identity = new ClientUpdateIdentity(args.Get("group") ?? "", int.TryParse(args.Get("batch"), out int batch) ? batch : 0);
        var result = verifier.Verify(File.ReadAllText(manifestPath), identity,
            channel == "beta" ? UpdateChannel.Beta : UpdateChannel.Stable, current, DateTimeOffset.UtcNow);
        if (result.Ok)
        {
            Console.WriteLine($"通过：版本 {result.Version}，通道 {result.Channel}");
            return 0;
        }
        Console.Error.WriteLine($"拒绝：{result.Error}");
        return 1;
    }

    private static int Inspect(Args args)
    {
        string? manifestPath = args.Get("manifest");
        if (manifestPath == null) return Usage();
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestPath));
        if (manifest == null) return 1;
        Console.WriteLine($"产品     : {manifest.Product}");
        Console.WriteLine($"通道     : {manifest.Channel}");
        Console.WriteLine($"版本     : {manifest.Version}（最低 {manifest.MinVersion}，最低客户端 {manifest.MinCompatibleClient}）");
        Console.WriteLine($"发布批次 : {manifest.Batch} 受众 {string.Join(",", manifest.Audience)}");
        Console.WriteLine($"地址     : {manifest.Url}");
        Console.WriteLine($"大小     : {manifest.Size}");
        Console.WriteLine($"SHA-256  : {manifest.Sha256}");
        Console.WriteLine($"签名长度 : {manifest.Signature.Length} 字符");
        return 0;
    }

    private static int Usage()
    {
        Console.WriteLine("FNSyncPlus.UpdateTool keygen|sign|verify|inspect  （详见源码头部注释）");
        Console.WriteLine("示例：");
        Console.WriteLine("  keygen --private keys/update-private.pem --public keys/update-public.pem");
        Console.WriteLine("  sign --input release/descriptor.json --key keys/update-private.pem --package release/pkg.zip --out release/manifest.json");
        Console.WriteLine("  verify --manifest release/manifest.json --public keys/update-public.pem --current 0.4.0");
        return 2;
    }

    private sealed class Args
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

        public static Args Parse(string[] args)
        {
            var parsed = new Args();
            for (int i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
                string name = args[i][2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    parsed._values[name] = args[++i];
                else
                    parsed._flags.Add(name);
            }
            return parsed;
        }

        public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;
        public bool Has(string name) => _flags.Contains(name) || _values.ContainsKey(name);
    }
}
