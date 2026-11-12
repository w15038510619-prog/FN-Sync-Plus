using System.Text.Json;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Regression tests for install-root relocation.
///
/// <c>current.json</c> stores the absolute directory of the active payload, which is only true on
/// the machine that produced it. Copying the built layout into an installer target, extracting the
/// release ZIP on another machine, or changing a drive letter used to leave a pointer that resolves
/// to nothing — the launcher then reported "no usable version" even though <c>app-&lt;version&gt;</c>
/// was sitting right next to it.
/// </summary>
public sealed class LayoutRelocationTestSuite
{
    public Task<IReadOnlyList<UpdateSuiteCase>> RunAsync(string workspace, CancellationToken token = default)
    {
        Directory.CreateDirectory(workspace);
        var cases = new List<UpdateSuiteCase>();

        void Case(string name, Func<(bool Passed, string Detail)> body)
        {
            try
            {
                var (passed, detail) = body();
                cases.Add(new UpdateSuiteCase(name, passed, detail));
            }
            catch (Exception ex)
            {
                cases.Add(new UpdateSuiteCase(name, false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        // Every case gets its own root so a repair in one case cannot hide a failure in another.
        string NewRoot(string name)
        {
            string root = Path.Combine(workspace, name);
            Directory.CreateDirectory(root);
            return root;
        }

        void CreatePayload(string root, string version)
        {
            string directory = Path.Combine(root, $"app-{version}");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "FNSyncPlus.WinUI.exe"), "test payload");
            File.WriteAllText(Path.Combine(directory, "build-meta.json"),
                $"{{\"version\":\"{version}\",\"supported_data_format\":1}}");
        }

        void WritePointer(string root, string fileName, string version, string directory, int format)
        {
            File.WriteAllText(Path.Combine(root, fileName),
                $"{{\"version\":\"{version}\",\"directory\":{JsonSerializer.Serialize(directory)}," +
                $"\"installed_at\":\"{DateTimeOffset.UtcNow:O}\",\"payload_sha256\":\"test\",\"supported_data_format\":{format}}}");
        }

        string Marker(string workspace_, string name) => Path.Combine(workspace_, $"{name}-data-format.json");

        // The pointer is JSON, so the on-disk path is escaped; compare parsed values, not raw text.
        string? PointerDirectory(string root, string fileName)
        {
            string path = Path.Combine(root, fileName);
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("directory", out var value) ? value.GetString() : null;
        }

        Case("current.json 指向其他机器的绝对路径时重新锚定到本机目录", () =>
        {
            string root = NewRoot("current-foreign");
            CreatePayload(root, "0.5.0");
            WritePointer(root, "current.json", "0.5.0", @"C:\BuildMachine\Release_WinUI\install\app-0.5.0", 1);
            var layout = new VersionedInstallLayout(root, Marker(workspace, "current-foreign"));
            string? active = layout.ActiveVersion;
            string? repaired = PointerDirectory(root, "current.json");
            bool pointsHere = string.Equals(repaired, layout.VersionDirectory("0.5.0"), StringComparison.OrdinalIgnoreCase);
            return (active == "0.5.0" && pointsHere, $"active={active} repaired={repaired}");
        });

        Case("previous.json 重新锚定后仍可回滚", () =>
        {
            string root = NewRoot("previous-foreign");
            CreatePayload(root, "0.5.0");
            CreatePayload(root, "0.4.0");
            WritePointer(root, "current.json", "0.5.0", Path.Combine(root, "app-0.5.0"), 1);
            WritePointer(root, "previous.json", "0.4.0", @"D:\OldMachine\install\app-0.4.0", 1);
            File.WriteAllText(Marker(workspace, "previous-foreign"),
                "{\"format\":1,\"app_version\":\"0.5.0\"}");
            var layout = new VersionedInstallLayout(root, Marker(workspace, "previous-foreign"));
            string? previous = layout.PreviousVersion;
            bool restored = layout.TryRestorePointer(out string reason);
            bool repaired = string.Equals(PointerDirectory(root, "previous.json"),
                layout.VersionDirectory("0.4.0"), StringComparison.OrdinalIgnoreCase);
            return (previous == "0.4.0" && repaired && restored && layout.ActiveVersion == "0.4.0",
                $"previous={previous} repaired={repaired} restored={restored} {reason}");
        });

        Case("目录确实不存在时不臆造版本", () =>
        {
            string root = NewRoot("missing-payload");
            WritePointer(root, "current.json", "0.9.0", @"C:\BuildMachine\install\app-0.9.0", 1);
            var layout = new VersionedInstallLayout(root, Marker(workspace, "missing-payload"));
            return (layout.ActiveVersion == null, $"active={layout.ActiveVersion ?? "null"}");
        });

        Case("指针文件只读时仍能启动（修复不落盘也不抛异常）", () =>
        {
            string root = NewRoot("readonly-pointer");
            CreatePayload(root, "0.5.0");
            string pointerPath = Path.Combine(root, "current.json");
            WritePointer(root, "current.json", "0.5.0", @"C:\BuildMachine\install\app-0.5.0", 1);
            File.SetAttributes(pointerPath, FileAttributes.ReadOnly);
            try
            {
                var layout = new VersionedInstallLayout(root, Marker(workspace, "readonly-pointer"));
                string? active = layout.ActiveVersion;
                bool stillForeign = File.ReadAllText(pointerPath).Contains("BuildMachine", StringComparison.OrdinalIgnoreCase);
                return (active == "0.5.0" && stillForeign, $"active={active} unchanged={stillForeign}");
            }
            finally
            {
                File.SetAttributes(pointerPath, FileAttributes.Normal);
            }
        });

        return Task.FromResult<IReadOnlyList<UpdateSuiteCase>>(cases);
    }
}
