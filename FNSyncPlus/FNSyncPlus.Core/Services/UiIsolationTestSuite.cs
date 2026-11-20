using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// UI isolation suite (round 3). Two halves:
///
/// 1. Process/session isolation semantics that are testable in-process: distinct single-instance
///    identities coexist (a visual-acceptance run can never exclude the user's real instance),
///    same identity still excludes, and an isolated session fixture never claims saved production
///    credentials.
///
/// 2. Artifact audit of a completed ui-visual-acceptance run: every PASS has a real screenshot
///    on disk, every capture records session/pid/page/dpi/ready-wait, and the protected-instance
///    assertions are present and green. A screenshot "PASS" without a file is fraud, and this
///    suite is what catches it.
/// </summary>
public sealed class UiIsolationTestSuite
{
    public Task<IReadOnlyList<UpdateSuiteCase>> RunAsync(string workspace, string? acceptanceResultPath,
        CancellationToken token = default)
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

        // ---- 单实例隔离语义 ----
        Case("不同实例标识的会话可以共存", () =>
        {
            // A production instance and a visual-acceptance session use different identities, so
            // neither can exclude the other — the precondition for "验收绝不影响正式实例".
            using var production = new SingleInstanceCoordinator("Global\\FNSyncPlus_test_production");
            using var sessionA = new SingleInstanceCoordinator("Global\\FNSyncPlus_test_isolated_A");
            using var sessionB = new SingleInstanceCoordinator("Global\\FNSyncPlus_test_isolated_B");
            return (production.IsPrimary && sessionA.IsPrimary && sessionB.IsPrimary,
                $"production={production.IsPrimary} A={sessionA.IsPrimary} B={sessionB.IsPrimary}");
        });

        Case("同一实例标识仍然互相排除", () =>
        {
            using var first = new SingleInstanceCoordinator("Global\\FNSyncPlus_test_same");
            using var second = new SingleInstanceCoordinator("Global\\FNSyncPlus_test_same");
            return (first.IsPrimary && !second.IsPrimary,
                $"first={first.IsPrimary} second={second.IsPrimary}");
        });

        Case("纯视觉验收的会话配置不携带凭据", () =>
        {
            // The visual fixture writes RememberCredentials=false and no server; an isolated
            // session built from it must not surface any saved credential path.
            var settings = AppSettings.LoadIsolatedTestSession(Guid.NewGuid());
            settings.RememberCredentials = false;
            settings.NasServer = "";
            settings.Save();
            var reloaded = AppSettings.LoadIsolatedTestSession(Guid.NewGuid());
            bool clean = reloaded.Tasks.Count == 0 && !reloaded.RememberCredentials;
            // Fixture-loaded in-memory settings for visual runs also start with no credentials.
            var unbounded = AppSettings.CreateUnboundedIsolatedSession();
            bool unboundedClean = !unbounded.RememberCredentials;
            return (clean && unboundedClean,
                $"reloadedTasks={reloaded.Tasks.Count} remember={reloaded.RememberCredentials} " +
                $"unboundedRemember={unbounded.RememberCredentials}");
        });

        // ---- 验收产物审计 ----
        if (!string.IsNullOrEmpty(acceptanceResultPath) && File.Exists(acceptanceResultPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(acceptanceResultPath));
                var root = document.RootElement;
                var results = root.TryGetProperty("cases", out var casesElement) ? casesElement : default;
                var captures = root.TryGetProperty("captures", out var capturesElement) ? capturesElement : default;

                Case("验收结果中的截图文件全部真实存在", () =>
                {
                    if (captures.ValueKind != JsonValueKind.Array)
                        return (false, "captures 不是数组");
                    var missing = new System.Collections.Generic.List<string>();
                    int checkedCount = 0;
                    foreach (var capture in captures.EnumerateArray())
                    {
                        if (!capture.TryGetProperty("screenshot", out var shot) ||
                            shot.GetString() is not { } path || !File.Exists(path))
                        {
                            missing.Add(capture.TryGetProperty("page", out var page) ? page.GetString() ?? "?" : "?");
                        }
                        checkedCount++;
                    }
                    return (missing.Count == 0 && checkedCount > 0,
                        $"captures={checkedCount} missing={missing.Count} {string.Join(";", missing.Take(3))}");
                });

                Case("每张截图都记录了会话、页面、DPI 与就绪等待", () =>
                {
                    if (captures.ValueKind != JsonValueKind.Array || captures.GetArrayLength() == 0)
                        return (false, "没有截图记录");
                    foreach (var capture in captures.EnumerateArray())
                    {
                        bool complete =
                            capture.TryGetProperty("session", out _) &&
                            capture.TryGetProperty("pid", out _) &&
                            capture.TryGetProperty("page", out _) &&
                            capture.TryGetProperty("dpi", out _) &&
                            capture.TryGetProperty("readyWaitMs", out _) &&
                            capture.TryGetProperty("theme", out _);
                        if (!complete)
                            return (false, "存在字段不完整的截图记录");
                    }
                    return (true, $"captures={captures.GetArrayLength()} 全部字段完整");
                });

                Case("受保护实例未被结束的断言存在且通过", () =>
                {
                    if (results.ValueKind != JsonValueKind.Array) return (false, "cases 不是数组");
                    var decoy = results.EnumerateArray()
                        .FirstOrDefault(c => c.TryGetProperty("name", out var n) &&
                                             (n.GetString() ?? "").Contains("受保护实例在全部场景结束后仍然存活"));
                    if (decoy.ValueKind == JsonValueKind.Undefined)
                        return (false, "缺少受保护实例存活断言");
                    return (decoy.GetProperty("passed").GetBoolean(),
                        decoy.GetProperty("detail").GetString() ?? "");
                });

                Case("页面未就绪或截图失败的场景被标记为 FAIL 而非略过", () =>
                {
                    if (results.ValueKind != JsonValueKind.Array) return (false, "cases 不是数组");
                    var readyChecks = results.EnumerateArray()
                        .Where(c => c.TryGetProperty("name", out var n) &&
                                    (n.GetString() ?? "").Contains("就绪信号"))
                        .ToList();
                    if (readyChecks.Count == 0) return (false, "没有页面就绪断言");
                    // All four pages × two themes must have been exercised.
                    int readyCount = readyChecks.Count(c => c.GetProperty("passed").GetBoolean());
                    return (readyCount == readyChecks.Count && readyChecks.Count >= 10,
                        $"readyAssertions={readyChecks.Count} passed={readyCount}");
                });
            }
            catch (Exception ex)
            {
                Case("验收结果文件可解析", () => (false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
        else if (!string.IsNullOrEmpty(acceptanceResultPath))
        {
            Case("验收结果文件存在", () => (false, $"未找到：{acceptanceResultPath}"));
        }

        return Task.FromResult<IReadOnlyList<UpdateSuiteCase>>(cases);
    }
}
