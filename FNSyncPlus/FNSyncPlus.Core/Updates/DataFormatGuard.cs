using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace FNSyncPlus.Core.Updates;

/// <summary>What the on-disk format marker actually looks like.</summary>
public enum FormatMarkerState
{
    /// <summary>No marker at all: this really is a first run.</summary>
    Missing,
    /// <summary>Parsed and structurally valid.</summary>
    Valid,
    /// <summary>Exists but cannot be parsed (truncated, empty, wrong shape).</summary>
    Corrupt,
    /// <summary>Exists but cannot be read (permissions, sharing violation, I/O error).</summary>
    Unreadable,
    /// <summary>Parsed but carries a value this build cannot interpret.</summary>
    Unsupported
}

/// <summary>Explicit lifecycle states during format marker atomic update transaction.</summary>
public enum WriteTransactionState
{
    Prepared,
    BackupVerified,
    Replacing,
    Verifying,
    Committed,
    RollbackRequired,
    RolledBack,
    RecoveryFailed
}

public sealed record FormatMarkerInspection(FormatMarkerState State, int Format, string Detail)
{
    /// <summary>Only a genuinely absent marker may be created from scratch.</summary>
    public bool AllowsInitialization => State == FormatMarkerState.Missing;

    /// <summary>True when this build must stop instead of writing.</summary>
    public bool Blocks(int supportedFormat) => State switch
    {
        FormatMarkerState.Missing => false,
        FormatMarkerState.Valid => Format > supportedFormat,
        _ => true
    };
}

/// <summary>
/// Guards the boundary between program versions and the on-disk configuration/data format.
///
/// Implements strict Fail-Closed transactional guarantees:
///  * If an existing marker is present, its validity is verified and an identical, confirmed
///    backup copy (.bak) is created before any replacement can occur.
///  * Writes use a temporary file, flushed to disk, and replaced atomically.
///  * After replacement, the marker is read back and verified.
///  * On verification or replacement failure, the original valid marker is restored.
///  * If rollback fails (RecoveryFailed), the .bak file is NEVER deleted and is preserved for manual recovery.
/// </summary>
public sealed class DataFormatGuard
{
    private const int LockRetries = 25;
    private const int LockRetryDelayMs = 80;

    private sealed class Record
    {
        [JsonPropertyName("format")] public int Format { get; set; }
        [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
        [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    }

    private readonly string _path;
    private readonly int _supportedFormat;
    private readonly Action<string>? _log;

    public DataFormatGuard(string markerPath, int supportedFormat, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(markerPath)) throw new ArgumentException("格式标记路径不能为空", nameof(markerPath));
        if (supportedFormat < 1) throw new ArgumentOutOfRangeException(nameof(supportedFormat), "支持的格式版本必须为正数");
        _path = markerPath;
        _supportedFormat = supportedFormat;
        _log = log;
    }

    public string MarkerPath => _path;

    /// <summary>Path of the preserved copy taken when a marker was found corrupt, if any.</summary>
    public string? LastPreservedCopy { get; private set; }

    /// <summary>Path of the preserved backup copy when rollback fails (RecoveryFailed).</summary>
    public string? LastPreservedBackup { get; private set; }

    /// <summary>The final or current lifecycle state of the write transaction.</summary>
    public WriteTransactionState LastTransactionState { get; private set; } = WriteTransactionState.Prepared;

    public int SupportedFormat => _supportedFormat;

    /// <summary>Fault-injection test hooks to verify fail-closed boundaries.</summary>
    internal Func<string, bool>? FaultInjectBackupCreation { get; set; }
    internal Func<string, bool>? FaultInjectReplaceOperation { get; set; }
    internal Func<string, bool>? FaultInjectPostWriteVerifier { get; set; }
    internal Func<string, bool>? FaultInjectRollbackFailure { get; set; }

