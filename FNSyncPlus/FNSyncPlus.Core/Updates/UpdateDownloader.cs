using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace FNSyncPlus.Core.Updates;

public sealed record UpdateDownloadResult(bool Ok, string FilePath, string Error);

/// <summary>
/// Downloads verified update packages with resumable HTTP range requests. Every completed download
/// is re-hashed; a mismatch renames nothing and reports failure. Only HTTPS endpoints are accepted.
/// </summary>
public sealed class UpdateDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public UpdateDownloader(HttpClient? client = null)
    {
        _http = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _ownsClient = client == null;
    }

    public async Task<UpdateDownloadResult> DownloadAsync(string url, string destinationPath, string expectedSha256,
        long expectedSize, IProgress<long>? progress = null, CancellationToken token = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new UpdateDownloadResult(false, destinationPath, "更新包地址必须是 HTTPS");
        if (expectedSize <= 0) return new UpdateDownloadResult(false, destinationPath, "更新包大小无效");
        string partial = destinationPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing > expectedSize)
        {
            File.Delete(partial);
            existing = 0;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                File.Delete(partial);
                return new UpdateDownloadResult(false, destinationPath, "断点续传状态无效，已清理并等待重试");
            }
            if ((int)response.StatusCode >= 400)
                return new UpdateDownloadResult(false, destinationPath, $"下载失败：HTTP {(int)response.StatusCode}");
            bool appending = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            if (!appending) existing = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, appending ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long written = existing;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    written += read;
                    if (written > expectedSize) return new UpdateDownloadResult(false, destinationPath, "下载内容超过清单大小，已拒绝");
                    progress?.Report(written);
                }
            }
            token.ThrowIfCancellationRequested();
            if (new FileInfo(partial).Length != expectedSize)
                return new UpdateDownloadResult(false, destinationPath, "下载大小与清单不一致");
            string actual;
            await using (var stream = File.OpenRead(partial))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                return new UpdateDownloadResult(false, destinationPath, "更新包 SHA-256 与清单不一致，已删除");
            }
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(partial, destinationPath);
            return new UpdateDownloadResult(true, destinationPath, "");
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex) { return new UpdateDownloadResult(false, destinationPath, $"下载网络错误：{ex.Message}"); }
        catch (IOException ex) { return new UpdateDownloadResult(false, destinationPath, $"下载写入错误：{ex.Message}"); }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
