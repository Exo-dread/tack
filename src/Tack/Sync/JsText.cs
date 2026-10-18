using System.Text;

namespace Tack.Sync;

/// <summary>
/// The few JavaScript string behaviours the shared model depends on, so the desktop reaches the
/// same answers as Tack Focus: what counts as white space (/\s/ and trim()), JSON.stringify of a
/// string (used to break ties between equal clocks) and parseInt(…, 36) (used to read stamps).
/// </summary>
internal static class JsText
{
    /// <summary>JavaScript's WhiteSpace and LineTerminator characters (note: not U+0085, not U+180E, but U+FEFF).</summary>
    public static bool IsWhiteSpace(char c) => c switch
    {
        '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' => true,
        >= '\u2000' and <= '\u200A' => true,
        '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' => true,
        _ => false,
    };

    /// <summary>String.prototype.trim().</summary>
    public static string Trim(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsWhiteSpace(s[start])) start++;
        while (end > start && IsWhiteSpace(s[end - 1])) end--;
        return start == 0 && end == s.Length ? s : s[start..end];
    }

    /// <summary>Task text is one line: newlines and runs of spaces collapse (cleanTaskText in text.ts).</summary>
    public static string CleanTaskText(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inSpace = false;
        foreach (char c in text)
        {
            if (IsWhiteSpace(c))
            {
                if (!inSpace) sb.Append(' ');
                inSpace = true;
            }
            else
            {
                sb.Append(c);
                inSpace = false;
            }
        }
        return Trim(sb.ToString());
    }

    /// <summary>JSON.stringify(value) for a string or null.</summary>
    public static string Stringify(string? value)
    {
        if (value == null) return "null";
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    bool lone = char.IsHighSurrogate(c)
                        ? i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])
                        : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(value[i - 1]));
                    if (c < ' ' || lone) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>parseInt(s, 36): leading white space, an optional sign, then base-36 digits up to the first other character. NaN if none.</summary>
    public static double ParseInt36(string s)
    {
        int i = 0;
        while (i < s.Length && IsWhiteSpace(s[i])) i++;
        double sign = 1;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
        {
            if (s[i] == '-') sign = -1;
            i++;
        }
        double value = 0;
        int digits = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            int d = c is >= '0' and <= '9' ? c - '0'
                : c is >= 'a' and <= 'z' ? c - 'a' + 10
                : c is >= 'A' and <= 'Z' ? c - 'A' + 10
                : -1;
            if (d < 0) break;
            value = value * 36 + d;
            digits++;
        }
        return digits == 0 ? double.NaN : sign * value;
    }
}
