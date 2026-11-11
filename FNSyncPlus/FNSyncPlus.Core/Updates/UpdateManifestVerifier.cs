using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Updates;

public enum UpdateChannel { Stable = 0, Beta = 1 }

/// <summary>Signed release description served over HTTPS. The signature covers the exact JSON body.</summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    [JsonPropertyName("product")] public string Product { get; set; } = "FN Sync Plus";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "stable";
    [JsonPropertyName("arch")] public string Arch { get; set; } = "x64";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("min_version")] public string MinVersion { get; set; } = "0.0.0";
    [JsonPropertyName("released_at")] public DateTimeOffset ReleasedAt { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("batch")] public int Batch { get; set; }
    [JsonPropertyName("audience")] public List<string> Audience { get; set; } = new();
    [JsonPropertyName("min_compatible_client")] public string MinCompatibleClient { get; set; } = "0.0.0";

    /// <summary>Detached signature (base64) over the canonical bytes; not part of the signed payload.</summary>
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
}

public sealed record UpdateVerificationResult(bool Ok, string Error, Version? Version, UpdateChannel? Channel)
{
    public static UpdateVerificationResult Fail(string error) => new(false, error, null, null);
}

/// <summary>
/// Verifies update manifests with an ECDSA P-256 public key pinned in the client. The signature is
/// over the manifest serialized WITHOUT the signature field, using deterministic key ordering.
/// Private keys never live in the client or repository.
/// </summary>
public sealed class UpdateManifestVerifier
{
    private readonly byte[] _publicKeySpki;

    public UpdateManifestVerifier(byte[] publicKeySubjectPublicKeyInfo)
    {
        _publicKeySpki = publicKeySubjectPublicKeyInfo ?? throw new ArgumentNullException(nameof(publicKeySubjectPublicKeyInfo));
        if (_publicKeySpki.Length == 0) throw new ArgumentException("更新公钥不能为空", nameof(publicKeySubjectPublicKeyInfo));
    }

