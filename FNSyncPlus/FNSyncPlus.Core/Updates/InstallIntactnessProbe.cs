using System;
using System.IO;
using System.Linq;

namespace FNSyncPlus.Core.Updates;

/// <summary>
/// Filesystem evidence about what an install operation did to the install root. This is the
/// proof source for "the install never began": not a guess from a missing log line but a direct
/// observation that the pointer still names the pre-handoff version and no staging/target
/// artifacts exist. When the probe itself cannot read the root it reports "not provable", and
/// fail-closed callers keep the maintenance hold.
/// </summary>
public sealed class InstallIntactnessProbe
{
    private readonly string _root;
    private readonly string _preHandoffVersion;
    private readonly string _targetVersion;

    public InstallIntactnessProbe(string root, string preHandoffVersion, string targetVersion)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("安装根目录不能为空", nameof(root));
        _root = Path.GetFullPath(root);
        _preHandoffVersion = preHandoffVersion;
        _targetVersion = targetVersion;
    }

    /// <summary>
    /// True only when the filesystem PROVES this operation never modified the installation:
    /// current.json still names the pre-handoff version, the target payload directory does not
    /// exist, and no staging directory remains. Any read failure or doubt returns false —
    /// "cannot prove intact" must hold the maintenance pause, not release it.
    /// </summary>
    public bool ProveInstallNeverBegan(out string detail)
    {
        try
        {
            var layout = new VersionedInstallLayout(_root);
            string? active = layout.ActiveVersion;
            if (!string.Equals(active, _preHandoffVersion, StringComparison.Ordinal))
            {
                detail = $"current.json 指向 {active ?? "(缺失)"}，不是交接前版本 {_preHandoffVersion}";
                return false;
            }
            if (Directory.Exists(layout.VersionDirectory(_targetVersion)))
            {
                detail = $"目标版本目录已存在：{layout.VersionDirectory(_targetVersion)}";
                return false;
            }
            string[] staging = Directory.GetDirectories(_root, ".staging-*");
            if (staging.Length > 0)
            {
                detail = $"存在安装暂存目录：{staging[0]}";
                return false;
            }
            detail = "文件系统证据：指针保持交接前版本、无目标目录、无暂存目录，安装确未开始";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            detail = $"无法读取安装根目录（{ex.GetType().Name}），无法证明安装未开始";
            return false;
        }
    }

    /// <summary>True when the filesystem shows the pointer moved to the target version.</summary>
    public bool ProveInstallCompleted(out string detail)
    {
        try
        {
            var layout = new VersionedInstallLayout(_root);
            string? active = layout.ActiveVersion;
            bool done = string.Equals(active, _targetVersion, StringComparison.Ordinal) &&
                        Directory.Exists(layout.VersionDirectory(_targetVersion));
            detail = done ? $"current.json 已指向 {_targetVersion}" : $"current.json 指向 {active ?? "(缺失)"}";
            return done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            detail = $"无法读取安装根目录（{ex.GetType().Name}）";
            return false;
        }
    }
}