    /// <summary>Read-only inspection. Never writes, never repairs, never deletes.</summary>
    public FormatMarkerInspection Probe()
    {
        string? text;
        try
        {
            if (!File.Exists(_path)) return new FormatMarkerInspection(FormatMarkerState.Missing, 0, "尚未创建格式标记");
            if (new FileInfo(_path).Length == 0)
                return new FormatMarkerInspection(FormatMarkerState.Corrupt, 0, "格式标记文件为空（可能写入中断）");
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new FormatMarkerInspection(FormatMarkerState.Unreadable, 0, $"无法读取格式标记：{ex.Message}");
        }

        Record? record;
        try { record = JsonSerializer.Deserialize<Record>(text); }
        catch (JsonException ex)
        {
            return new FormatMarkerInspection(FormatMarkerState.Corrupt, 0, $"格式标记不是有效 JSON：{ex.Message}");
        }
        if (record == null)
            return new FormatMarkerInspection(FormatMarkerState.Corrupt, 0, "格式标记内容为空");
        if (record.Format < 1)
            return new FormatMarkerInspection(FormatMarkerState.Unsupported, record.Format,
                $"格式标记中的格式号 {record.Format} 无法识别");
        return new FormatMarkerInspection(FormatMarkerState.Valid, record.Format, $"数据格式 {record.Format}");
    }

    /// <summary>
    /// Gate for "may this build touch local data?". Returns false with an explanation whenever the
    /// on-disk state cannot be established with certainty.
    /// </summary>
    public bool TryClaim(string appVersion, out string error)
    {
        error = "";
        if (!TryAcquireLock(out FileStream? lockFile, out error)) return false;
        using (lockFile)
        {
            var probe = Probe();
            switch (probe.State)
            {
                case FormatMarkerState.Missing:
                    // Genuine first run: create the marker.
                    return TryWrite(appVersion, _supportedFormat, out error);

                case FormatMarkerState.Valid:
                    if (probe.Format > _supportedFormat)
                    {
                        error = $"本机配置由更高版本（数据格式 {probe.Format}）写入，当前程序只支持格式 {_supportedFormat}，" +
                                "已停止运行以避免写坏配置；请安装最新版本。";
                        _log?.Invoke(error);
                        return false;
                    }
                    return TryWrite(appVersion, probe.Format, out error);

                case FormatMarkerState.Corrupt:
                case FormatMarkerState.Unsupported:
                    PreserveForDiagnosis(probe.State);
                    error = $"配置格式标记已损坏（{probe.Detail}），为防止写坏数据已停止运行。" +
                            $"损坏文件已保留：{LastPreservedCopy ?? _path}。请人工确认后处理，程序不会自动删除。";
                    _log?.Invoke(error);
                    return false;

                default:
                    error = $"无法读取配置格式标记（{probe.Detail}），已停止运行以避免写坏数据。";
                    _log?.Invoke(error);
                    return false;
            }
        }
    }

    /// <summary>
    /// Raises the marker to a newer format. Only called by a real migration that has already written
    /// data in the new format; never used to paper over an unreadable marker.
    /// </summary>
    public bool TryUpgrade(string appVersion, int newFormat, out string error)
    {
        error = "";
        if (newFormat < 1 || newFormat > _supportedFormat)
        {
            error = $"不能把数据格式升级到 {newFormat}（当前程序最高支持 {_supportedFormat}）";
            return false;
        }
        if (!TryAcquireLock(out FileStream? lockFile, out error)) return false;
        using (lockFile)
        {
            var probe = Probe();
            if (probe.State is FormatMarkerState.Corrupt or FormatMarkerState.Unreadable or FormatMarkerState.Unsupported)
            {
                error = $"升级格式标记前无法确认当前状态（{probe.Detail}），已停止以避免覆盖诊断信息。";
                PreserveForDiagnosis(probe.State);
                return false;
            }
            if (probe.State == FormatMarkerState.Valid && probe.Format > newFormat)
            {
                error = $"当前格式 {probe.Format} 已高于目标格式 {newFormat}，拒绝降级标记。";
                return false;
            }
            return TryWrite(appVersion, newFormat, out error);
        }
    }

