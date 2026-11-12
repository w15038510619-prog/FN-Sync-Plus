// Local isolated HTTPS update server for the pre-production acceptance harness.
//
// This is NOT a company update server and never pretends to be one: it binds to loopback only,
// serves a single content directory over HTTPS with an ephemeral self-signed certificate that is
// generated in-memory at startup and is never installed into any trust store. The client side of
// the acceptance pins this exact thumbprint inside the isolated test environment only
// (FNSYNC_UPDATE_TEST_CERT_PIN + FNSYNC_DATA_ROOT), so production TLS validation stays untouched.
//
// Every request is recorded (method, path, negotiated TLS protocol, status, bytes) as a JSON
// line, giving the acceptance report verifiable TLS evidence instead of assumptions.
//
// Usage:
//   FNSyncPlus.LocalUpdateServer --content <dir> --port <n> --facts <facts.json> [--request-log <path>]

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

static string? Opt(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
    return null;
}

string? content = Opt(args, "--content");
string? factsPath = Opt(args, "--facts");
string portText = Opt(args, "--port") ?? "";
if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(factsPath) ||
    !int.TryParse(portText, out int port) || port <= 0 || port > 65535)
{
    Console.Error.WriteLine("用法: FNSyncPlus.LocalUpdateServer --content <dir> --port <n> --facts <facts.json> [--request-log <path>]");
    return 2;
}
content = Path.GetFullPath(content);
Directory.CreateDirectory(content);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(factsPath))!);
string requestLogPath = Opt(args, "--request-log") ?? (factsPath + ".requests.jsonl");
File.WriteAllText(requestLogPath, "");
var logLock = new object();

// Ephemeral certificate: exists only for this process lifetime, never exported, never trusted
// by the operating system. The SAN covers both loopback name forms so the acceptance can use
// either https://localhost or https://127.0.0.1 with a real hostname check.
using var rsa = RSA.Create(2048);
var request = new CertificateRequest(
    "CN=FN Sync Plus Local Test Update Server (isolated acceptance only)",
    rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var san = new SubjectAlternativeNameBuilder();
san.AddDnsName("localhost");
san.AddIpAddress(IPAddress.Loopback);
san.AddIpAddress(IPAddress.IPv6Loopback);
request.CertificateExtensions.Add(san.Build());
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
request.CertificateExtensions.Add(new X509KeyUsageExtension(
    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
using var rawCert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
byte[] pfxBytes = rawCert.Export(X509ContentType.Pfx);
using var certificate = new X509Certificate2(pfxBytes);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate));
    try
    {
        // "localhost" may resolve to ::1 first; serve both so hostname validation is exercised
        // regardless of resolver order. IPv6 may be unavailable on some machines.
        options.Listen(IPAddress.IPv6Loopback, port, listen => listen.UseHttps(certificate));
    }
    catch (Exception ex)
    {
        Console.WriteLine("IPv6 回环监听不可用（忽略，仅使用 127.0.0.1）：" + ex.Message);
    }
});

var app = builder.Build();

// Request evidence: recorded BEFORE the static-file middleware answers, completed after, so a
// failed TLS handshake never appears (the request never reaches the pipeline) — exactly the
// property the acceptance asserts for the "unpinned certificate must be rejected" case.
app.Use(async (context, next) =>
{
    var tls = context.Features.Get<ITlsHandshakeFeature>();
    await next();
    var entry = new Dictionary<string, object?>
    {
        ["at"] = DateTimeOffset.UtcNow,
        ["method"] = context.Request.Method,
        ["path"] = context.Request.Path.Value,
        ["tls_protocol"] = tls?.Protocol.ToString(),
        ["status"] = context.Response.StatusCode,
        ["content_length"] = context.Response.ContentLength,
    };
    string line = JsonSerializer.Serialize(entry);
    lock (logLock) File.AppendAllText(requestLogPath, line + Environment.NewLine);
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(content),
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

await app.StartAsync();

var facts = new Dictionary<string, object?>
{
    ["pid"] = Environment.ProcessId,
    ["port"] = port,
    ["base_url_localhost"] = $"https://localhost:{port}",
    ["base_url_ipv4"] = $"https://127.0.0.1:{port}",
    ["cert_thumbprint"] = certificate.Thumbprint,
    ["cert_subject"] = certificate.Subject,
    ["cert_not_after_utc"] = certificate.NotAfter.ToUniversalTime(),
    ["cert_signature_algorithm"] = certificate.SignatureAlgorithm.FriendlyName,
    ["content_dir"] = content,
    ["request_log"] = requestLogPath,
    ["started_utc"] = DateTimeOffset.UtcNow,
    ["isolation_note"] = "Ephemeral self-signed certificate for the isolated acceptance environment " +
        "only; never installed into any trust store; company/production update server NOT deployed " +
        "by this harness; production TLS validation untouched.",
};
string factsJson = JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(factsPath + ".tmp", factsJson);
File.Move(factsPath + ".tmp", factsPath, overwrite: true);
Console.WriteLine($"本地隔离更新服务器就绪：https://localhost:{port}（证书指纹 {certificate.Thumbprint}，仅隔离验收环境）");

await app.WaitForShutdownAsync();
return 0;
