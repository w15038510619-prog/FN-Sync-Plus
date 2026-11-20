using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Models;

string output = args.Contains("--out") ? args[Array.IndexOf(args, "--out") + 1] : Path.Combine(Path.GetTempPath(), "FN044_" + Guid.NewGuid().ToString("N"));
output = Path.GetFullPath(output);
Directory.CreateDirectory(output);
if (!args.Contains("--live") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppStorageRoot.OverrideEnvironmentVariable)))
    Environment.SetEnvironmentVariable(AppStorageRoot.OverrideEnvironmentVariable, Path.Combine(output, "runtime"));
var results = new List<object>();
int failed = 0, blocked = 0, passed = 0;
void Record(string name, string status, string detail = "")
{
    results.Add(new { name, status, detail });
    Console.WriteLine($"[{status}] {name} {detail}");
    if (status == "PASS") passed++; else if (status == "FAIL") failed++; else if (status == "BLOCKED") blocked++;
}
async Task Check(string name, Func<Task> action)
{
    try { await action(); Record(name, "PASS"); }
    catch (Exception ex) { Record(name, "FAIL", ex.GetType().Name + ": " + ex.Message); }
}
void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
async Task<string> Hash(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream));
}

async Task Chain(INasStorageProvider provider, string target, string label, dynamic? workbook = null)
{
    string source = Path.Combine(output, label + "-source"); Directory.CreateDirectory(source);
    Directory.CreateDirectory(Path.Combine(source, "空文件夹"));
    string file = Path.Combine(source, "已保存的表格.xlsx");
    if (workbook != null) { workbook.Worksheets[1].Cells[1, 1].Value2 = "saved V1"; workbook.SaveAs(file, 51); }
    else await File.WriteAllTextAsync(file, "saved V1");
    await File.WriteAllTextAsync(Path.Combine(source, "零字节.txt"), "");
    if (workbook == null) await File.WriteAllTextAsync(Path.Combine(source, "~$已保存的表格.xlsx"), "owner file");
    string firstHash = await Hash(file);
    var engine = new ExperimentalVersionedBackup();
    var first = await engine.CreateNewAsync(provider, source, target, settlingDelaySeconds: 1);
    Require(first.Snapshot.Manifest.Files.Count == 2, "Owner file filtering or zero-byte handling failed");
    var plan = await new ExperimentalPhysicalSnapshot().PrepareAsync(provider, target);
    var publication = await new ExperimentalFirstProjection().PublishAsync(provider, target, plan);
    Record(label + " first backup and physical publication", "PASS");
    if (workbook != null) { workbook.Worksheets[1].Cells[1, 1].Value2 = "saved V2"; workbook.Save(); }
    else await File.WriteAllTextAsync(file, "saved V2");
    string secondHash = await Hash(file);
    var next = await engine.RunAsync(provider, source, target, settlingDelaySeconds: 1);
    var nextPlan = await new ExperimentalPhysicalSnapshot().PrepareAsync(provider, target);
    await new ExperimentalNextProjection().PublishAsync(provider, target, plan, publication, nextPlan);
    Require(next.Snapshot.Manifest.History.Count > 0, "Missing previous version");
    Record(label + " incremental and history retention", "PASS");
    var package = new DiscoveredBackupPackage { NasFolderUnc = target, VersionId = next.Snapshot.Id, PackageName = label };
    var restore = new RestoreEngine(new ManifestManager());
    string current = Path.Combine(output, label + "-current");
    var currentReport = await restore.RunRestorePackageAsync(provider, package, current);
    Require(currentReport.FailedFiles == 0 && currentReport.IntegrityVerifiedFiles == 2 &&
        await Hash(Path.Combine(current, Path.GetFileName(file))) == secondHash, "Current restore hash mismatch");
    Record(label + " current restore SHA-256", "PASS");
    var history = next.Snapshot.Manifest.History.Single(h => h.OriginalRel == Path.GetFileName(file));
    string old = Path.Combine(output, label + "-history");
    var historyReport = await restore.RestorePackageHistoryAsync(provider, package, old, history);
    Require(historyReport.FailedFiles == 0 && historyReport.IntegrityVerifiedFiles == 1 &&
        await Hash(Path.Combine(old, Path.GetFileName(file))) == firstHash, "History restore hash mismatch");
    Record(label + " historical restore SHA-256", "PASS");
    if (workbook != null) Record(label + " WPS saved workbook stayed open through both backups", "PASS");
}

