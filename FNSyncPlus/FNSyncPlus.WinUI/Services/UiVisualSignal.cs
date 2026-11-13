using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.WinUI.Services
{
    /// <summary>
    /// Readiness beacon for the pure visual acceptance mode (--ui-visual-acceptance, isolated
    /// sessions only). Each time a page finishes navigation AND its content has loaded, the app
    /// refreshes <c>ui-ready.json</c> next to the isolated session settings. The acceptance
    /// harness polls this file and only takes a screenshot once the target page reports ready —
    /// a white rectangle or the previous page can never be mistaken for a result.
    ///
    /// No-ops outside visual acceptance mode, so production runs carry no overhead.
    /// </summary>
    public static class UiVisualSignal
    {
        private const string FileName = "ui-ready.json";

        public static void ReportPageReady(string page, IReadOnlyDictionary<string, object?> details)
        {
            if (!AppServices.IsUiVisualAcceptance) return;
            try
            {
                var settings = AppServices.GetService<AppSettings>();
                string? directory = settings.IsolatedSessionPath != null
                    ? Path.GetDirectoryName(settings.IsolatedSessionPath)
                    : null;
                if (directory == null) return;
                Directory.CreateDirectory(directory);
                var payload = new Dictionary<string, object?>
                {
                    ["page"] = page,
                    ["ready"] = true,
                    ["pid"] = Environment.ProcessId,
                    ["reported_at"] = DateTimeOffset.UtcNow,
                };
                foreach (var pair in details) payload[pair.Key] = pair.Value;
                string path = Path.Combine(directory, FileName);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(payload,
                    new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            catch (Exception ex)
            {
                App.LogCrashException(ex, "UiVisualSignal.ReportPageReady");
            }
        }
    }
}
