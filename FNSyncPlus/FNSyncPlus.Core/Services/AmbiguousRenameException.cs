using System;
using System.Collections.Generic;
using System.IO;

namespace FNSyncPlus.Core.Services
{
    /// <summary>
    /// 扫描发现无法唯一确认的改名差异：同一内容对应多个旧名或多个新名。
    /// 程序停止该次备份、保留上一份提交；待用户检查后自行整理文件再同步。
    /// </summary>
    public class AmbiguousRenameException : IOException
    {
        public sealed record Group(string OldPath, long Size, string Sha256, IReadOnlyList<string> CandidatePaths);

        public IReadOnlyList<Group> AmbiguousGroups { get; }

        public IReadOnlyList<string> UnmatchedAdditions { get; }

        public AmbiguousRenameException(string message, IReadOnlyList<Group> ambiguousGroups, IReadOnlyList<string> unmatchedAdditions)
            : base(message)
        {
            AmbiguousGroups = ambiguousGroups;
            UnmatchedAdditions = unmatchedAdditions;
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
                lines.Add($"{index++}. 原备份文件「{group.OldPath}」（{group.Size} 字节）内容相同的新文件有 {group.CandidatePaths.Count} 个：");
                foreach (var candidate in group.CandidatePaths) lines.Add($"   - {candidate}");
            }
            if (UnmatchedAdditions.Count > 0)
            {
                lines.Add("此外，以下新增文件与消失文件无法配对，可能是改名也可能不是：");
                foreach (var addition in UnmatchedAdditions) lines.Add($"   - {addition}");
            }
            lines.Add("请改为不同的文件名或删除多余副本后重新同步；程序不会自行猜测配对，也不会因此删除或覆盖任何已备份内容。");
            return string.Join(Environment.NewLine, lines);
        }
    }
}
