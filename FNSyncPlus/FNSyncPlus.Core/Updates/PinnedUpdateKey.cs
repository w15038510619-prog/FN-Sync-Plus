using System.Reflection;

namespace FNSyncPlus.Core.Updates;

/// <summary>
/// The update signing public key pinned inside the client. It is embedded at build time from
/// <c>Updates/update-public.pem</c>; when no key is embedded the verifier cannot be created and all
/// update paths stay disabled (fail-closed) instead of trusting whatever the network returns.
/// </summary>
public static class PinnedUpdateKey
{
    public const string ResourceName = "FNSyncPlus.update-public.pem";
    private static readonly Lazy<string?> Cached = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsConfigured => Cached.Value != null;

    public static UpdateManifestVerifier? TryCreateVerifier(out string error)
    {
        error = "";
        string? pem = Cached.Value;
        if (pem == null)
        {
            error = "本构建未内置更新签名公钥，自动更新已停用。请使用正式发布构建。";
            return null;
        }
        try { return UpdateManifestVerifier.FromPem(pem); }
        catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            error = $"内置更新公钥无效，自动更新已停用：{ex.Message}";
            return null;
        }
    }

    private static string? Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        string pem = reader.ReadToEnd();
        return string.IsNullOrWhiteSpace(pem) ? null : pem;
    }
}
