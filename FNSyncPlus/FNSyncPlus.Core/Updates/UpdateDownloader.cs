using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace FNSyncPlus.Core.Updates;

public sealed record UpdateDownloadResult(bool Ok, string FilePath, string Error, bool Reused = false);

/// <summary>
/// Streams complete HTTPS packages and validates size and SHA-256 before making them available.
/// </summary>
public sealed class UpdateDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public UpdateDownloader(HttpClient? client = null)
    {
        _http = client ?? UpdateService.CreateHttpClient(new UpdateSettings());
        _ownsClient = client == null;
    }

    public async Task<UpdateDownloadResult> DownloadAsync(string url, string destinationPath, string expectedSha256,
        long expectedSize, IProgress<long>? progress = null, CancellationToken token = default, TimeSpan? timeout = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new UpdateDownloadResult(false, destinationPath, "更新包地址必须是 HTTPS");
        if (expectedSize <= 0) return new UpdateDownloadResult(false, destinationPath, "更新包大小无效");
        string partial = destinationPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(120));
        var downloadToken = deadline.Token;
        try
        {
            if (await MatchesAsync(destinationPath, expectedSize, expectedSha256, downloadToken).ConfigureAwait(false))
                return new UpdateDownloadResult(true, destinationPath, "", Reused: true);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            // Earlier builds wrote a shared partial file without binding it to a signed release.
            File.Delete(destinationPath + ".partial");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, downloadToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri?.Scheme is { } scheme && scheme != "https")
                return new UpdateDownloadResult(false, destinationPath, $"服务器未返回完整的 HTTPS 更新包（HTTP {(int)response.StatusCode}）。");
            if (response.Content.Headers.ContentLength is long length && length > expectedSize)
                return new UpdateDownloadResult(false, destinationPath, "下载内容超过清单大小，已拒绝");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var updateClock = System.Diagnostics.Stopwatch.StartNew();
            long written = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, downloadToken).ConfigureAwait(false)) > 0)
                {
                    if (read > expectedSize - written) return new UpdateDownloadResult(false, destinationPath, "下载内容超过清单大小，已拒绝");
                    await output.WriteAsync(buffer.AsMemory(0, read), downloadToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    written += read;
                    if (progress != null && (written == expectedSize || updateClock.ElapsedMilliseconds >= 200))
                    {
                        progress.Report(written);
                        updateClock.Restart();
                    }
                }
            }
            downloadToken.ThrowIfCancellationRequested();
            if (written != expectedSize || new FileInfo(partial).Length != expectedSize)
                return new UpdateDownloadResult(false, destinationPath, "下载大小与清单不一致");
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedSha256, StringComparison.OrdinalIgnoreCase))
                return new UpdateDownloadResult(false, destinationPath, "更新包 SHA-256 与清单不一致，已删除");
            File.Move(partial, destinationPath, overwrite: true);
            return new UpdateDownloadResult(true, destinationPath, "");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new UpdateDownloadResult(false, destinationPath, "下载更新包超时，请检查网络或代理设置后重试。"); }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return new UpdateDownloadResult(false, destinationPath, "无法下载更新包，请检查网络、代理和证书后重试。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new UpdateDownloadResult(false, destinationPath, "无法保存更新包，请检查安装目录的写入权限和磁盘空间。"); }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static async Task<bool> MatchesAsync(string path, long size, string sha256, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return stream.Length == size && string.Equals(
            Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)),
            sha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
