using System;
using System.IO;

namespace FNSyncPlus.Core.Updates;

public sealed record HandoffVerification(bool Ok, string Error, VerifiedRelease? Release)
{
    public static HandoffVerification Refuse(string error) => new(false, error, null);
}

/// <summary>
/// Verification the standalone updater performs before replacing files. It exists in Core (not in
/// the updater's Main) so the attack matrix can be tested directly, and it deliberately trusts
/// nothing that came from the caller: the manifest is re-verified with the pinned key and the
/// version, size, hash and URL are all re-derived from that signature.
/// </summary>
public static class ReleaseHandoff
{
    /// <summary>
    /// Binds a descriptor + local package to the signed manifest.
    /// </summary>
    /// <param name="installed">Version currently on disk; used for the "must be newer" rule. Null
    /// means unknown, which is treated as a refusal rather than an implicit downgrade allowance.</param>
    public static HandoffVerification Verify(ReleaseDescriptor descriptor, UpdateManifestVerifier verifier,
        Version? installed, string installRoot, DateTimeOffset now)
    {
        if (descriptor == null) return HandoffVerification.Refuse("发布描述缺失或格式无效");
        if (verifier == null) return HandoffVerification.Refuse("未内置更新公钥，拒绝安装");
        if (installed == null) return HandoffVerification.Refuse("无法确认当前已安装版本，不能排除降级，已拒绝安装");

        string package;
        try { package = Path.GetFullPath(descriptor.PackagePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return HandoffVerification.Refuse($"更新包路径无效：{ex.Message}");
        }
        string root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!package.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return HandoffVerification.Refuse("更新包不在安装目录内，拒绝安装");
        if (!File.Exists(package)) return HandoffVerification.Refuse("更新包文件不存在");
        if (string.Equals(Path.GetFileName(package), "release.json", StringComparison.OrdinalIgnoreCase))
            return HandoffVerification.Refuse("更新包路径指向描述文件，拒绝安装");

        var identity = new ClientUpdateIdentity(descriptor.MachineGroup, descriptor.ReleaseBatch);
        var verification = verifier.VerifyRelease(descriptor.ManifestJson, identity, descriptor.RequestedChannel,
            installed, now);
        if (!verification.Ok) return HandoffVerification.Refuse($"更新清单未通过校验：{verification.Error}");
        var manifest = verification.Manifest!;

        if (!string.Equals(manifest.Version, descriptor.Version, StringComparison.Ordinal))
            return HandoffVerification.Refuse(
                $"发布描述版本号（{descriptor.Version}）与已签名清单（{manifest.Version}）不一致");
        if (!string.Equals(manifest.Sha256, descriptor.PackageSha256, StringComparison.OrdinalIgnoreCase))
            return HandoffVerification.Refuse("发布描述的 SHA-256 与已签名清单不一致");
        if (manifest.Size != descriptor.PackageSize)
            return HandoffVerification.Refuse("发布描述的大小与已签名清单不一致");

        long size;
        string hash;
        try
        {
            size = new FileInfo(package).Length;
            hash = UpdatePackageExtractor.HashFile(package);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HandoffVerification.Refuse($"无法读取更新包：{ex.Message}");
        }
        if (size != manifest.Size)
            return HandoffVerification.Refuse($"更新包大小与已签名清单不一致（期望 {manifest.Size}，实际 {size}）");
        if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            return HandoffVerification.Refuse("更新包 SHA-256 与已签名清单不一致");

        var release = new VerifiedRelease(descriptor.ManifestJson, manifest.Version, verification.Channel,
            manifest.Sha256, manifest.Size, manifest.Url, package, descriptor.MachineGroup, descriptor.ReleaseBatch);
        return new HandoffVerification(true, "", release);
    }
}
