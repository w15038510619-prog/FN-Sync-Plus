using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Storage.FnOs;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Acceptance;

public static partial class AcceptanceCli
{
    private const string HistoryName = "版本/可恢复旧版.txt";
    private const string VersionOne = "FN Sync Plus - 历史版本 V1\r\n";
    private const string VersionTwo = "FN Sync Plus - 当前版本 V2\r\n";
    private sealed record Fixture(int Phase, Entry[] Current, Entry? History);

    private static async Task FixtureAsync(string command, Options options, List<Result> results, CancellationToken token)
    {
        string fixturePath = LocalPath(options.Require("--fixture"));
        if (command == "--verify-restored")
        {
            var fixture = await ReadFixtureAsync(fixturePath, token);
            string destination = LocalPath(options.Require("--target"));
            if (!Directory.Exists(destination)) throw new DirectoryNotFoundException();
            bool history = options.Has("--history");
            if (history && fixture.History == null) throw new Blocked("A phase-two fixture with an expected history version is required.");
            var expected = history ? [fixture.History!] : fixture.Current;
            var mismatches = await CheckFixtureAsync(destination, expected, token);
            results.Add(new(history ? "ManualHistoryRestore" : "ManualCurrentRestore", "Local", mismatches.Length == 0 ? "PASS" : "FAIL",
                new { fixture.Phase, Checked = expected.Length, MismatchedPaths = mismatches }, mismatches.Length == 0
                    ? "The restored local paths, sizes and SHA-256 hashes match the generated fixture."
                    : "One or more expected files or directories were missing or different."));
            return;
        }

        string source = LocalPath(options.Require("--source"));
        if (fixturePath.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fixturePath.Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidInput("Keep the verification JSON outside the folder the app backs up.");
        if (command == "--new-fixture")
        {
            if (Directory.Exists(source) || File.Exists(source) || File.Exists(fixturePath))
                throw new InvalidInput("Choose a new source folder and a new fixture JSON filename.");
            Directory.CreateDirectory(source);
            var files = new Dictionary<string, string>
            {
                ["普通/日常记录.txt"] = "普通中文文件\r\n",
                ["普通/" + new string('中', 84) + "ab.txt"] = "UTF-8 258-byte name\r\n",
                ["普通/" + new string('长', 98) + "ab.txt"] = "UTF-8 300-byte name\r\n",
                ["子目录/二层/三层/Unicode-λ-表格-🌱.txt"] = "Unicode 和嵌套路径\r\n",
                [HistoryName] = VersionOne
            };
            var entries = new List<Entry>();
            foreach (var (relative, content) in files)
            {
                string file = Under(source, relative);
                Directory.CreateDirectory(PathHelper.EnsureLongPathPrefix(Path.GetDirectoryName(file)!));
                var bytes = Encoding.UTF8.GetBytes(content);
                await using var stream = new FileStream(PathHelper.EnsureLongPathPrefix(file), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await stream.WriteAsync(bytes, token);
                entries.Add(new(relative, false, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
            }
            string[] empty = ["空目录", "子目录/二层/空目录"];
            foreach (string relative in empty)
            {
                Directory.CreateDirectory(Under(source, relative));
                entries.Add(new(relative, true, 0, null));
            }
            await WriteNewJsonAsync(fixturePath, new Fixture(1, Sort(entries), null), token);
            results.Add(new("CreateFixture", "Local", "PASS", new { Phase = 1, Files = files.Count, EmptyDirectories = empty.Length }, "Synthetic test files and a separate verification manifest were created."));
            return;
        }

        var original = await ReadFixtureAsync(fixturePath, token);
        if (original.Phase != 1 || original.History != null || (await CheckFixtureAsync(source, original.Current, token)).Length != 0 ||
            original.Current.SingleOrDefault(entry => entry.Path == HistoryName) is not { Directory: false } old ||
            old.Sha256 != HashText(VersionOne))
            throw new InvalidInput("Only update a matching phase-one fixture after the first backup.");
        byte[] updated = Encoding.UTF8.GetBytes(VersionTwo);
        string edited = Under(source, HistoryName);
        string nextFixture = fixturePath + ".v2.json";
        if (File.Exists(nextFixture)) throw new InvalidInput("The phase-two verification JSON already exists.");
        await using (var file = new FileStream(edited, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != old.Sha256)
                throw new InvalidInput("The editable test file changed during verification.");
            file.Position = 0;
            await file.WriteAsync(updated, token);
            file.SetLength(updated.Length);
        }
        var current = original.Current.Select(entry => entry.Path == HistoryName
            ? entry with { Bytes = updated.Length, Sha256 = HashText(VersionTwo) } : entry).ToArray();
        await WriteNewJsonAsync(nextFixture, new Fixture(2, current, old), token);
        results.Add(new("UpdateFixture", "Local", "PASS", new { Phase = 2, ManifestSuffix = ".v2.json" }, "The phase-two file and separate current/history verification manifest were created."));
    }

    private static async Task<Fixture> ReadFixtureAsync(string path, CancellationToken token)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllBytesAsync(path, token), Json);
        if (fixture == null || fixture.Phase is not (1 or 2) || fixture.Current is not { Length: > 0 })
            throw new InvalidDataException();
        ValidateEntries(fixture.Current);
        if (fixture.History != null) ValidateEntries([fixture.History]);
        return fixture;
    }

    private static async Task<string[]> CheckFixtureAsync(string root, Entry[] entries, CancellationToken token)
    {
        var mismatches = new List<string>();
        foreach (var entry in entries)
        {
            string path = Under(root, entry.Path);
            EnsureNoReparse(path);
            string extended = PathHelper.EnsureLongPathPrefix(path);
            if (entry.Directory)
            {
                if (!Directory.Exists(extended) || Directory.EnumerateFileSystemEntries(extended).Any()) mismatches.Add(entry.Path);
            }
            else if (!File.Exists(extended))
                mismatches.Add(entry.Path);
            else
            {
                await using var stream = File.OpenRead(extended);
                if (stream.Length != entry.Bytes ||
                    !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add(entry.Path);
            }
        }
        return mismatches.ToArray();
    }

    private static async Task SelfTestAsync(string scratch, List<Result> results, CancellationToken token)
    {
        void Check(string id, bool ok, string reason) => results.Add(new(id, "Local", ok ? "PASS" : "FAIL", null, reason));

        var fixtureResults = new List<Result>();
        await RestoreAcceptanceAsync(new("--test-restore-conflict-engine", ["--mode", "LocalFixture"]), false, scratch, fixtureResults, token);
        Check("FixtureLocalOnly", fixtureResults is [{ TestLevel: "Local", Status: "PASS" }], "RestoreEngine restores fixture bytes and skips the seeded conflict at Local level.");

        var source = Path.Combine(scratch, "missing-manifest");
        Directory.CreateDirectory(source);
        await using var provider = new SmbStorageProvider(rootUncPath: source);
        bool missing = false;
        try { await ReadBackupAsync(provider, "", token); }
        catch (Blocked) { missing = true; }
        Check("MissingManifestFailsClosed", missing, "The actual provider backup reader does not substitute a fixture.");
        Directory.CreateDirectory(Path.Combine(source, ".fnsync", "manifest-versions"));
        bool invalid = false;
        try { await ReadBackupAsync(provider, "", token); }
        catch (InvalidDataException) { invalid = true; }
        Check("InvalidVersionedManifestFailsClosed", invalid, "An empty versioned manifest directory cannot pass.");

        string committed = Path.Combine(scratch, "versioned");
        await CreateLocalBackupAsync(committed, token);
        var sourceManifest = await new ManifestManager().LoadManifestAsync(committed, token);
        await using var versionProvider = new SmbStorageProvider(rootUncPath: scratch);
        var stored = await new VersionedManifestStore().SaveAsync(versionProvider, "versioned", sourceManifest, null, token);
        var pinned = await ReadBackupAsync(versionProvider, "versioned", token);
        var verified = await TestConflictAsync(versionProvider, pinned, scratch, "LocalFixture", "LocalFileSystem", "versioned-fixture", token);
        Check("VersionedSnapshotRestored", pinned.Package.VersionId == stored.Id && verified.Status == "PASS" &&
            verified.TestLevel == "Local", "A committed versioned manifest takes precedence and is restored through the production RestoreEngine.");
        File.WriteAllText(Path.Combine(committed, ".fnsync", "manifest-versions", "commit-invalid.json"), "{}");
        bool rejectedDamage = false;
        try { await ReadBackupAsync(versionProvider, "versioned", token); }
        catch (InvalidDataException) { rejectedDamage = true; }
        Check("DamagedVersionHistoryRejected", rejectedDamage, "A damaged version record blocks use of both older commits and the legacy manifest.");

        var dual = new List<Result>();
        await DualAccountAsync(new("--test-dual-account-live", []), dual, token);
        Check("NoCredentialsBlocked", dual.Count == DualTests.Length && ExitCode(dual) == 2 && dual.All(row => row.Status == "BLOCKED"), "No-account reports retain one BLOCKED result per real test.");
        Check("PermissionClassifications", ClassifyProbe(new() { Result = "fail", ErrNo = 403 }).Status == "PASS" &&
            ClassifyProbe(new() { Result = "fail", ErrNo = 404 }).Status == "BLOCKED" &&
            ClassifyProbe(new() { Result = "succ", Files = [] }).Status == "BLOCKED" &&
            ClassifyProbe(new() { Result = "succ", Files = [new FnOsFileEntry()] }).Status == "FAIL" &&
            ClassifyProbe(new() { Result = "fail", ErrNo = 9999 }).Status == "BLOCKED" &&
            Error("TokenIsolation", "RealNAS", new System.Net.WebSockets.WebSocketException()).Reason.Contains("Connection"),
            "Explicit denial, access, missing path, empty directory, network failure and unknown replies are handled separately.");
        Check("WorkerBlocksAggregate", ExitCode([new("AuthA", "RealNAS", "PASS", null, ""), new("WorkerStop", "RealNAS", "BLOCKED", null, "")]) == 2,
            "Successful authentication cannot produce an all-pass report when the worker is unverified.");
        Check("AuditEvidenceRequired", Audit([], [], true).Status == "BLOCKED" &&
            Audit(["http.upload", "file.rm"], ["http.upload"], true).Status == "FAIL" &&
            Audit(["http.upload", "unknown"], ["http.upload"], true).Status == "BLOCKED" &&
            Audit(["http.upload"], ["http.upload"], true).Status == "PASS",
            "Audit result requires both traces, classified operations and no deletion.");
        var baseline = new Baseline(1, Guid.NewGuid(), "SmbShare", HashText("source"), "snapshot",
            [new("old.txt", false, 1, HashText("old")), new("same-name.txt", false, 1, HashText("old"))]);
        Check("SnapshotChangesDetected", Compare(baseline, "snapshot", baseline.Entries, "BeforeRestore").Status == "PASS" &&
            Compare(baseline, "snapshot", [new("new.txt", false, 1, HashText("old")), new("same-name.txt", false, 1, HashText("new"))], "AfterRestore").Status == "FAIL" &&
            Compare(baseline, "different", baseline.Entries, "AfterRestore").Status == "FAIL",
            "Snapshot comparison detects changed bytes, added and removed paths, and a changed snapshot identity.");
    }
}
