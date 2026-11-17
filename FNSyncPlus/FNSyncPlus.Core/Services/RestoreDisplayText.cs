using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FNSyncPlus.Core.Services;

public static partial class RestoreDisplayText
{
    // Some old entries contain a JSON Unicode escape inside the already-decoded path.
    // A lone "folder\u7248.txt" can also be an ordinary Windows path. Leave it alone.
    [GeneratedRegex(@"(?:\\u[0-9a-fA-F]{4}){2,}|(?<![^/])\\u[0-9a-fA-F]{4}", RegexOptions.CultureInvariant)]
    private static partial Regex EscapedUnicode();

    /// <summary>Formats a path for display. The manifest entry remains the restore identity.</summary>
    public static string OriginalPath(string path) => EscapedUnicode().Replace(path, static match =>
    {
        try
        {
            string decoded = JsonSerializer.Deserialize<string>($"\"{match.Value}\"")!;
            return decoded.EnumerateRunes().All(rune =>
                rune.Value >= 0xA0 && Rune.GetUnicodeCategory(rune) is not
                    (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format))
                ? decoded : match.Value;
        }
        catch (JsonException) { return match.Value; }
    });

    public static Task WriteReportAsync(string path, IEnumerable<string> lines) =>
        File.WriteAllLinesAsync(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
}
