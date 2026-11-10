using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;
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

        // 已信任的设备证书 SHA-256 指纹列表 (Host -> SHA256 Thumbprint)
        public Dictionary<string, string> TrustedCertThumbprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

        [JsonIgnore]
        public bool IsInMemoryOnly { get; set; } = false;

        [JsonIgnore]
        public string? CustomConfigPath { get; set; } = null;

        [JsonIgnore]
        public string? IsolatedCredentialPath { get; set; }

        // Optional, explicitly selected test session. It never points at production settings.
        [JsonIgnore]
        public string? IsolatedSessionPath { get; private set; }

        // The beta profile has its own file and credentials; old app versions use a different path.
        [JsonIgnore]
        public string? VersionedBetaProfilePath { get; private set; }

        [JsonIgnore]
        public string? VersionedLegacySettingsPath { get; private set; }

        [JsonIgnore]
        public bool IsVersionedBetaProfile => VersionedBetaProfilePath != null;

        [JsonIgnore]
        private string? IsolatedTestRemoteRoot { get; set; }

        public static AppSettings CreateInMemory() => new AppSettings { IsInMemoryOnly = true };
        public static AppSettings CreateForTest(string testFilePath) => new AppSettings { CustomConfigPath = testFilePath };

        public static AppSettings LoadVersionedBetaProfile() => LoadVersionedProfileAt(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FNSyncPlus", "versioned-beta"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FNSyncPlus", "settings.json"));

        // This isolated test path is never selected by ordinary application startup.
        public static AppSettings LoadVersionedBetaTestProfile(Guid id)
        {
            if (id == Guid.Empty) throw new ArgumentException("测试配置标识无效", nameof(id));
            string folder = Path.Combine(Path.GetTempPath(), "FNSyncPlus_CodexTests", $"VersionedProfile_{id:N}");
            return LoadVersionedProfileAt(folder, Path.Combine(folder, "old-settings-readonly.json"));
        }

        private static AppSettings LoadVersionedProfileAt(string folder, string legacySettingsPath)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "settings.json");
            var settings = File.Exists(path) ? FromJson(File.ReadAllText(path)) : new AppSettings();
            if (!string.IsNullOrEmpty(settings.LegacySourceFolder) || !string.IsNullOrEmpty(settings.LegacyTargetShare))
                throw new InvalidDataException("新版配置包含旧式单任务字段，必须先明确迁移。");
            settings.VersionedBetaProfilePath = path;
            settings.VersionedLegacySettingsPath = legacySettingsPath;
            settings.IsolatedCredentialPath = Path.Combine(folder, "credentials.dat");
            return settings;
        }

        public static AppSettings LoadIsolatedTestSession(Guid id)
        {
            if (id == Guid.Empty) throw new ArgumentException("隔离会话标识无效", nameof(id));
            string folder = Path.Combine(Path.GetTempPath(), "FNSyncPlus_CodexTests", $"WinUiSession_{id:N}");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "settings.json");
            var settings = File.Exists(path) ? FromJson(File.ReadAllText(path)) : new AppSettings();
            settings.IsInMemoryOnly = true;
            settings.IsolatedSessionPath = path;
            return settings;
        }

        public void SetIsolatedTestBoundary(string remoteRoot)
        {
            if (IsolatedSessionPath == null || !PathHelper.IsSafeRelativePath(remoteRoot) || remoteRoot.Contains('/') || remoteRoot.Contains('\\') ||
                !remoteRoot.StartsWith("Codex_FN_Test_", StringComparison.Ordinal))
                throw new InvalidOperationException("隔离测试远端父目录无效");
            IsolatedTestRemoteRoot = remoteRoot;
            foreach (var task in Tasks) EnsureIsolatedTestTask(task);
        }

        public void EnsureIsolatedTestTask(SyncTask task)
        {
            if (IsolatedSessionPath == null) return;
            if (IsolatedTestRemoteRoot == null) throw new InvalidOperationException("隔离测试尚未指定远端父目录");
            string localRoot = Path.Combine(Path.GetTempPath(), "FNSyncPlus_CodexTests");
            string relative = Path.GetRelativePath(localRoot, Path.GetFullPath(task.SourceFolder));
            bool WithinRemote(string path)
            {
                string normalized = PathHelper.NormalizeRelPath(path);
                if (normalized.StartsWith("我的文件/", StringComparison.Ordinal)) normalized = normalized["我的文件/".Length..];
                return PathHelper.IsSafeRelativePath(normalized) && normalized.StartsWith(IsolatedTestRemoteRoot + "/", StringComparison.OrdinalIgnoreCase);
            }
            if (task.ProviderType != StorageProviderType.FnOsPersonal || !PathHelper.IsSafeRelativePath(relative) ||
                !WithinRemote(task.DestinationFolder) || (!string.IsNullOrEmpty(task.ExperimentalExistingRoot) && !WithinRemote(task.ExperimentalExistingRoot)))
                throw new InvalidOperationException("隔离会话中的任务超出指定测试目录，已阻止执行");
        }

        /// <summary>Reads only already-trusted SHA-256 certificate fingerprints; never migrates or writes the source settings.</summary>
        public static Dictionary<string, string> ReadTrustedCertificates(string path)
        {
            var trusted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return trusted;
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object) return trusted;
                bool found = false;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!property.Name.Equals(nameof(TrustedCertThumbprints), StringComparison.OrdinalIgnoreCase)) continue;
                    if (found || property.Value.ValueKind != JsonValueKind.Object)
                        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    found = true;
                    foreach (var entry in property.Value.EnumerateObject())
                    {
                        string? hash = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(entry.Name) && hash?.Length == 64 && hash.All(Uri.IsHexDigit))
                        {
                            if (!trusted.TryAdd(entry.Name, hash)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
            return trusted;
        }

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
            var settings = LoadFromPath(GetConfigFilePath());
            settings.CustomConfigPath = null;
            return settings;
        }

        public static AppSettings LoadFromPath(string path)
        {
            if (!File.Exists(path))
            {
                return new AppSettings { CustomConfigPath = path };
            }
            try
            {
                var settings = FromJson(File.ReadAllText(path));
                settings.CustomConfigPath = path;
                if (MigrateLegacySettings(settings))
                {
                    File.Copy(path, path + ".pre-migration-" + Guid.NewGuid().ToString("N") + ".bak");
                    settings.Save();
                }
                return settings;
            }
            catch (Exception ex)
            {
                throw new IOException($"无法读取或迁移配置文件，原文件未自动重置：{path}", ex);
            }
        }

        public static AppSettings FromJson(string json)
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? throw new JsonException("配置为空");
            if (settings.Tasks == null) throw new JsonException("任务列表无效");
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
            if (IsInMemoryOnly && IsolatedSessionPath == null) return;

            lock (Tasks)
            {
                string path = VersionedBetaProfilePath ?? IsolatedSessionPath ?? (!string.IsNullOrEmpty(CustomConfigPath) ? CustomConfigPath : GetConfigFilePath());
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
                    if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                    else File.Move(temp, path);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                if (VersionedBetaProfilePath == null && IsolatedSessionPath == null && string.IsNullOrEmpty(CustomConfigPath))
                    ApplyStartupRegistry(StartWithWindows);
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
