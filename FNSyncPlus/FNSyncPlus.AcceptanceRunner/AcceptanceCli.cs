using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.Acceptance;

public static partial class AcceptanceCli
{
    private sealed record Result(string TestId, string TestLevel, string Status, object? Evidence, string Reason);
    private sealed class Blocked(string reason) : Exception(reason);
    private sealed class InvalidInput(string reason) : Exception(reason);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ConsoleJson = new(Json) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    private static readonly string Build = typeof(AcceptanceCli).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    public static async Task<int> Main(string[] args) => await RunAsync(args);

    public static async Task<int> RunAsync(string[] args)
    {
        string command = args.FirstOrDefault() ?? "--help";
        if (command == "--help" && args.Length <= 1)
        {
            Console.WriteLine("""
                FNSyncPlus.AcceptanceRunner (Windows x64)
                --version
                --new-fixture --source <new local directory> --fixture <new fixture.json>
                --update-fixture --source <local test directory> --fixture <fixture.json>
                --verify-restored --target <local directory> --fixture <fixture.json> [--history]
                --test-restore-conflict-engine --mode LocalFixture
                --snapshot-before --mode RealNasBackup --acceptance-id <GUID> --baseline <new local JSON>
                --test-restore-conflict-engine --mode RealNasBackup --acceptance-id <GUID> --baseline <local JSON>
                  Both RealNasBackup commands also require --provider SmbShare|FnOsPersonal --source <authorized test backup>
                  --authorize-nas --source-quiescent
                  FnOsPersonal also requires --server <https://host[:port]> [--cert-sha256 <SHA-256>]
                --test-dual-account-live [--authorize-nas --server <https://host[:port]> --root-a <sandbox> --root-b <sandbox>
                  --allow-test-writes [--cert-sha256 <SHA-256>]]
                --self-test
                Exit codes: 0 PASS, 1 FAIL, 2 BLOCKED, 3 SKIP. Reports are JSON on stdout.
                Credentials are entered in an interactive console. Never pass passwords on the command line.
                """);
            return 0;
        }

        if (command == "--version" && args.Length == 1)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Version = Build.Split('+')[0], BuildSha = Build.Split('+').ElementAtOrDefault(1) }, Json));
            return 0;
        }

        Console.OutputEncoding = new UTF8Encoding(false);
        string level = command == "--test-dual-account-live" || args.Contains("RealNasBackup") ? "RealNAS" : "Local";
        var results = new List<Result>();
        string? temp = null;
        string? acceptanceId = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        try
        {
            var options = new Options(command, args.Skip(1));
            acceptanceId = options.Get("--acceptance-id");
            // Always isolate Core before it resolves any settings or log locations.
            if (command is "--test-restore-conflict-engine" or "--test-dual-account-live" or "--self-test" or "--snapshot-before")
            {
                temp = Path.Combine(Path.GetTempPath(), $"FNSyncPlus_Acceptance_{Guid.NewGuid():N}");
                AppStorageRoot.InitializeForTests(temp);
                Directory.CreateDirectory(temp);
            }

            switch (command)
            {
                case "--test-restore-conflict-engine":
                    await RestoreAcceptanceAsync(options, false, temp!, results, timeout.Token);
                    break;
                case "--snapshot-before":
                    await RestoreAcceptanceAsync(options, true, null, results, timeout.Token);
                    break;
                case "--test-dual-account-live":
                    await DualAccountAsync(options, results, timeout.Token);
                    break;
                case "--new-fixture":
                case "--update-fixture":
                case "--verify-restored":
                    await FixtureAsync(command, options, results, timeout.Token);
                    break;
                case "--self-test":
                    await SelfTestAsync(temp!, results, timeout.Token);
                    break;
                default:
                    throw new InvalidInput("Unknown command. See --help.");
            }
        }
        catch (Exception error)
        {
            results.Add(Error(command, level, error));
        }
        finally
        {
            if (temp != null && Directory.Exists(temp))
            {
                try { Directory.Delete(temp, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    results.Add(new("TemporaryCleanup", "Local", "FAIL", null, "The isolated test directory could not be removed."));
                }
            }
        }

        if (results.Count == 0) results.Add(new(command, level, "FAIL", null, "No results were produced."));
        int exitCode = ExitCode(results);
        Console.WriteLine(JsonSerializer.Serialize(new { Build, AcceptanceId = acceptanceId, ExitCode = exitCode, Results = results }, ConsoleJson));
        return exitCode;
    }

    private static int ExitCode(List<Result> results) =>
        results.Any(row => row.Status == "FAIL") ? 1 :
        results.Any(row => row.Status == "BLOCKED") ? 2 :
        results.Any(row => row.Status == "PASS") ? 0 : 3;

    private static Result Error(string id, string level, Exception error) => error switch
    {
        Blocked known => new(id, level, "BLOCKED", null, known.Message),
        InvalidInput known => new(id, level, "FAIL", null, known.Message),
        InvalidDataException or JsonException => new(id, level, "FAIL", null, "Invalid or unsupported backup, manifest, or evidence."),
        UnauthorizedAccessException => new(id, level, "BLOCKED", null, "Access was denied."),
        DirectoryNotFoundException or FileNotFoundException => new(id, level, "BLOCKED", null, "The supplied file or directory could not be found."),
        HttpRequestException or WebSocketException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException =>
            new(id, level, "BLOCKED", null, "Connection or TLS validation failed."),
        TimeoutException or OperationCanceledException => new(id, level, "BLOCKED", null, "The operation timed out or was canceled."),
        IOException => new(id, level, "BLOCKED", null, "The path or backup could not be read or written. Check availability and permissions."),
        _ => new(id, level, "FAIL", new { ExceptionType = error.GetType().Name }, "Unexpected error. No credentials or raw exception text are included.")
    };

    private sealed class Options
    {
        private static readonly string[] RemoteFlags = ["--authorize-nas", "--source-quiescent"];
        private static readonly string[] RemoteValues = ["--mode", "--provider", "--source", "--server", "--cert-sha256", "--acceptance-id", "--baseline"];
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        public Options(string command, IEnumerable<string> args)
        {
            var (flags, values) = command switch
            {
                "--snapshot-before" or "--test-restore-conflict-engine" => (RemoteFlags, RemoteValues),
                "--test-dual-account-live" => (new[] { "--authorize-nas", "--allow-test-writes" }, new[] { "--server", "--cert-sha256", "--root-a", "--root-b" }),
                "--new-fixture" or "--update-fixture" => (Array.Empty<string>(), new[] { "--source", "--fixture" }),
                "--verify-restored" => (new[] { "--history" }, new[] { "--target", "--fixture" }),
                _ => (Array.Empty<string>(), Array.Empty<string>())
            };
            using var input = args.GetEnumerator();
            while (input.MoveNext())
            {
                string key = input.Current;
                if (_values.ContainsKey(key)) throw new InvalidInput("An option was repeated.");
                if (flags.Contains(key)) _values.Add(key, null);
                else if (values.Contains(key) && input.MoveNext() && !string.IsNullOrWhiteSpace(input.Current) && !input.Current.StartsWith("--"))
                    _values.Add(key, input.Current);
                else throw new InvalidInput("Unknown option or missing option value. See --help.");
            }
        }

        public bool Has(string name) => _values.ContainsKey(name);
        public int Count => _values.Count;
        public string? Get(string name) => _values.GetValueOrDefault(name);
        public string Require(string name) => Get(name) ?? throw new InvalidInput($"Required option: {name}.");
    }

    private static Uri Server(Options options)
    {
        if (!Uri.TryCreate(options.Require("--server"), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4) || uri.Port == 0)
            throw new InvalidInput("Use an HTTPS NAS address without credentials, path, query, or fragment.");
        var pin = options.Get("--cert-sha256");
        if (pin != null && !IsSha256(pin))
            throw new InvalidInput("The certificate SHA-256 fingerprint must contain exactly 64 hexadecimal digits.");
        return uri;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