    private bool TryAcquireLock(out FileStream? handle, out string error)
    {
        error = "";
        string lockPath = _path + ".lock";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            handle = null;
            error = $"无法创建配置目录：{ex.Message}";
            return false;
        }

        for (int attempt = 0; attempt < LockRetries; attempt++)
        {
            try
            {
                handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(LockRetryDelayMs);
            }
            catch (UnauthorizedAccessException ex)
            {
                handle = null;
                error = $"没有写入配置目录的权限：{ex.Message}";
                return false;
            }
        }
        handle = null;
        error = "另一个程序正在写入配置格式标记，为避免并发写入已停止本次操作。";
        return false;
    }

    /// <summary>
    /// Executes the safe transaction:
    /// Prepared → BackupVerified → Replacing → Verifying → Committed
    /// If verification or replacement fails:
    /// Replacing/Verifying → RollbackRequired → RolledBack
    /// If rollback fails:
    /// RollbackRequired → RecoveryFailed (the .bak is strictly preserved and never deleted).
    /// </summary>
    private bool TryWrite(string appVersion, int format, out string error)
    {
        error = "";
        LastTransactionState = WriteTransactionState.Prepared;
        string temp = $"{_path}.tmp-{Guid.NewGuid():N}";
        string backup = $"{_path}.bak-{Guid.NewGuid():N}";
        bool backupCreated = false;
        bool exists = File.Exists(_path);

        try
        {
            // 1. Prepared 阶段：若原标记存在，先核实有效性并建立完整一致的事务备份
            if (exists)
            {
                var initialProbe = Probe();
                if (initialProbe.State != FormatMarkerState.Valid)
                {
                    error = $"写入前原配置格式标记处于异常状态（{initialProbe.Detail}），已停止以避免破坏现有数据。";
                    PreserveForDiagnosis(initialProbe.State);
                    return false;
                }

                byte[] originalBytes;
                try
                {
                    originalBytes = File.ReadAllBytes(_path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = $"读取原格式标记失败（{ex.Message}），无法安全创建备份。";
                    return false;
                }

                // 创建备份
                try
                {
                    if (FaultInjectBackupCreation != null && !FaultInjectBackupCreation(_path))
                        throw new IOException("故障注入：备份创建失败");

                    File.Copy(_path, backup, overwrite: false);
                    backupCreated = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = $"无法创建配置格式标记事务备份（{ex.Message}），为防止原数据丢失已终止操作。";
                    return false;
                }

                // 强校验备份副本：可读性与字节一致性
                try
                {
                    if (!File.Exists(backup))
                    {
                        error = "事务备份创建后文件不存在，已安全终止。";
                        return false;
                    }
                    byte[] backupBytes = File.ReadAllBytes(backup);
                    if (!originalBytes.AsSpan().SequenceEqual(backupBytes))
                    {
                        error = "事务备份文件内容与原标记不一致，为防止数据损坏已终止。";
                        try { File.Delete(backup); } catch { }
                        backupCreated = false;
                        return false;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = $"校验事务备份时发生异常（{ex.Message}），已终止操作。";
                    return false;
                }

                LastTransactionState = WriteTransactionState.BackupVerified;
            }

            // 写入临时文件
            string json = JsonSerializer.Serialize(new Record
            {
                Format = format, AppVersion = appVersion, UpdatedAt = DateTimeOffset.UtcNow
            });
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream);
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // 2. Replacing 阶段
            LastTransactionState = WriteTransactionState.Replacing;
            bool replaced = false;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (FaultInjectReplaceOperation != null && !FaultInjectReplaceOperation(_path))
                        throw new IOException("故障注入：替换操作被模拟打断");

                    if (exists)
                    {
                        try { File.Replace(temp, _path, null, ignoreMetadataErrors: true); }
                        catch (IOException) { File.Move(temp, _path, overwrite: true); }
                    }
                    else
                    {
                        File.Move(temp, _path);
                    }
                    replaced = true;
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }

            if (!replaced)
            {
                LastTransactionState = WriteTransactionState.RollbackRequired;
                ExecuteRollback(backupCreated, backup, "替换格式标记文件失败（可能被其他程序独占占用）", out error);
                return false;
            }

            // 3. Verifying 阶段
            LastTransactionState = WriteTransactionState.Verifying;
            var probe = Probe();
            bool verified = probe.State == FormatMarkerState.Valid && probe.Format == format;
            if (FaultInjectPostWriteVerifier != null && !FaultInjectPostWriteVerifier(_path))
            {
                verified = false;
            }

            if (!verified)
            {
                LastTransactionState = WriteTransactionState.RollbackRequired;
                string reason = probe.State != FormatMarkerState.Valid ? probe.Detail : "格式号不匹配或完整性校验未通过";
                ExecuteRollback(backupCreated, backup, $"写入格式标记后校验失败（{reason}）", out error);
                return false;
            }

            // 4. Committed 阶段：最终状态确认有效，方可提交并清理事务文件
            LastTransactionState = WriteTransactionState.Committed;
            if (backupCreated)
            {
                try { File.Delete(backup); } catch { }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastTransactionState = WriteTransactionState.RollbackRequired;
            ExecuteRollback(backupCreated, backup, $"写入配置格式标记异常：{ex.Message}", out error);
            return false;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { }

            // 绝不在 RecoveryFailed 或未确认状态下删除 backup！
            if (LastTransactionState == WriteTransactionState.Committed || LastTransactionState == WriteTransactionState.RolledBack)
            {
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            }
            else if (backupCreated && File.Exists(backup))
            {
                // 必须严格保留最后的有效备份
                LastPreservedBackup = backup;
                _log?.Invoke($"安全防御：因事务未成功提交，原有效标记备份已保留：{backup}");
            }
        }
    }

    private void ExecuteRollback(bool backupCreated, string backup, string triggerReason, out string error)
    {
        if (backupCreated && File.Exists(backup))
        {
            try
            {
                if (FaultInjectRollbackFailure != null && !FaultInjectRollbackFailure(backup))
                    throw new IOException("故障注入：回滚恢复操作异常");

                File.Copy(backup, _path, overwrite: true);
                var probeAfter = Probe();
                if (probeAfter.State == FormatMarkerState.Valid)
                {
                    LastTransactionState = WriteTransactionState.RolledBack;
                    error = $"{triggerReason}；已安全自动回滚恢复原格式标记并停止运行。";
                    _log?.Invoke(error);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"回滚恢复原有效标记时发生异常：{ex.Message}");
            }

            // 回滚未成功 -> 进入 RecoveryFailed，绝不删除 backup！
            LastTransactionState = WriteTransactionState.RecoveryFailed;
            LastPreservedBackup = backup;
            error = $"{triggerReason}；尝试回滚原有效标记失败（状态：RecoveryFailed）。原有效备份已严格保留：{backup}，程序已安全停止，请人工确认处理。";
            _log?.Invoke(error);
        }
        else
        {
            // 首次运行无备份，清理可能生成的半成品
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            LastTransactionState = WriteTransactionState.RolledBack;
            error = $"{triggerReason}；未生成有效标记，已清理临时残留并安全退出。";
        }
    }

    /// <summary>Copies a damaged marker aside. The original is never deleted or rewritten here.</summary>
    private void PreserveForDiagnosis(FormatMarkerState state)
    {
        if (LastPreservedCopy != null) return;
        if (state == FormatMarkerState.Unreadable) return;
        try
        {
            string copy = $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_path, copy, overwrite: false);
            LastPreservedCopy = copy;
            _log?.Invoke($"已保留损坏的格式标记副本：{copy}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastPreservedCopy = null;
        }
    }
}
