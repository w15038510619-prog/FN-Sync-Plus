using System.Security.Cryptography;
using System.Text.Json;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] is not ("--live" or "--inspect-source")))
    throw new ArgumentException("Pass a fresh local evidence directory and optionally --live or --inspect-source.");
string output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Evidence directory must be fresh.");
Directory.CreateDirectory(output);
bool inspectOnly = args.Contains("--inspect-source");
bool live = args.Contains("--live") || inspectOnly;
var settings = new AppSettings { IsInMemoryOnly = true };
var auth = new SmbAuthService();
using var manager = new TaskManager(settings, auth, new LogService(Path.Combine(output, "logs")), experimentalVersionedBackup: true);
var connection = new NasConnectionService(auth, settings, manager);
const string sandbox = "Codex_FN_Test_20261106_071556_94aea127";
const string priorBackup = "Codex_FN_Test_001/source";
INasStorageProvider provider;
string parent;
var checks = new List<string>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidDataException(name);
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
if (live)
{
    var saved = AppSettings.LoadVersionedBetaProfile();
    var credential = SmbAuthService.LoadCredentials(saved.IsolatedCredentialPath)
        ?? throw new InvalidOperationException("BLOCKED: no saved NAS login.");
    settings.TrustedCertThumbprints = saved.TrustedCertThumbprints;
    var login = await connection.ConnectAsync(credential.Server, credential.Username, credential.Password, false);
    if (!login.Success || connection.CurrentStorageProvider is not FnOsPersonalStorageProvider personal)
        throw new IOException("BLOCKED: cannot connect to fnOS personal space.");
    provider = personal;
    if (!await provider.ExistsAsync(sandbox)) throw new IOException("Authorized test sandbox is missing.");
    parent = sandbox + "/Restore046_" + Guid.NewGuid().ToString("N");
}
else
{
    string mock = Path.Combine(output, "nas");
    Directory.CreateDirectory(mock);
    provider = new TeamSmbStorageProvider(mock);
    parent = "Restore046_" + Guid.NewGuid().ToString("N");
}

var restore = new RestoreEngine(new ManifestManager());
string? originalSource = null;
if (live)
{
    try
    {
        var original = await restore.DiscoverProviderPackageAsync(provider, priorBackup, "source", default);
        if (original != null)
        {
            var data = await restore.ReadPackageManifestAsync(provider, original);
            originalSource = data.SourceRoot;
            await File.WriteAllTextAsync(Path.Combine(output, "existing-source-manifest-summary.json"),
                JsonSerializer.Serialize(new { originalSource, original.VersionId, Files = data.Files.Select(f => f.OriginalRel),
                    Directories = data.Directories.Select(d => d.OriginalRel) }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    catch (Exception error) { Console.WriteLine("Existing source could not be read: " + error.GetType().Name); }
    if (inspectOnly)
    {
        await connection.DisconnectAsync();
        Environment.ExitCode = originalSource == null ? 1 : 0;
        return;
    }
}

string folder = Path.Combine(output, "fixture");
string current = Path.Combine(output, "current");
string historical = Path.Combine(output, "historical");
string entire = Path.Combine(output, "entire");
string longName = new string('长', 90) + ".txt";
string[] paths = ["子目录/文件A.xlsx", "子目录/" + longName, "普通/文件C.xlsx", "普通/文件D.xlsx", "版本/二级/文件E.xlsx"];
foreach (string path in paths)
{
    string file = Path.Combine(folder, path);
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    await File.WriteAllTextAsync(file, "V1 " + path);
}
Directory.CreateDirectory(Path.Combine(folder, "普通", "空目录"));
if (!(await provider.CreateDirectoryAsync(parent)).Success) throw new IOException("Cannot create fresh test parent.");
string backup = parent + "/source";
var engine = new ExperimentalVersionedBackup();
await engine.CreateNewAsync(provider, folder, backup, settlingDelaySeconds: 1);
string changed = Path.Combine(folder, paths[2]);
await File.WriteAllTextAsync(changed, "第二版");
await engine.RunAsync(provider, folder, backup, settlingDelaySeconds: 1);
string other = parent + "/另一份测试备份";
var otherFolder = Path.Combine(output, "other");
Directory.CreateDirectory(otherFolder);
await File.WriteAllTextAsync(Path.Combine(otherFolder, "根目录文件.txt"), "另一份备份");
await engine.CreateNewAsync(provider, otherFolder, other, settlingDelaySeconds: 1);

var package = await restore.DiscoverProviderPackageAsync(provider, backup, "source", default)
    ?? throw new IOException("Fixture backup not found.");
var manifest = await restore.ReadPackageManifestAsync(provider, package);
Check(manifest.Files.Count == 5 && manifest.Files.All(f => f.OriginalRel.Contains('/')) &&
      manifest.Files.Select(f => f.OriginalRel.Split('/')[0]).Distinct().Count() == 3, "Five files under three root folders");
try
{
    await restore.RestorePackageSelectionAsync(provider, package, current, [], [], ConflictResolution.SkipExisting);
    throw new InvalidDataException("Empty selection was accepted.");
}
catch (ArgumentException) { Check(!Directory.Exists(current), "Empty selection performs no writes"); }

var files = manifest.Files.Where(f => f.OriginalRel.StartsWith("普通/") || f.OriginalRel.EndsWith(longName)).ToArray();
var directories = manifest.Directories.Where(d => d.OriginalRel is "普通" or "普通/空目录").Select(d => d.OriginalRel).ToArray();
var report = await restore.RestorePackageSelectionAsync(provider, package, current, files.Select(f => f.OriginalRel).ToArray(),
    directories, ConflictResolution.SkipExisting);
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
Check(report.IntegrityVerifiedFiles == 3 && Directory.GetFiles(current, "*", SearchOption.AllDirectories).Length == 3 &&
      Directory.Exists(Path.Combine(current, "普通", "空目录")), "Folder and individual selection restores exact scope including empty directory");
Check(files.All(f => Hash(Path.Combine(current, f.OriginalRel)).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)),
      "Selected paths, Chinese/long names and SHA-256");
var repeated = await restore.RestorePackageSelectionAsync(provider, package, current, files.Select(f => f.OriginalRel).ToArray(),
    directories, ConflictResolution.SkipExisting);
Check(repeated.SkippedFiles == 3 && repeated.RestoredFiles == 0, "Existing files are skipped");
var version = manifest.History.Single(h => h.OriginalRel == paths[2]);
var history = await restore.RestorePackageHistoryAsync(provider, package, historical, version);
Check(history.IntegrityVerifiedFiles == 1 && (await File.ReadAllTextAsync(Path.Combine(historical, paths[2]))) == "V1 " + paths[2],
      "Historical version preserves relative path and original verified contents");
var all = await restore.RunRestorePackageAsync(provider, package, entire);
Check(all.IntegrityVerifiedFiles == 5 && manifest.Files.All(f =>
    Hash(Path.Combine(entire, f.OriginalRel)).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)), "Explicit entire backup matches all five hashes");
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
{
    live, backup, other, originalSource, checks, current, historical, entire,
    expected = manifest.Files.ToDictionary(f => f.OriginalRel, f => f.Sha256)
}, new JsonSerializerOptions { WriteIndented = true }));
if (live) await connection.DisconnectAsync();
