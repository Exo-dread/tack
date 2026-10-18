using System.Security.Cryptography;

namespace Tack.Sync;

/// <summary>Ids, as Tack Focus makes them (web/src/model/ids.ts).</summary>
internal static class Ids
{
    private const string DeviceAlphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>12 random hex characters, the shape every Tack task and sheet id has.</summary>
    public static string NewId()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>A short random device id, used to break ties between clocks.</summary>
    public static string NewDeviceId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        Span<char> id = stackalloc char[8];
        for (int i = 0; i < 8; i++) id[i] = DeviceAlphabet[bytes[i] % DeviceAlphabet.Length];
        return new string(id);
    }

    /// <summary>
    /// A 12-hex id derived from a string, for things every device must create identically
    /// (e.g. the copy kept when two devices edited the same note at once).
    /// </summary>
    public static string Derived(string source)
    {
        // Two independent FNV-1a passes give 64 bits; 48 of them are used. (Math.imul and >>> in the original.)
        uint h1 = 0x811c9dc5;
        uint h2 = 0x01000193 ^ 0x5bd1e995;
        foreach (char c in source)
        {
            h1 = unchecked((h1 ^ c) * 0x01000193);
            h2 = unchecked((h2 ^ c) * 0x5bd1e995) ^ (h2 >> 15);
        }
        return (h1.ToString("x8") + h2.ToString("x8"))[..12];
    }

    /// <summary>
    /// The id Tack Focus gives a record from a file whose id is missing, malformed or already taken:
    /// stable for the same content, so importing the same file twice doesn't duplicate it (validId in file.ts).
    /// </summary>
    public static string ForImport(string? raw, HashSet<string> used, string seed)
    {
        string id = raw != null && IsValid(raw) ? raw : "";
        if (id.Length == 0 || used.Contains(id))
        {
            int h = 0;
            string s = $"{seed}:{used.Count}";
            foreach (char c in s) h = unchecked(h * 31 + c);
            id = $"imp{(uint)h:x8}";
        }
        used.Add(id);
        return id;
    }

    /// <summary>Ids from files: the shape Tack itself makes (letters, digits, _ and -, at most 64).</summary>
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        foreach (char c in id)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or '-')) return false;
        }
        return true;
    }
}
