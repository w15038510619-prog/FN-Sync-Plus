using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FNSyncPlus.Core.Services
{
    /// <summary>
    /// 扫描发现无法唯一确认的改名差异：同一内容对应多个旧名或多个新名。
    /// 程序停止该次备份、保留上一份提交；待用户检查后自行整理文件再同步。
    /// 也可以由用户在界面上显式确认配对，但确认会在提交前重新校验来源与目标内容。
    /// </summary>
    public class AmbiguousRenameException : IOException
    {
        /// <summary>One old backup file whose content reappears under several or zero new names.</summary>
        public sealed record Group(string OldPath, long Size, string Sha256, IReadOnlyList<string> CandidatePaths)
        {
            /// <summary>Human-readable reason this group cannot be paired automatically.</summary>
            public string Reason { get; init; } = "";
        }

        /// <summary>A new file that appeared in the source tree without a matching old file.</summary>
        public sealed record Addition(string Path, long Size, string Sha256);

        /// <summary>A file that disappeared from the source tree without a confirmed successor.</summary>
        public sealed record Removal(string Path, long Size, string Sha256);

        public IReadOnlyList<Group> AmbiguousGroups { get; }

        public IReadOnlyList<string> UnmatchedAdditions { get; }

        /// <summary>New files with size/hash, used by the confirmation UI and re-verification.</summary>
        public IReadOnlyList<Addition> UnmatchedAdditionDetails { get; }

        /// <summary>Old files that vanished without a confirmed successor.</summary>
        public IReadOnlyList<Removal> UnmatchedRemovalDetails { get; }

        /// <summary>Whether the previous committed backup was left untouched by the stopped run.</summary>
        public bool PreviousBackupPreserved { get; }

        /// <summary>Current committed manifest id at the time the run stopped, if known.</summary>
        public string? PreviousManifestId { get; }

        public AmbiguousRenameException(string message, IReadOnlyList<Group> ambiguousGroups, IReadOnlyList<string> unmatchedAdditions,
            IReadOnlyList<Addition>? unmatchedAdditionDetails = null, IReadOnlyList<Removal>? unmatchedRemovalDetails = null,
            bool previousBackupPreserved = true, string? previousManifestId = null)
            : base(message)
        {
            AmbiguousGroups = ambiguousGroups;
            UnmatchedAdditions = unmatchedAdditions;
            UnmatchedAdditionDetails = unmatchedAdditionDetails ?? unmatchedAdditions
                .Select(path => new Addition(path, 0, "")).ToList();
            UnmatchedRemovalDetails = unmatchedRemovalDetails ?? Array.Empty<Removal>();
            PreviousBackupPreserved = previousBackupPreserved;
            PreviousManifestId = previousManifestId;
        }

        public string BuildUserSummary()
        {
            var lines = new List<string>
            {
                "本次备份已停止，上一份备份未改变。以下文件无法唯一确认是否为改名，请检查源目录："
            };
            int index = 1;
            foreach (var group in AmbiguousGroups)
            {
                lines.Add($"{index++}. 原备份文件「{group.OldPath}」（{group.Size} 字节，SHA-256 {ShortHash(group.Sha256)}）内容相同的新文件有 {group.CandidatePaths.Count} 个：");
                foreach (var candidate in group.CandidatePaths) lines.Add($"   - {candidate}");
                if (!string.IsNullOrEmpty(group.Reason)) lines.Add($"   停止原因：{group.Reason}");
            }
            if (UnmatchedRemovalDetails.Count > 0)
            {
                lines.Add("以下旧备份文件在源目录中消失，但没有可确认的新文件接手：");
                foreach (var removal in UnmatchedRemovalDetails)
                    lines.Add($"   - {removal.Path}（{removal.Size} 字节，SHA-256 {ShortHash(removal.Sha256)}）");
            }
            if (UnmatchedAdditions.Count > 0)
            {
                lines.Add("以下新增文件与消失文件无法配对，可能是改名也可能不是：");
                foreach (var addition in UnmatchedAdditionDetails)
                    lines.Add(addition.Sha256.Length == 64
                        ? $"   - {addition.Path}（{addition.Size} 字节，SHA-256 {ShortHash(addition.Sha256)}）"
                        : $"   - {addition.Path}");
            }
            lines.Add("请改为不同的文件名或删除多余副本后重新同步；程序不会自行猜测配对，也不会因此删除或覆盖任何已备份内容。");
            if (PreviousBackupPreserved)
                lines.Add("上一份备份的可恢复性不受影响；本次没有提交任何新的当前版本。");
            return string.Join(Environment.NewLine, lines);
        }

        private static string ShortHash(string hash) =>
            hash is { Length: 64 } ? hash[..12] + "…" : hash;
    }

    /// <summary>
    /// Explicit, re-verified manual pairing for an ambiguous rename stop. A confirmation is only
    /// accepted when, at execution time, the old backup object still matches its recorded SHA-256
    /// and the source file still matches the same SHA-256 and size. Stale confirmations are refused.
    /// </summary>
    public sealed class AmbiguousRenameResolution
    {
        public sealed record Pair(string OldPath, string NewPath);

        public sealed record Problem(string Pair, string Reason);

        private readonly Dictionary<string, string> _pairs;

        public AmbiguousRenameResolution(IEnumerable<Pair> pairs)
        {
            ArgumentNullException.ThrowIfNull(pairs);
            _pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
            {
                if (pair == null || string.IsNullOrWhiteSpace(pair.OldPath) || string.IsNullOrWhiteSpace(pair.NewPath))
                    throw new ArgumentException("确认配对必须同时包含旧路径和新路径");
                if (!_pairs.TryAdd(pair.OldPath, pair.NewPath))
                    throw new ArgumentException($"同一旧文件被确认了多个新路径: {pair.OldPath}");
                if (_pairs.Values.Count(v => string.Equals(v, pair.NewPath, StringComparison.OrdinalIgnoreCase)) > 1)
                    throw new ArgumentException($"多个旧文件被确认到同一新路径: {pair.NewPath}");
            }
        }

        public IReadOnlyDictionary<string, string> Pairs => _pairs;

        /// <summary>
        /// Checks a resolution against the exception and the CURRENT source tree. Every new file is
        /// re-hashed; the caller must additionally verify that the old object is unchanged at submit
        /// time (the backup run re-verifies every previous object before writing).
        /// </summary>
        public static IReadOnlyList<Problem> Validate(AmbiguousRenameException stopped, AmbiguousRenameResolution resolution,
            string sourceDirectory, Func<string, string>? hashSource = null)
        {
            ArgumentNullException.ThrowIfNull(stopped);
            ArgumentNullException.ThrowIfNull(resolution);
            var problems = new List<Problem>();
            var additions = stopped.UnmatchedAdditionDetails.ToDictionary(a => a.Path, a => a, StringComparer.OrdinalIgnoreCase);
            var removals = stopped.UnmatchedRemovalDetails.ToDictionary(r => r.Path, r => r, StringComparer.OrdinalIgnoreCase);
            foreach (var group in stopped.AmbiguousGroups)
                foreach (var candidate in group.CandidatePaths)
                    if (!additions.ContainsKey(candidate))
                        additions[candidate] = new AmbiguousRenameException.Addition(candidate, group.Size, group.Sha256);
            hashSource ??= path => FNSyncPlus.Core.Utils.HashHelper.ComputeFileSha256(
                Path.Combine(sourceDirectory, path.Replace('/', Path.DirectorySeparatorChar)));

            var usedNew = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in resolution.Pairs)
            {
                string label = pair.Key + " → " + pair.Value;
                if (usedNew.Contains(pair.Value))
                { problems.Add(new Problem(label, "同一个新文件被多个旧文件使用")); continue; }
                usedNew.Add(pair.Value);
                if (!additions.TryGetValue(pair.Value, out var addition))
                { problems.Add(new Problem(label, "该新文件不在本次停止的差异列表中")); continue; }
                string expectedHash;
                if (removals.TryGetValue(pair.Key, out var removal)) expectedHash = removal.Sha256;
                else
                {
                    var group = stopped.AmbiguousGroups.FirstOrDefault(g =>
                        string.Equals(g.OldPath, pair.Key, StringComparison.OrdinalIgnoreCase) ||
                        g.CandidatePaths.Contains(pair.Value, StringComparer.OrdinalIgnoreCase));
                    if (group == null) { problems.Add(new Problem(label, "旧文件不在本次停止的差异列表中")); continue; }
                    expectedHash = group.Sha256;
                    if (!string.Equals(addition.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase) && addition.Sha256.Length == 64)
                    { problems.Add(new Problem(label, "新文件内容与停止时记录不一致")); continue; }
                }
                string actual;
                try { actual = hashSource(pair.Value); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { problems.Add(new Problem(label, $"无法重新读取新文件：{ex.Message}")); continue; }
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    problems.Add(new Problem(label, "新文件内容已变化，必须重新扫描"));
            }
            return problems;
        }
    }
}
