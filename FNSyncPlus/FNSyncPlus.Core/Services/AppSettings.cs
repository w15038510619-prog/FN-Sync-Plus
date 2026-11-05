using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FNSyncPlus.Core.Models;
using Microsoft.Win32;

namespace FNSyncPlus.Core.Services
{
    public class AppSettings
    {
        public string NasServer { get; set; } = "";
        public string Username { get; set; } = "";
        public bool RememberCredentials { get; set; } = true;
        public string PreferredBackupRoot { get; set; } = ""; // 常用/上次选定的 NAS 备份根目录
        public int MaxConcurrentSyncs { get; set; } = 2;       // 全局并发任务数
        public string AppTheme { get; set; } = "Light";         // 主题：Light / Dark

        // 多任务集合
        public List<SyncTask> Tasks { get; set; } = new();

        // 兼容 v2.0 单任务遗留字段
        [JsonPropertyName("SourceFolder")]
        public string? LegacySourceFolder { get; set; }

        [JsonPropertyName("TargetShare")]
        public string? LegacyTargetShare { get; set; }

        public bool AutoSyncEnabled { get; set; } = true;
        public int SyncIntervalMinutes { get; set; } = 5;
        public bool RealtimeWatchEnabled { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
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
                    var settings = FromJson(json);
                    if (MigrateLegacySettings(settings))
                    {
                        settings.Save();
                    }
                    return settings;
                }
            }
            catch
            {
                // 加载失败时使用默认配置
            }
            return new AppSettings();
        }

        public static AppSettings FromJson(string json)
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            return settings;
        }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, JsonOptions);
        }

        public static bool MigrateLegacySettings(AppSettings settings)
        {
            if (settings.Tasks.Count == 0 &&
                (!string.IsNullOrEmpty(settings.LegacySourceFolder) || !string.IsNullOrEmpty(settings.LegacyTargetShare)))
            {
                string src = settings.LegacySourceFolder ?? "";
                string dest = settings.LegacyTargetShare ?? "";
                string taskName = !string.IsNullOrEmpty(src) ? Path.GetFileName(src.TrimEnd('\\', '/')) : "我的备份";
                if (string.IsNullOrEmpty(taskName)) taskName = "默认同步任务";

                var migratedTask = new SyncTask
                {
                    TaskId = "legacy_task_1",
                    TaskName = taskName,
                    SourceFolder = src,
                    NasDeviceId = settings.NasServer,
                    DestinationFolder = dest,
                    DestinationRoot = dest,
                    AutoSyncEnabled = settings.AutoSyncEnabled,
                    RealtimeWatchEnabled = settings.RealtimeWatchEnabled,
                    IntervalMinutes = settings.SyncIntervalMinutes,
                    LastSyncStatus = "已迁移旧版任务"
                };
                settings.Tasks.Add(migratedTask);
                return true;
            }
            return false;
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
