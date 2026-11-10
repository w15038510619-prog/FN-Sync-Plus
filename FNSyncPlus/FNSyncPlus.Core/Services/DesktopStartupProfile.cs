namespace FNSyncPlus.Core.Services;

public enum DesktopStartupProfile
{
    VersionedBeta,
    IsolatedTest,
    LegacyEngine
}

public static class DesktopStartupProfileSelector
{
    public static DesktopStartupProfile Select(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var args = arguments.ToArray();
        bool isolated = args.Contains("--isolated-test-session", StringComparer.Ordinal);
        bool versioned = args.Contains("--versioned-beta", StringComparer.Ordinal);
        bool legacy = args.Contains("--legacy-engine", StringComparer.Ordinal);
        if ((isolated && (versioned || legacy)) || (versioned && legacy))
            throw new InvalidOperationException("隔离测试、新版试用和旧版引擎参数不能同时使用。");
        if (isolated) return DesktopStartupProfile.IsolatedTest;
        if (legacy) return DesktopStartupProfile.LegacyEngine;
        return DesktopStartupProfile.VersionedBeta;
    }
}
