using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Tack.Drop.Core;

/// <summary>
/// An email's subject from an .eml file (RFC 5322 headers, RFC 2047 encoded words), for the task a dropped
/// email becomes. Only the header block is read, at most 64 KB of it; the body is never looked at.
/// </summary>
internal static partial class MailSubject
{
    private const int MaxHeaderBytes = 64 * 1024;

    static MailSubject()
    {
        // Mail comes in every charset; .NET knows only the Unicode ones and Latin-1 until asked.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string? FromEml(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FromEml(file);
        }
        catch (Exception) { return null; }
    }

    /// <summary>The Subject header, decoded and on one line; null when there's none (or it's empty).</summary>
    public static string? FromEml(Stream stream)
    {
        var buffer = new byte[MaxHeaderBytes];
        int n = 0;
        while (n < buffer.Length)
        {
            int got = stream.Read(buffer, n, buffer.Length - n);
            if (got <= 0) break;
            n += got;
        }
        int start = n >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        string text = Decode(buffer.AsSpan(start, n - start));

        // The header block ends at the first empty line; folded lines go on with a space or a tab.
        int end = text.IndexOf("\n\n", StringComparison.Ordinal) is var a and >= 0 ? a : text.Length;
        int crlf = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (crlf >= 0 && crlf < end) end = crlf;
        string headers = Unfold().Replace(text[..end], " ");
        foreach (string line in headers.Split('\n'))
        {
            if (!line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase)) continue;
            string subject = DropNames.OneLine(DecodeWords(line[8..]), DropLimits.MaxTitle);
            return subject.Length > 0 ? subject : null;
        }
        return null;
    }

    /// <summary>UTF-8 when it is (mail written by today's apps, RFC 6532); otherwise Latin-1, which never fails.</summary>
    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>"=?utf-8?B?…?=" and "=?iso-8859-1?Q?…?=" words decoded; the space between two of them goes, as RFC 2047 says.</summary>
    public static string DecodeWords(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        int at = 0;
        bool lastWasWord = false;
        foreach (Match m in EncodedWord().Matches(raw))
        {
            string between = raw[at..m.Index];
            if (!(lastWasWord && between.All(c => c is ' ' or '\t' or '\r' or '\n'))) sb.Append(between);
            string? word = DecodeWord(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            sb.Append(word ?? m.Value);
            lastWasWord = word != null;
            at = m.Index + m.Length;
        }
        sb.Append(raw[at..]);
        return sb.ToString();
    }

    private static string? DecodeWord(string charset, string kind, string text)
    {
        try
        {
            int star = charset.IndexOf('*'); // RFC 2231's language: "utf-8*en"
            if (star >= 0) charset = charset[..star];
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { encoding = Encoding.Latin1; }

            byte[] bytes;
            if (kind is "B" or "b")
            {
                string b64 = text.TrimEnd('=');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                bytes = Convert.FromBase64String(b64);
            }
            else
            {
                var list = new List<byte>(text.Length);
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '_') list.Add(0x20);
                    else if (c == '=' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
                    {
                        list.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                        i += 2;
                    }
                    else list.Add((byte)(c & 0xFF));
                }
                bytes = list.ToArray();
            }
            return encoding.GetString(bytes);
        }
        catch (FormatException) { return null; }
    }

    [GeneratedRegex(@"\r?\n[ \t]+")]
    private static partial Regex Unfold();

    [GeneratedRegex(@"=\?([^?\s]{1,40})\?([bBqQ])\?([^?\s]{0,2000})\?=")]
    private static partial Regex EncodedWord();
}
