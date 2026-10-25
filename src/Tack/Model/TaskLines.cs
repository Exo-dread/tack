using System.Text.RegularExpressions;

namespace Tack.Model;

/// <summary>Several lines of text as tasks, one a line, the way a pasted list (or dropped text) becomes them.</summary>
internal static partial class TaskLines
{
    /// <summary>Each non-empty line, trimmed, with a bullet, number or checkbox in front taken off (twice, for "1. [ ] …").</summary>
    public static List<string> Split(string text) =>
        text.Replace("\r", "").Split('\n')
            .Select(l => ListMarker().Replace(ListMarker().Replace(l, ""), "").Trim())
            .Where(l => l.Length > 0)
            .ToList();

    [GeneratedRegex(@"^\s*(?:[-*+•·◦▪–]\s+|\d{1,3}[.)]\s+|\[[ xX]?\]\s*)")]
    private static partial Regex ListMarker();
}
