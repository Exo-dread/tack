using System.Globalization;
using System.Text.RegularExpressions;

namespace Tack.Sync;

/// <summary>
/// Dates as both apps store them: ISO 8601 with the local offset. Records keep the exact text they
/// arrived with, so a date written by Tack Focus goes back unchanged; these helpers read and write it.
/// </summary>
internal static partial class IsoTime
{
    /// <summary>The time zone dates are shown and read in; tests pin it so shared vectors come out the same anywhere.</summary>
    internal static TimeZoneInfo? ZoneOverride { get; set; }

    public static TimeZoneInfo Zone => ZoneOverride ?? TimeZoneInfo.Local;

    /// <summary>isoLocal in time.ts: always three fractional digits, e.g. 2026-10-17T21:40:12.345+01:00.</summary>
    public static string Format(DateTimeOffset t) =>
        t.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    /// <summary>The form System.Text.Json writes in tack.json's top level (fraction without trailing zeros).</summary>
    public static string FormatTrimmed(DateTimeOffset t) =>
        t.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

    /// <summary>Drops sub-millisecond precision, which JavaScript dates don't have.</summary>
    public static DateTimeOffset ToMilliseconds(DateTimeOffset t) => t.AddTicks(-(t.Ticks % TimeSpan.TicksPerMillisecond));

    /// <summary>
    /// Milliseconds since the epoch for an ISO date, read exactly as parseDate in time.ts reads it
    /// (fractions of any length, "Z" or ±hh[:]mm, no zone meaning local time). Null if unreadable.
    /// </summary>
    public static long? ParseMs(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var m = IsoPattern().Match(JsText.Trim(value));
        if (!m.Success) return ParseOther(value);

        string fraction = (m.Groups[7].Value + "000")[..3];
        int year = Int(m.Groups[1]), month = Int(m.Groups[2]), day = Int(m.Groups[3]);
        int hour = Int(m.Groups[4]), minute = Int(m.Groups[5]);
        int second = m.Groups[6].Success ? Int(m.Groups[6]) : 0;
        int ms = int.Parse(fraction, CultureInfo.InvariantCulture);

        // The limits V8 applies to this format: days up to 31 in any month roll over, 24:00:00.000 is
        // the next midnight, offsets up to ±23:59.
        if (month is < 1 or > 12 || day is < 1 or > 31 || minute > 59 || second > 59) return null;
        if (hour > 24 || (hour == 24 && (minute != 0 || second != 0 || ms != 0))) return null;

        long local = DaysFromCivil(year, month, 1) * 86_400_000L + (day - 1) * 86_400_000L
            + hour * 3_600_000L + minute * 60_000L + second * 1000L + ms;

        string zone = m.Groups[8].Value;
        if (zone.Length == 0) return LocalToUtc(local);
        if (zone is "Z" or "z") return local;
        int sign = zone[0] == '-' ? -1 : 1;
        string digits = zone[1..].Replace(":", "");
        int oh = int.Parse(digits[..2], CultureInfo.InvariantCulture), om = int.Parse(digits[2..], CultureInfo.InvariantCulture);
        if (oh > 23 || om > 59) return null;
        return local - sign * (oh * 3_600_000L + om * 60_000L);
    }

    /// <summary>
    /// The same instant as <see cref="ParseMs"/>, to the tick (Tack 1.1 wrote seven fractional digits),
    /// keeping the text's own offset where .NET can represent it.
    /// </summary>
    public static DateTimeOffset? Parse(string? value)
    {
        if (ParseMs(value) is not long ms || ms < MinMs || ms > MaxMs) return null;
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        var m = IsoPattern().Match(JsText.Trim(value!));
        if (m.Success && m.Groups[7].Value is { Length: > 3 } fraction)
            utc = utc.AddTicks(int.Parse((fraction + "0000")[3..7], CultureInfo.InvariantCulture));
        if (m.Success && m.Groups[8].Value is { Length: > 1 } zone)
        {
            int sign = zone[0] == '-' ? -1 : 1;
            string digits = zone[1..].Replace(":", "");
            var offset = TimeSpan.FromMinutes(sign * (int.Parse(digits[..2], CultureInfo.InvariantCulture) * 60 + int.Parse(digits[2..], CultureInfo.InvariantCulture)));
            if (offset.Duration() <= TimeSpan.FromHours(14))
            {
                try { return utc.ToOffset(offset); } catch (ArgumentOutOfRangeException) { }
            }
            return utc;
        }
        if (m.Success && m.Groups[8].Value is "Z" or "z") return utc;
        return TimeZoneInfo.ConvertTime(utc, Zone);
    }

    private static readonly long MinMs = DateTimeOffset.MinValue.AddDays(1).ToUnixTimeMilliseconds();
    private static readonly long MaxMs = DateTimeOffset.MaxValue.AddDays(-1).ToUnixTimeMilliseconds();

    private static int Int(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);

    /// <summary>Days since 1970-01-01 of a proleptic Gregorian date (year 0 included, as in JavaScript).</summary>
    private static long DaysFromCivil(long y, int m, int d)
    {
        y -= m <= 2 ? 1 : 0;
        long era = (y >= 0 ? y : y - 399) / 400;
        long yoe = y - era * 400;
        long doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    private static long? LocalToUtc(long localMs)
    {
        if (localMs < MinMs || localMs > MaxMs) return localMs;
        var local = DateTime.SpecifyKind(DateTime.UnixEpoch.AddMilliseconds(localMs), DateTimeKind.Unspecified);
        return localMs - (long)Zone.GetUtcOffset(local).TotalMilliseconds;
    }

    /// <summary>Anything else: JavaScript falls back to its engine's own date parser; this is the nearest .NET reading.</summary>
    private static long? ParseOther(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? d.ToUnixTimeMilliseconds()
            : null;

    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt]([0-9]{2}):([0-9]{2})(?::([0-9]{2}))?(?:[.,]([0-9]+))?([Zz]|[+-][0-9]{2}:?[0-9]{2})?\z")]
    private static partial Regex IsoPattern();
}
