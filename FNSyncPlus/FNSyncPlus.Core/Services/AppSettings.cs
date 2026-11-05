using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

namespace FNSyncPlus.Core.Services
{
    public class AppSettings
    {
        public string NasServer { get; set; } = "";
        public string Username { get; set; } = "";
        public bool RememberCredentials { get; set; } = true;
        public string SourceFolder { get; set; } = "";
        public string TargetShare { get; set; } = "";
        public string TargetSubFolder { get; set; } = "";
        public bool AutoSyncEnabled { get; set; } = true;
        public int SyncIntervalMinutes { get; set; } = 5;
        public bool RealtimeWatchEnabled { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static string GetConfigFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "FNSyncPlus");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            return Path.Combine(folder, "settings.json");
        }

        public static AppSettings Load()
        {
            try
            {
                string path = GetConfigFilePath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (settings != null) return settings;
                }
            }
            catch
            {
                // 加载失败时使用默认配置
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string path = GetConfigFilePath();
                string json = JsonSerializer.Serialize(this, JsonOptions);
                File.WriteAllText(path, json);
                ApplyStartupRegistry(StartWithWindows);
            }
            catch
            {
                // 保存失败不崩溃
            }
        }

        public static void ApplyStartupRegistry(bool enable)
        {
            try
            {
                const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
                using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
                if (key != null)
                {
                    string appPath = Environment.ProcessPath ?? "";
                    if (enable && !string.IsNullOrEmpty(appPath))
                    {
                        key.SetValue("FNSyncPlus", $"\"{appPath}\" --minimize");
                    }
                    else
                    {
                        key.DeleteValue("FNSyncPlus", throwOnMissingValue: false);
                    }
                }
            }
            catch
            {
                // 注册表写入受限时忽略
            }
        }
    }
}