    /// <summary>Loads a PEM ("BEGIN PUBLIC KEY") pinned key shipped with the app.</summary>
    public static UpdateManifestVerifier FromPem(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) throw new ArgumentException("更新公钥 PEM 为空", nameof(pem));
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        return new UpdateManifestVerifier(ecdsa.ExportSubjectPublicKeyInfo());
    }

    public UpdateVerificationResult Verify(string manifestJson, ClientUpdateIdentity client, UpdateChannel requestedChannel,
        Version? currentVersion, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(manifestJson)) return UpdateVerificationResult.Fail("更新清单为空");
        UpdateManifest? manifest;
        byte[] body;
        string signature;
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return UpdateVerificationResult.Fail("更新清单根节点必须是对象");
            if (!doc.RootElement.TryGetProperty("signature", out var signatureElement) || signatureElement.ValueKind != JsonValueKind.String)
                return UpdateVerificationResult.Fail("更新清单缺少签名");
            signature = signatureElement.GetString() ?? "";
            body = CanonicalBytes(doc.RootElement);
            manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestJson);
        }
        catch (JsonException ex) { return UpdateVerificationResult.Fail($"更新清单 JSON 无效: {ex.Message}"); }
        if (manifest == null) return UpdateVerificationResult.Fail("更新清单为空");

        byte[] signatureBytes;
        try { signatureBytes = Convert.FromBase64String(signature); }
        catch (FormatException) { return UpdateVerificationResult.Fail("签名不是有效的 Base64"); }
        using (var ecdsa = ECDsa.Create())
        {
            try
            {
                ecdsa.ImportSubjectPublicKeyInfo(_publicKeySpki, out _);
                if (!ecdsa.VerifyData(body, signatureBytes, HashAlgorithmName.SHA256))
                    return UpdateVerificationResult.Fail("更新清单签名校验失败，拒绝使用");
            }
            catch (CryptographicException) { return UpdateVerificationResult.Fail("内置更新公钥无效，拒绝使用"); }
        }

        // Signature passed; now apply release policy.
        if (manifest.Schema != 1) return UpdateVerificationResult.Fail($"不支持的清单架构 {manifest.Schema}");
        if (!string.Equals(manifest.Product, "FN Sync Plus", StringComparison.Ordinal))
            return UpdateVerificationResult.Fail("更新清单产品标识不匹配");
        if (!string.Equals(manifest.Arch, "x64", StringComparison.OrdinalIgnoreCase))
            return UpdateVerificationResult.Fail("更新包架构不匹配");
        if (!Version.TryParse(manifest.Version, out var version)) return UpdateVerificationResult.Fail("版本号无效");
        if (!Version.TryParse(manifest.MinVersion, out var minVersion)) return UpdateVerificationResult.Fail("最低兼容版本无效");
        if (!Version.TryParse(manifest.MinCompatibleClient, out var minClient)) return UpdateVerificationResult.Fail("最低客户端版本无效");
        if (currentVersion != null && version <= currentVersion)
            return UpdateVerificationResult.Fail("更新清单不是更高版本，拒绝降级或重复安装");
        if (currentVersion != null && currentVersion < minClient)
            return UpdateVerificationResult.Fail("当前客户端过旧，不能通过自动更新升级，请手动安装");
        if (!ChannelMatches(manifest.Channel, requestedChannel))
            return UpdateVerificationResult.Fail($"通道不匹配：清单为 {manifest.Channel}，请求为 {requestedChannel}");
        if (manifest.Size <= 0) return UpdateVerificationResult.Fail("更新包大小无效");
        if (manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(Uri.IsHexDigit))
            return UpdateVerificationResult.Fail("更新包 SHA-256 格式无效");
        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return UpdateVerificationResult.Fail("更新包地址必须是 HTTPS");
        if (manifest.ReleasedAt > now.AddHours(24))
            return UpdateVerificationResult.Fail("更新清单发布时间异常");
        if (manifest.Audience.Count > 0 && !manifest.Audience.Contains(client.MachineGroup, StringComparer.OrdinalIgnoreCase) &&
            !manifest.Audience.Contains("*", StringComparer.Ordinal))
            return UpdateVerificationResult.Fail("该发布批次不面向当前客户端");
        if (client.ReleaseBatch < manifest.Batch)
            return UpdateVerificationResult.Fail("该发布批次尚未覆盖当前客户端");
        return new UpdateVerificationResult(true, "", version, ChannelMatches(manifest.Channel, UpdateChannel.Beta) ? UpdateChannel.Beta : UpdateChannel.Stable);
    }

    /// <summary>
    /// A Stable client accepts only stable releases; a Beta client accepts beta releases and, as a
    /// fallback, stable ones. A stable client never receives beta builds.
    /// </summary>
    internal static bool ChannelMatches(string manifestChannel, UpdateChannel requested)
    {
        bool stable = string.Equals(manifestChannel, "stable", StringComparison.OrdinalIgnoreCase);
        bool beta = string.Equals(manifestChannel, "beta", StringComparison.OrdinalIgnoreCase);
        return requested == UpdateChannel.Beta ? beta || stable : stable;
    }

    /// <summary>Serializes the JSON object with sorted property names, excluding "signature".</summary>
    public static byte[] CanonicalBytes(JsonElement root)
    {
        var builder = new MemoryStream();
        using (var writer = new Utf8JsonWriter(builder))
            WriteCanonical(writer, root, skipSignature: true);
        return builder.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool skipSignature)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                    .Where(p => !(p.NameEquals("signature") && skipSignature))
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, skipSignature: false);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item, skipSignature: false);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(element.GetString()); break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long l)) writer.WriteNumberValue(l);
                else writer.WriteNumberValue(element.GetDouble());
                break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            default: writer.WriteNullValue(); break;
        }
    }
}

/// <summary>Facts about this installation used for batch/audience gating; never contains NAS data.</summary>
public sealed record ClientUpdateIdentity(string MachineGroup, int ReleaseBatch, string Architectures = "x64");
