using System.Text.RegularExpressions;

namespace Tack.Sync;

/// <summary>
/// Ordering keys ("fractional indexing"), the same keys Tack Focus makes (web/src/model/order.ts).
/// A task's position is a string that sorts between its neighbours, so moving one task rewrites
/// only that task, and two devices can reorder different tasks without overwriting each other.
///
/// Keys are base-62 fractions (digits 0-9A-Za-z, in ASCII order) that never end in "0", compared
/// ordinally. Adding to the top or bottom steps a digit instead of halving, so keys grow by one
/// character only every ~60 additions in the same place.
/// </summary>
internal static partial class OrderKey
{
    public const string Mid = "V";

    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int Base = 62;

    /// <summary>Longer keys can only come from a damaged or crafted file (adding at one end grows a key by a character per ~60).</summary>
    public const int MaxLength = 10_000;

    public static bool IsOrderKey(string? value) => value != null && value.Length <= MaxLength && KeyPattern().IsMatch(value);

    /// <summary>Ordinal "a sorts before b", the comparison every key operation uses.</summary>
    public static bool Less(string a, string b) => string.CompareOrdinal(a, b) < 0;

    /// <summary>A key that sorts after <paramref name="a"/>.</summary>
    public static string After(string a)
    {
        for (int i = 0; i < a.Length; i++)
        {
            int d = Digits.IndexOf(a[i]);
            if (d < Base - 1) return a[..i] + Digits[d + 1];
        }
        // All z's: extend, starting low so the next ~60 additions stay this length.
        return a.Length > 0 ? a + "1" : Mid;
    }

    /// <summary>A key that sorts before <paramref name="b"/> (and after the empty key).</summary>
    public static string Before(string b)
    {
        for (int i = 0; i < b.Length; i++)
        {
            int d = Digits.IndexOf(b[i]);
            if (d > 1) return b[..i] + Digits[d - 1];
            if (d == 1) return b[..i] + "0z";
        }
        // Only reachable for malformed keys made of zeros.
        return new string('0', b.Length) + Mid;
    }

    /// <summary>A key strictly between <paramref name="a"/> and <paramref name="b"/>; null means "no bound". Requires a &lt; b.</summary>
    public static string Between(string? a, string? b)
    {
        if (a == null && b == null) return Mid;
        if (a == null) return Before(b!);
        if (b == null) return After(a);
        if (!Less(a, b)) throw new ArgumentException($"OrderKey.Between: \"{a}\" is not before \"{b}\"");

        string prefix = "";
        for (int i = 0; ; i++)
        {
            int da = i < a.Length ? Digits.IndexOf(a[i]) : 0;
            int db = i < b.Length ? Digits.IndexOf(b[i]) : Base;
            if (da == db)
            {
                prefix += DigitText(da);
                continue;
            }
            if (db - da > 1) return prefix + DigitText((da + db) >> 1);
            // Adjacent digits: b's digit alone works if b continues past it…
            if (i + 1 < b.Length) return prefix + DigitText(db);
            // …otherwise keep a's digit and go just past the rest of a.
            return prefix + DigitText(da) + After(i + 1 < a.Length ? a[(i + 1)..] : "");
        }
    }

    /// <summary>
    /// Keys for putting an item at <paramref name="index"/> in a list sorted by key: the item's key first,
    /// then new keys for the neighbours from <paramref name="index"/> on that have to make room. Usually
    /// that's the item's key alone. But lists can hold equal keys (two devices adding at the top at once,
    /// or a task coming back from focus elsewhere with the key it had), and nothing sorts between equal
    /// keys. Then the neighbours below that don't sort after the one above get new keys too, in the same
    /// order, so the item really lands at <paramref name="index"/>.
    ///
    /// The extra keys are ordinary edits made by the device that moves the item, so every device that
    /// receives them sorts the list the same way; merging never has to invent keys.
    /// </summary>
    public static string[] KeysAt(IReadOnlyList<string> sortedKeys, int index)
    {
        string? a = index > 0 && index - 1 < sortedKeys.Count ? sortedKeys[index - 1] : null;
        int end = index;
        while (a != null && end < sortedKeys.Count && !Less(a, sortedKeys[end])) end++;
        string? b = end >= 0 && end < sortedKeys.Count ? sortedKeys[end] : null;
        var keys = new string[end - index + 1];
        if (b == null)
        {
            string? k = a;
            for (int i = 0; i < keys.Length; i++) keys[i] = k = k == null ? Mid : After(k);
        }
        else
        {
            string k = b;
            for (int i = keys.Length - 1; i >= 0; i--) keys[i] = k = Between(a, k);
        }
        return keys;
    }

    /// <summary>
    /// Keys for items that must appear in the given order. Keys already in order are kept (the longest
    /// increasing run, so as few items as possible change); the others get new keys between their
    /// neighbours. Returns the new key for each item, or null where the existing key stays.
    /// A list with no usable keys at all is numbered like Tack Focus numbers an import:
    /// from "V" at the bottom upwards (<paramref name="anchorTop"/> false) or from "V" at the top down.
    /// </summary>
    public static string?[] Fit(IReadOnlyList<(string? Key, string Id)> items, bool anchorTop)
    {
        int n = items.Count;
        bool[] keep = LongestIncreasing(items);
        var result = new string?[n];
        int i = 0;
        while (i < n)
        {
            if (keep[i]) { i++; continue; }
            int start = i;
            while (i < n && !keep[i]) i++;
            // Two kept neighbours with the same key (a tie broken by id) leave no room between them:
            // give the later one a new key as well.
            while (start > 0 && i < n && !Less(items[start - 1].Key!, items[i].Key!))
            {
                keep[i] = false;
                while (i < n && !keep[i]) i++;
            }
            string? prev = start > 0 ? items[start - 1].Key : null;
            string? next = i < n ? items[i].Key : null;
            if (next == null && (prev != null || anchorTop))
            {
                string? k = prev;
                for (int j = start; j < i; j++) result[j] = k = k == null ? Mid : After(k);
            }
            else
            {
                string? k = next;
                for (int j = i - 1; j >= start; j--) result[j] = k = Between(prev, k);
            }
        }
        return result;
    }

    /// <summary>Marks a longest run of items whose (key, id) strictly increase; items without a key never count.</summary>
    private static bool[] LongestIncreasing(IReadOnlyList<(string? Key, string Id)> items)
    {
        int n = items.Count;
        var tails = new List<int>();   // tails[k]: index of the smallest last item of an increasing run of length k+1
        var previous = new int[n];
        for (int i = 0; i < n; i++)
        {
            previous[i] = -1;
            if (items[i].Key == null) continue;
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Compare(items[tails[mid]], items[i]) < 0) lo = mid + 1; else hi = mid;
            }
            if (lo > 0) previous[i] = tails[lo - 1];
            if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
        }
        var keep = new bool[n];
        for (int i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = previous[i]) keep[i] = true;
        return keep;
    }

    private static int Compare((string? Key, string Id) a, (string? Key, string Id) b)
    {
        int c = string.CompareOrdinal(a.Key, b.Key);
        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    }

    /// <summary>DIGITS[d] concatenated in JavaScript ("undefined" past the ends; only malformed keys get there).</summary>
    private static string DigitText(int d) => d is >= 0 and < Base ? Digits[d].ToString() : "undefined";

    [GeneratedRegex(@"^[0-9A-Za-z]*[1-9A-Za-z]\z")]
    private static partial Regex KeyPattern();
}
