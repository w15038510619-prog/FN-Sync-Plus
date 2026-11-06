using System;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Models
{
    /// <summary>
    /// 结构化任务与诊断日志实体
    /// </summary>
    public class TaskLogEntry
    {
        [JsonPropertyName("time")]
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

        [JsonPropertyName("level")]
        public string Level { get; set; } = "INFO"; // INFO, WARN, ERROR, DEBUG

        [JsonPropertyName("task_id")]
        public string TaskId { get; set; } = string.Empty;

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = "GENERAL"; // SCAN, PLAN, UPLOAD, MANIFEST, VERIFY, RESTORE, CLEANUP

        [JsonPropertyName("msg")]
        public string Message { get; set; } = string.Empty;

        // 详细结构化排障字段
        [JsonPropertyName("local_rel")]
        public string? LocalRelPath { get; set; }

        [JsonPropertyName("remote_rel")]
        public string? RemoteRelPath { get; set; }

        [JsonPropertyName("utf8_bytes")]
        public int? Utf8Bytes { get; set; }

        [JsonPropertyName("nas_mapped_name")]
        public string? NasMappedName { get; set; }

        [JsonPropertyName("rpc_name")]
        public string? RpcName { get; set; }

        [JsonPropertyName("err_code")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("err_msg")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("retry_count")]
        public int RetryCount { get; set; } = 0;

        [JsonPropertyName("upload_result")]
        public string? UploadResult { get; set; }

        [JsonPropertyName("manifest_result")]
        public string? ManifestResult { get; set; }

        [JsonPropertyName("verify_result")]
        public string? VerificationResult { get; set; }

        [JsonIgnore]
        public string FormattedTimestamp => Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");

        [JsonIgnore]
        public string FormattedDisplay
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"[{FormattedTimestamp}] [{Level,-5}] [{Stage}] {Message}");
                if (!string.IsNullOrEmpty(LocalRelPath)) sb.Append($" | 源文件: {LocalRelPath}");
                if (!string.IsNullOrEmpty(NasMappedName)) sb.Append($" | NAS映射: {NasMappedName}");
                if (Utf8Bytes.HasValue) sb.Append($" ({Utf8Bytes} B)");
                if (!string.IsNullOrEmpty(RpcName)) sb.Append($" | 接口: {RpcName}");
                if (!string.IsNullOrEmpty(ErrorCode) || !string.IsNullOrEmpty(ErrorMessage))
                {
                    sb.Append($" | 异常: [code: {ErrorCode}] {ErrorMessage}");
                }
                if (RetryCount > 0) sb.Append($" | 重试: {RetryCount}次");
                if (!string.IsNullOrEmpty(VerificationResult)) sb.Append($" | 校验: {VerificationResult}");
                return sb.ToString();
            }
        }

        public override string ToString() => FormattedDisplay;
    }
}