if (!args.Contains("--live"))
{
    await Check("Signed Stable manifest retains its actual channel", () =>
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = new FNSyncPlus.Core.Updates.UpdateManifest { Version = "0.4.4", Channel = "stable",
            Url = "https://example.org/release.zip", Size = 1, Sha256 = new string('a', 64), ReleasedAt = DateTimeOffset.UtcNow };
        string signed = FNSyncPlus.Core.Updates.UpdateManifestSigner.Sign(manifest, key);
        var verifier = FNSyncPlus.Core.Updates.UpdateManifestVerifier.FromPem(key.ExportSubjectPublicKeyInfoPem());
        foreach (var requested in new[] { FNSyncPlus.Core.Updates.UpdateChannel.Stable, FNSyncPlus.Core.Updates.UpdateChannel.Beta })
        {
            var verified = verifier.Verify(signed, new("", 0), requested, new Version(0, 4, 3), DateTimeOffset.UtcNow);
            Require(verified.Ok && verified.Channel == FNSyncPlus.Core.Updates.UpdateChannel.Stable, "Stable was mislabeled Beta after verification");
        }
        return Task.CompletedTask;
    });
    await Check("Native connection does not supply storage to local tasks", async () =>
    {
        var settings = new AppSettings { IsInMemoryOnly = true };
        using var manager = new TaskManager(settings, new SmbAuthService());
        var connection = new NasConnectionService(new SmbAuthService(), settings, manager);
        var provider = new SmbStorageProvider(rootUncPath: output);
        typeof(NasConnectionService).GetProperty(nameof(NasConnectionService.CurrentStorageProvider))!.SetValue(connection, provider);
        Require(await connection.GetTaskStorageAsync(new SyncTask { ProviderType = StorageProviderType.LocalFileSystem }, default) == null,
            "A local task was redirected to the active NAS provider");
        await connection.DisconnectAsync();
    });
    await Check("fnOS team roots exclude personal and ordinary SMB exports", () =>
    {
        Require(SmbAuthService.IsFnOsTeamShare("团队文件-cenco-public") &&
            SmbAuthService.FnOsTeamShareDisplayName("团队文件-cenco-public") == "cenco-public" &&
            !SmbAuthService.IsFnOsTeamShare("Codex_FN_Test_001") && !SmbAuthService.IsFnOsTeamShare("数据库电表") &&
            !SmbAuthService.IsFnOsTeamShare("vol1") && !SmbAuthService.IsFnOsTeamShare("public_docs") &&
            SmbAuthService.CategorizeShare("08_正常子目录", "user") == NasShareCategory.SystemOrOther, "An unrelated SMB export was presented as an fnOS team folder");
        return Task.CompletedTask;
    });
    await Check("Stable defaults and existing custom feed preservation", () =>
    {
        string policy = Path.Combine(output, "update-policy.json");
        var defaults = FNSyncPlus.Core.Updates.UpdateService.LoadSettings(policy);
        Require(defaults.Channel == "stable" && defaults.ManifestUrl == FNSyncPlus.Core.Updates.UpdateSettings.DefaultStableManifestUrl &&
            defaults.AutoMode == 0 && !defaults.UnattendedEnabled, "Unsafe new-install update defaults");
        File.WriteAllText(policy, "{\"channel\":\"beta\",\"manifest_url\":\"https://example.org/custom/manifest.json\",\"auto_mode\":1}");
        var custom = FNSyncPlus.Core.Updates.UpdateService.LoadSettings(policy);
        Require(custom.Channel == "beta" && custom.ManifestUrl == "https://example.org/custom/manifest.json" && custom.AutoMode == 1, "Custom settings were overwritten");
        File.WriteAllText(policy, "{\"channel\":\"beta\",\"manifest_url\":\"https://dev.sk-fj.com:8081/fnsync/beta/manifest.json\"}");
        var migrated = FNSyncPlus.Core.Updates.UpdateService.LoadSettings(policy);
        Require(migrated.Channel == "stable" && migrated.ManifestUrl == defaults.ManifestUrl, "Official feed did not migrate");
        return Task.CompletedTask;
    });
    await Check("Shared writer permits stable saved bytes", async () =>
    {
        string file = Path.Combine(output, "shared.xlsx"); await File.WriteAllTextAsync(file, "saved shared bytes");
        await using var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        await using var snapshot = await StableFileSnapshot.OpenAsync(file, default);
        Require(Convert.ToHexString(await SHA256.HashDataAsync(snapshot)) == await Hash(file), "Snapshot mismatch");
    });
    await Check("Exclusive writer is rejected", async () =>
    {
        string file = Path.Combine(output, "locked.xlsx"); await File.WriteAllTextAsync(file, "locked");
        using var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try { await using var snapshot = await StableFileSnapshot.OpenAsync(file, default); }
        catch (IOException) { return; }
        throw new InvalidDataException("Exclusive writer bypassed");
    });
    await Check("Office lock filtering retains normal temporary files", () =>
    {
        Require(StableFileSnapshot.IsOfficeOwnerFile("~$test.xlsx") && !StableFileSnapshot.IsOfficeOwnerFile("user.tmp") &&
            !StableFileSnapshot.IsOfficeOwnerFile("~$user.tmp"), "Broad or missing filter"); return Task.CompletedTask;
    });
    await Check("SMB provider local filesystem protocol regression", async () =>
    {
        string root = Path.Combine(output, "simulated-share"); Directory.CreateDirectory(root);
        var provider = new TeamSmbStorageProvider(root);
        await Chain(provider, "backup", "simulated-team");
        Require(await provider.TryCreateDirectoryExclusiveAsync("backup") == ExclusiveDirectoryResult.AlreadyExists, "Exclusive creation overwrote existing directory");
        try { await provider.MoveCurrentToArchiveAsync("backup", "different-backup/archive"); }
        catch (IOException) { return; }
        throw new InvalidDataException("Cross-backup move allowed");
    });
}
else
{
    var saved = AppSettings.LoadVersionedBetaProfile();
    var credential = SmbAuthService.LoadCredentials(saved.IsolatedCredentialPath);
    if (!credential.HasValue) Record("Real NAS", "BLOCKED", "No saved test-capable NAS credential");
    else
    {
        var settings = new AppSettings { IsInMemoryOnly = true, NasServer = credential.Value.Server,
            Username = credential.Value.Username, TrustedCertThumbprints = saved.TrustedCertThumbprints };
        using var manager = new TaskManager(settings, new SmbAuthService(), new LogService(Path.Combine(output, "task-logs")), experimentalVersionedBackup: true);
        var connection = new NasConnectionService(new SmbAuthService(), settings, manager);
        var login = await connection.ConnectAsync(credential.Value.Server, credential.Value.Username, credential.Value.Password, false);
        if (!login.Success) Record("Real NAS login", "BLOCKED", login.Message);
        else
        {
            if (args.Contains("--list-team-shares"))
            {
                var shares = await connection.GetAccessibleTeamSharesAsync();
                Require(shares.Count == 1 && FNSyncPlus.Core.Utils.PathHelper.ParseUncShare(shares[0]).ShareName == "团队文件-cenco-public", "Unexpected fnOS team directory list");
                await File.WriteAllTextAsync(Path.Combine(output, "team-shares.json"), JsonSerializer.Serialize(shares,
                    new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                Record("Real fnOS team list contains only cenco-public", "PASS");
                await connection.DisconnectAsync();
                return 0;
            }
            if (args.Contains("--cleanup-owned-fixture"))
            {
                string owned = (await File.ReadAllTextAsync(Path.Combine(output, "readonly-fixture.txt"))).Trim();
                string allowed = @"\\192.168.8.120\团队文件-cenco-public\Release044_5659e37752514f5e9d75353e2eb7f58c";
                Require(owned == allowed, "Cleanup record does not identify the owned fixture");
                var provider = await connection.ConnectTeamShareAsync(owned);
                string child = Path.GetFileName(owned);
                var snapshot = await new VersionedManifestStore().LoadAsync(provider, child);
                Require(snapshot?.Manifest.SourceRoot == Path.Combine(output, "real-team-source"), "Fixture ownership differs: cleanup refused");
                await provider.DeleteAsync(child, moveToTrash: false);
                Require(!await provider.ExistsAsync(child), "Fixture cleanup failed");
                Record("Verified owned team fixture removed", "PASS", child);
                await File.WriteAllTextAsync(Path.Combine(output, "cleanup.json"), JsonSerializer.Serialize(new { root = owned, status = "PASS", sourceVerified = true }));
                await connection.DisconnectAsync();
                return 0;
            }
            string id = "Release044_" + Guid.NewGuid().ToString("N");
            var personal = (FnOsPersonalStorageProvider)connection.CurrentStorageProvider!;
            var availableShares = await connection.GetAccessibleTeamSharesAsync();
            await File.WriteAllTextAsync(Path.Combine(output, "accessible-shares.json"), JsonSerializer.Serialize(availableShares));
            string personalRoot = "Codex_FN_Test_20261106_071556_94aea127/" + id;
            // Remove only earlier runs whose manifest proves this exact acceptance source.
            string cleanupShare = @"\\192.168.8.120\Codex_FN_Test_20261106_071556_94aea127";
            var cleanupProvider = await connection.ConnectTeamShareAsync(cleanupShare);
            foreach (var directory in Directory.EnumerateDirectories(cleanupShare, "Release044_*"))
            {
                string child = Path.GetFileName(directory);
                var snapshot = await new VersionedManifestStore().LoadAsync(cleanupProvider, child);
                string? oldSource = snapshot?.Manifest.SourceRoot;
                Console.WriteLine("Cleanup candidate " + child + " source=" + oldSource);
                string[] ownedSources = { @"D:\FN_Sync_Plus_RC_20261118\044-real-nas\real-personal-source", @"D:\FN_Sync_Plus_RC_20261118\044-real-nas-2\real-personal-source" };
                if (oldSource != null && (ownedSources.Contains(oldSource, StringComparer.OrdinalIgnoreCase) || (oldSource.StartsWith(@"D:\FN_Sync_Plus_RC_20261118\044-real-nas-", StringComparison.OrdinalIgnoreCase) && oldSource.EndsWith(@"\real-personal-source", StringComparison.OrdinalIgnoreCase))))
                {
                    var native = await new VersionedManifestStore().LoadAsync(personal, "Codex_FN_Test_20261106_071556_94aea127/" + child);
                    Require(native?.Id == snapshot!.Id, "Cleanup mapping mismatch: " + native?.Id + " vs " + snapshot!.Id);
                    var deletion = await cleanupProvider.DeleteAsync(child, moveToTrash: false);
                    Require(deletion.Success && !await personal.ExistsAsync("Codex_FN_Test_20261106_071556_94aea127/" + child), "Previous test cleanup failed");
                    Record("Earlier owned test directory removed", "PASS", child);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(output, "remote-test-root.txt"), personalRoot);
            if (!args.Contains("--skip-personal")) await Check("Real personal files", async () =>
            {
                try { await Chain(personal, personalRoot, "real-personal"); }
                finally
                {
                    string sandboxShare = @"\\192.168.8.120\Codex_FN_Test_20261106_071556_94aea127";
                    var smb = await connection.ConnectTeamShareAsync(sandboxShare);
                    var nativeVersion = await new VersionedManifestStore().LoadAsync(personal, personalRoot);
                    var smbVersion = await new VersionedManifestStore().LoadAsync(smb, id);
                    Require(nativeVersion != null && smbVersion?.Id == nativeVersion.Id, "Cleanup SMB mapping does not match the native test directory");
                    var cleanup = await smb.DeleteAsync(id, moveToTrash: false);
                    Require(cleanup.Success && !await personal.ExistsAsync(personalRoot), "Personal test cleanup failed");
                    Record("Personal test directory removed", "PASS");
                }
            });
            try
            {
                var shares = availableShares;
                string? share = shares.SingleOrDefault(p => FNSyncPlus.Core.Utils.PathHelper.ParseUncShare(p).ShareName.Equals("cenco-public", StringComparison.OrdinalIgnoreCase) ||
                    FNSyncPlus.Core.Utils.PathHelper.ParseUncShare(p).ShareName.Equals("团队文件-cenco-public", StringComparison.OrdinalIgnoreCase));
                if (share == null) Record("Real team files", "BLOCKED", "cenco-public is not accessible through the authorized SMB account");
                else
                {
                    var team = await connection.ConnectTeamShareAsync(share);
                    dynamic? application = null, book = null;
                    foreach (string progId in new[] { "ket.Application", "ET.Application", "Excel.Application" })
                    {
                        try
                        {
                            var type = Type.GetTypeFromProgID(progId);
                            if (type == null) continue;
                            application = Activator.CreateInstance(type);
                            book = application!.Workbooks.Add();
                            Record("Real spreadsheet application", "PASS", progId);
                            break;
                        }
                        catch (COMException) { }
                    }
                    if (book == null) Record("Real Excel/WPS", "BLOCKED", "Spreadsheet COM automation unavailable");
                    try
                    {
                        await Check("Real team backup and restore", async () => await Chain(team, id, "real-team", book));
                        await Check("Full UNC discovery and selected restore", async () =>
                        {
                            string full = share + "\\" + id;
                            var discovered = await new RestoreEngine(new ManifestManager()).DiscoverBackupPackagesAsync(full);
                            Require(discovered.Count == 1, "Expected exactly the owned versioned backup");
                            var pkg = discovered.Single();
                            var report = await new RestoreEngine(new ManifestManager()).RetryRestorePackageAsync(team, pkg,
                                Path.Combine(output, "selected-restore"), new[] { "已保存的表格.xlsx" });
                            Require(report.FailedFiles == 0 && report.IntegrityVerifiedFiles == 1, "Full UNC selected restore failed");
                        });
                        await Check("Normal team task entry", async () =>
                        {
                            string source = Path.Combine(output, "normal-task-source"); Directory.CreateDirectory(source);
                            await File.WriteAllTextAsync(Path.Combine(source, "task.txt"), "TaskManager V1");
                            string taskRoot = id + "-task";
                            var task = new SyncTask { TaskName = "Release044 task acceptance", SourceFolder = source,
                                DestinationFolder = share + "\\" + taskRoot, ProviderType = StorageProviderType.SmbShare,
                                NasUsername = connection.Username, NasAccountUid = connection.CurrentUid,
                                NasDeviceId = connection.ServerAddress, ScheduleMode = TaskSyncScheduleMode.ManualOnly };
                            try
                            {
                                manager.AddTask(task); manager.TriggerSync(task.TaskId);
                                var timeout = DateTime.UtcNow.AddSeconds(90);
                                while (manager.HasRunningOrQueuedWork && DateTime.UtcNow < timeout) await Task.Delay(200);
                                Require(!manager.HasRunningOrQueuedWork && task.State == TaskSyncState.Completed && task.LastSyncTime.HasValue,
                                    "Task did not commit: " + task.State + ": " + task.LastError);
                                var current = await new VersionedManifestStore().LoadAsync(team, taskRoot);
                                Require(current?.Manifest.Files.Count == 1 && !task.ExperimentalPendingInitialization, "Normal task initialization failed");
                                await File.WriteAllTextAsync(Path.Combine(source, "task.txt"), "TaskManager V2");
                                manager.TriggerSync(task.TaskId); timeout = DateTime.UtcNow.AddSeconds(90);
                                while (manager.HasRunningOrQueuedWork && DateTime.UtcNow < timeout) await Task.Delay(200);
                                Require(task.State == TaskSyncState.Completed, "Incremental task failed: " + task.LastError);
                                current = await new VersionedManifestStore().LoadAsync(team, taskRoot);
                                Require(current?.Manifest.History.Count == 1, "Normal task lost history");
                            }
                            finally
                            {
                                Require(!manager.HasRunningWork, "Task still writing: cleanup refused");
                                await manager.RemoveTaskAsync(task.TaskId);
                                await team.DeleteAsync(taskRoot, moveToTrash: false);
                                Require(!await team.ExistsAsync(taskRoot), "Normal task cleanup failed");
                            }
                        });
                    }
                    finally
                    {
                        if (book != null) { book.Close(false); Marshal.FinalReleaseComObject(book); }
                        // Close only this test workbook. Existing user workbooks and the application remain open.
                        if (application != null) Marshal.FinalReleaseComObject(application);
                        if (args.Contains("--keep-fixture"))
                        {
                            await File.WriteAllTextAsync(Path.Combine(output, "readonly-fixture.txt"), share + "\\" + id);
                            Record("Read-only test fixture prepared", "PASS", share + "\\" + id);
                        }
                        else
                        {
                        var cleanup = await team.DeleteAsync(id, moveToTrash: false);
                        Require(!await team.ExistsAsync(id), "Team test cleanup failed: " + cleanup.ErrorMessage);
                        Record("Team test directory removed", "PASS", share + "\\" + id);
                        }
                    }
                }
            }
            catch (Exception ex) { Record("Real team connectivity", "BLOCKED", ex.Message); }
            await connection.DisconnectAsync();
        }
    }
}
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed, failed, blocked, results }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"PASS {passed} / FAIL {failed} / BLOCKED {blocked}");
return failed > 0 ? 1 : blocked > 0 ? 2 : 0;
