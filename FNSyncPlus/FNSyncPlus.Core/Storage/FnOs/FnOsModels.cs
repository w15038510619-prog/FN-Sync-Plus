using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Storage.FnOs
{
    public class FnOsHandshakeResponse
    {
        [JsonPropertyName("pub")]
        public string? PublicKeyPem { get; set; }

        [JsonPropertyName("si")]
        public string? SessionId { get; set; }

        [JsonPropertyName("reqid")]
        public string? ReqId { get; set; }
    }

    public class FnOsEncryptedAuthEnvelope
    {
        [JsonPropertyName("req")]
        public string Req { get; set; } = "encrypted";

        [JsonPropertyName("iv")]
        public string IvBase64 { get; set; } = string.Empty;

        [JsonPropertyName("rsa")]
        public string RsaEncryptedKeyBase64 { get; set; } = string.Empty;

        [JsonPropertyName("aes")]
        public string AesEncryptedPayloadBase64 { get; set; } = string.Empty;
    }

    public class FnOsLoginResponse
    {
        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("longToken")]
        public string? LongToken { get; set; }

        [JsonPropertyName("secret")]
        public string? EncryptedSecretBase64 { get; set; }

        [JsonPropertyName("reqid")]
        public string? ReqId { get; set; }

        [JsonPropertyName("isBindTwofaSecret")]
        public bool? IsBindTwofaSecret { get; set; }

        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("errno")]
        public int? ErrNo { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrMsg { get; set; }
    }

    public class FnOsFileEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dir")]
        public int? Dir { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("mtim")]
        public long? ModifiedTimeSeconds { get; set; }

        [JsonPropertyName("btim")]
        public long? CreatedTimeSeconds { get; set; }

        [JsonPropertyName("uid")]
        public int? Uid { get; set; }

        [JsonPropertyName("v")]
        public int? VolumeId { get; set; }

        public bool IsDirectory => Dir == 1;

        public DateTime LastModifiedUtc => ModifiedTimeSeconds.HasValue && ModifiedTimeSeconds.Value > 0
            ? DateTimeOffset.FromUnixTimeSeconds(ModifiedTimeSeconds.Value).UtcDateTime
            : DateTime.UtcNow;
    }

    public class FnOsFileListResponse
    {
        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("files")]
        public List<FnOsFileEntry>? Files { get; set; }

        [JsonPropertyName("uver")]
        public long? UserVersion { get; set; }

        [JsonPropertyName("reqid")]
        public string? ReqId { get; set; }

        [JsonPropertyName("errno")]
        public int? ErrNo { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrMsg { get; set; }
    }

    public class FnOsCheckUploadResponse
    {
        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("from")]
        public long? FromOffset { get; set; }

        [JsonPropertyName("errno")]
        public int? ErrNo { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrMsg { get; set; }
    }

    public class FnOsMultipleDownloadRequest
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("paths")]
        public List<string> Paths { get; set; } = new();

        [JsonPropertyName("downloadLimit")]
        public int DownloadLimit { get; set; } = 2;

        [JsonPropertyName("noZipWhenSingleFile")]
        public bool NoZipWhenSingleFile { get; set; } = true;

        [JsonPropertyName("zipFileName")]
        public string ZipFileName { get; set; } = string.Empty;
    }

    public class FnOsMultipleDownloadResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("errno")]
        public int? ErrNo { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrMsg { get; set; }
    }

    public class FnOsGenericRpcResponse
    {
        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("reqid")]
        public string? ReqId { get; set; }

        [JsonPropertyName("errno")]
        public int? ErrNo { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrMsg { get; set; }
    }
}
