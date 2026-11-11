using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FNSyncPlus.Core.Updates;

/// <summary>
/// Produces signed update manifests. This type is shared with the offline release tool; the private
/// key is supplied by the caller and is never stored, logged or shipped with the client.
/// </summary>
public static class UpdateManifestSigner
{
    /// <summary>
    /// Signs the manifest and returns the JSON to publish. The signature covers the manifest
    /// serialized with sorted keys and without the <c>signature</c> member, exactly what
    /// <see cref="UpdateManifestVerifier"/> recomputes on the client.
    /// </summary>
    public static string Sign(UpdateManifest manifest, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(privateKey);
        manifest.Signature = "";
        string unsignedJson = JsonSerializer.Serialize(manifest);
        byte[] canonical = Canonicalize(unsignedJson);
        byte[] signature = privateKey.SignData(canonical, HashAlgorithmName.SHA256);
        string body = Encoding.UTF8.GetString(canonical);
        if (!body.EndsWith('}')) throw new InvalidOperationException("清单规范形式不是 JSON 对象");
        return body[..^1] + ",\"signature\":\"" + Convert.ToBase64String(signature) + "\"}";
    }

    /// <summary>Re-serializes arbitrary manifest JSON into the canonical byte form.</summary>
    public static byte[] Canonicalize(string manifestJson)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("更新清单根节点必须是对象");
        return UpdateManifestVerifier.CanonicalBytes(doc.RootElement);
    }

    /// <summary>Creates a new P-256 key pair and returns it as PEM (private, public).</summary>
    public static (string PrivatePem, string PublicPem) CreateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Builds the pinned public key file content as shipped inside the client.</summary>
    public static string ToPinnedPublicKeyFile(string publicPem) =>
        "----- pinned update signing key, FN Sync Plus -----\n" + publicPem.Trim() + "\n";
}
