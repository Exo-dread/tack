using System.Text.RegularExpressions;

namespace Tack.Sync;

/// <summary>
/// Hybrid logical clock stamps, the same strings Tack Focus writes (web/src/model/hlc.ts):
///
///   &lt;ms since epoch, base 36, 9 chars&gt;.&lt;counter, base 36, 4 chars&gt;.&lt;device id&gt;
///
/// Stamps compare as plain ordinal strings, so "later" is just <see cref="string.CompareOrdinal(string, string)"/>.
/// Everything here must produce exactly what the TypeScript produces; tests/vectors/sync.json checks it.
/// </summary>
internal static partial class Stamp
{
    public const string Zero = "000000000.0000.0";

    internal const long MaxCounter = 36L * 36 * 36 * 36 - 1;
    private const int MsWidth = 9;
    private const int CounterWidth = 4;
    private const long YearAndADayMs = 366L * 24 * 60 * 60 * 1000;
    private const string Digits36 = "0123456789abcdefghijklmnopqrstuvwxyz";

    public static string Encode(long ms, long counter, string node) =>
        $"{Base36(ms, MsWidth)}.{Base36(counter, CounterWidth)}.{node}";

    /// <summary>The parts of a stamp, read the way JavaScript's parseInt reads them (null if unreadable).</summary>
    public static (double Ms, double Counter, string Node)? Decode(string stamp)
    {
        // const [ms, counter, node] = stamp.split('.')
        string[] parts = stamp.Split('.');
        string ms = parts[0];
        string? counter = parts.Length > 1 ? parts[1] : null;
        string? node = parts.Length > 2 ? parts[2] : null;
        if (ms.Length == 0 || string.IsNullOrEmpty(counter) || node == null) return null;
        double m = JsText.ParseInt36(ms), c = JsText.ParseInt36(counter);
        if (!double.IsFinite(m) || !double.IsFinite(c)) return null;
        return (m, c, node);
    }

    /// <summary>Wall-clock time of a stamp in ms (0 if unreadable).</summary>
    public static double Time(string stamp) => Decode(stamp)?.Ms ?? 0;

    /// <summary>The device a stamp came from; stamps derived from desktop dates use the empty device "".</summary>
    public static string? Node(string stamp) => Decode(stamp)?.Node;

    public static bool IsStamp(string? value) => value != null && StampPattern().IsMatch(value);

    /// <summary>A stamp from a file that's plausible: well-formed and not more than a year in the future.</summary>
    public static bool IsPlausible(string? value, long nowMs) => IsStamp(value) && Time(value!) <= nowMs + YearAndADayMs;

    /// <summary>A clock for a record that has only a date (from a file without clocks): from a pseudo-device that loses ties.</summary>
    public static string FromTime(double ms) => Encode((long)Math.Max(0, Math.Floor(ms)), 0, "");

    public static bool IsLater(string a, string b) => string.CompareOrdinal(a, b) > 0;

    public static string Max(string a, string b) => IsLater(b, a) ? b : a;

    /// <summary>Number.prototype.toString(36), then padStart(width, '0') (negative numbers keep their sign inside the padding, as in JS).</summary>
    private static string Base36(long n, int width)
    {
        bool negative = n < 0;
        ulong v = negative ? (ulong)(-(n + 1)) + 1 : (ulong)n;
        Span<char> buffer = stackalloc char[16];
        int i = buffer.Length;
        do
        {
            buffer[--i] = Digits36[(int)(v % 36)];
            v /= 36;
        } while (v > 0);
        if (negative) buffer[--i] = '-';
        return new string(buffer[i..]).PadLeft(width, '0');
    }

    [GeneratedRegex(@"^[0-9a-z]{9}\.[0-9a-z]{4}\.[0-9a-zA-Z_-]*\z")]
    private static partial Regex StampPattern();
}
