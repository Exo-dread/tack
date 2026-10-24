using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Tack.Launcher.Core;
using Tack.Model;
using Tack.Sync;

namespace Tack.Drop.Core;

/// <summary>What reading a drag may ask of the PC: injectable, so the rules can be tested without the real disk.</summary>
internal sealed class DropContext
{
    public required IFileProbe Probe { get; init; }

    /// <summary>On another computer (a UNC path, a mapped network drive): never asked about, so an unreachable server can't stall Tack.</summary>
    public Func<string, bool> IsNetwork { get; init; } = WinPath.IsUnc;

    /// <summary>In a folder that's cleaned out from time to time (%TEMP%, a browser's or Outlook's cache): such a file is copied.</summary>
    public Func<string, bool> IsTemporary { get; init; } = _ => false;

    /// <summary>A file type that runs code (".exe"): never copied.</summary>
    public Func<string, bool> IsProgram { get; init; } = DropNames.ProgramExtensions.Contains;

    /// <summary>A local email file's subject (.eml or .msg), or null.</summary>
    public Func<string, string?> EmailSubject { get; init; } = _ => null;
}

/// <summary>
/// Turns a drag into tasks to make, one per item, in the order Tack prefers what a drag offers: files and
/// folders (CF_HDROP), then virtual files (an email dragged from Outlook), then web links (a browser's
/// address, with the page's title when it gives one), then plain text (one task a line, as a paste makes).
/// Every read is bounded, more than <see cref="DropLimits.MaxItems"/> items is refused before any is read,
/// and nothing here touches a network path or opens anything.
/// </summary>
internal static class DropReader
{
    private const int DescriptorSizeW = 592; // FILEDESCRIPTORW
    private const int DescriptorSizeA = 332; // FILEDESCRIPTORA
    private const int NameOffset = 72;
    private const uint FD_ATTRIBUTES = 0x04;
    private const uint FD_FILESIZE = 0x40;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    public static string TooMany => $"Drop up to {DropLimits.MaxItems} at a time";
    public static string TooManyLines => $"Drop up to {DropLimits.MaxItems} lines at a time";

    /// <summary>
    /// What the drag makes. With <paramref name="preview"/> (while it's dragged over the orb), only what it
    /// is and how many: nothing on disk is looked at.
    /// </summary>
    public static DropRead Read(IDropData data, DropContext ctx, bool preview = false)
    {
        if (data.Has(DropFormats.FileDrop))
        {
            var files = Files(data, ctx, preview);
            if (files.Refusal != null || files.Count > 0) return files;
        }

        var described = Descriptors(data, out string? tooMany);
        if (tooMany != null) return new DropRead(DropFamily.VirtualFiles, [], tooMany);
        // A browser offers a link file ("Page title.url") with every link it drags: the link is what's meant.
        bool shortcutsOnly = described is { Count: > 0 } && described.All(d => DropNames.IsShortcut(d.Name));
        if (described is { Count: > 0 } && !shortcutsOnly) return Virtual(described, ctx);

        var links = Links(data, shortcutsOnly ? described : null);
        if (links.Refusal != null || links.Count > 0) return links;
        if (shortcutsOnly) return Virtual(described!, ctx);

        return Text(data);
    }

    // ================= Files and folders =================

    private static DropRead Files(IDropData data, DropContext ctx, bool preview)
    {
        var paths = data.Files(DropLimits.MaxItems, out int count);
        if (count > DropLimits.MaxItems) return new DropRead(DropFamily.Files, [], TooMany);
        if (paths == null) return DropRead.None;

        var items = new List<DropItem>();
        foreach (string raw in paths)
        {
            if (AttachmentRules.CleanPath(raw) is not { } path) continue;
            string name = DropNames.LastSegment(path);
            if (name.EndsWith(':')) name = path; // a drive's root
            if (preview)
            {
                items.Add(new DropItem(ItemKind.File, name));
                continue;
            }

            bool network = ctx.IsNetwork(path);
            bool folder;
            if (network) folder = !DropNames.LooksLikeFile(name);
            else
            {
                folder = ctx.Probe.DirectoryExists(path);
                if (!folder && !ctx.Probe.FileExists(path)) continue; // gone since the drag began
            }
            string label = DropNames.OneLine(name, DropLimits.MaxLabel);
            if (folder)
            {
                items.Add(new DropItem(ItemKind.Folder, DropNames.OneLine(name, DropLimits.MaxTitle), path, label));
                continue;
            }

            string text = DropNames.OneLine(DropNames.WithoutExtension(name), DropLimits.MaxTitle);
            if (!network && DropNames.IsEmail(name)) text = ctx.EmailSubject(path) ?? text;
            if (text.Length == 0) text = label;
            if (!network && ctx.IsTemporary(path))
            {
                // Whoever put it there will clean it away: Tack keeps a copy, never of a program.
                items.Add(ctx.IsProgram(WinPath.Extension(name))
                    ? new DropItem(ItemKind.Text, text, Note: DropNote.Program)
                    : new DropItem(ItemKind.Copy, text, Label: DropNames.SafeFileName(name, "Untitled"), CopyFrom: path));
            }
            else items.Add(new DropItem(ItemKind.File, text, path, label));
        }
        return new DropRead(DropFamily.Files, items);
    }

    // ================= Virtual files (FileGroupDescriptorW + FileContents) =================

    private sealed record Described(int Index, string Name, long? Size);

    /// <summary>The files a FILEGROUPDESCRIPTOR lists (folders left out), or null when there's none.</summary>
    private static List<Described>? Descriptors(IDropData data, out string? tooMany)
    {
        tooMany = null;
        bool wide = true;
        int size = DescriptorSizeW;
        var value = data.Has(DropFormats.DescriptorW) ? data.Read(DropFormats.DescriptorW, 4 + DescriptorSizeW * DropLimits.MaxDescriptors) : DataValue.Missing;
        if (value.Status == DataRead.Missing && data.Has(DropFormats.Descriptor))
        {
            wide = false;
            size = DescriptorSizeA;
            value = data.Read(DropFormats.Descriptor, 4 + DescriptorSizeA * DropLimits.MaxDescriptors);
        }
        if (value.Status == DataRead.TooBig) { tooMany = TooMany; return null; }
        if (value.Bytes is not { Length: >= 4 } bytes) return null;

        uint claimed = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (claimed > DropLimits.MaxDescriptors) { tooMany = TooMany; return null; }
        int n = (int)Math.Min(claimed, (uint)((bytes.Length - 4) / size));
        var list = new List<Described>();
        for (int i = 0; i < n; i++)
        {
            var d = bytes.AsSpan(4 + i * size, size);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d);
            uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(d[36..]);
            if ((flags & FD_ATTRIBUTES) != 0 && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
            long? length = (flags & FD_FILESIZE) != 0
                ? ((long)BinaryPrimitives.ReadUInt32LittleEndian(d[64..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(d[68..])
                : null;
            var nameBytes = d[NameOffset..];
            string name = wide ? Wide(nameBytes) : Ansi(nameBytes);
            list.Add(new Described(i, name, length));
            if (list.Count > DropLimits.MaxItems) { tooMany = TooMany; return null; }
        }
        return list.Count > 0 ? list : null;
    }

    private static DropRead Virtual(List<Described> files, DropContext ctx)
    {
        var items = new List<DropItem>();
        foreach (var f in files)
        {
            string name = DropNames.SafeFileName(f.Name, "Untitled");
            string text = DropNames.OneLine(DropNames.WithoutExtension(name), DropLimits.MaxTitle);
            if (text.Length == 0) text = name;
            if (ctx.IsProgram(WinPath.Extension(name)))
                items.Add(new DropItem(ItemKind.Text, text, Note: DropNote.Program));
            else if (f.Size > DropLimits.MaxSavedBytes)
                items.Add(new DropItem(ItemKind.Text, text, Note: DropNote.TooBig));
            else
                items.Add(new DropItem(ItemKind.Copy, text, Label: name, ContentIndex: f.Index, Size: f.Size));
        }
        return new DropRead(DropFamily.VirtualFiles, items);
    }

    // ================= Links =================

    private static DropRead Links(IDropData data, List<Described>? shortcuts)
    {
        var found = new List<(string Url, string? Title)>();
        if (data.Has(DropFormats.MozUrl))
        {
            // "address\ntitle", once per link.
            var v = data.Read(DropFormats.MozUrl, DropLimits.MaxSmallBytes);
            if (v.Status == DataRead.TooBig) return new DropRead(DropFamily.Links, [], TooMany);
            string[] lines = Value(v, wide: true).Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i += 2)
            {
                if (lines[i].Trim().Length == 0) continue;
                found.Add((lines[i], i + 1 < lines.Length ? lines[i + 1] : null));
                if (found.Count > DropLimits.MaxItems) return new DropRead(DropFamily.Links, [], TooMany);
            }
        }
        if (found.Count == 0)
        {
            foreach (var (format, wide) in new[] { (DropFormats.UrlW, true), (DropFormats.Url, false) })
            {
                if (!data.Has(format)) continue;
                var v = data.Read(format, DropLimits.MaxSmallBytes);
                if (v.Status == DataRead.TooBig) return new DropRead(DropFamily.Links, [], TooMany);
                string url = Value(v, wide).Trim();
                if (url.Length == 0) continue;
                found.Add((url, null));
                break;
            }
        }
        if (found.Count == 0 && data.Has(DropFormats.UriList))
        {
            var v = data.Read(DropFormats.UriList, DropLimits.MaxSmallBytes);
            if (v.Status == DataRead.TooBig) return new DropRead(DropFamily.Links, [], TooMany);
            string text = v.Text ?? (v.Bytes is { Length: >= 2 } b && b[1] == 0 ? Wide(b) : Utf8(v.Bytes ?? []));
            foreach (string line in text.Replace("\r", "").Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                found.Add((t, null));
                if (found.Count > DropLimits.MaxItems) return new DropRead(DropFamily.Links, [], TooMany);
            }
        }
        if (found.Count == 0) return DropRead.None;

        var items = new List<DropItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (raw, title) in found)
        {
            if (AttachmentRules.CleanUrl(raw) is not { } uri || !seen.Add(uri.AbsoluteUri)) continue;
            string? name = DropNames.OneLine(title, DropLimits.MaxTitle);
            if (name.Length == 0 && found.Count == 1 && shortcuts is [var only]) name = DropNames.OneLine(DropNames.WithoutExtension(DropNames.SafeFileName(only.Name, "")), DropLimits.MaxTitle);
            if (name.Length == 0) name = null;
            items.Add(new DropItem(ItemKind.Link, name ?? uri.AbsoluteUri, uri.AbsoluteUri, name ?? uri.IdnHost));
        }
        return new DropRead(DropFamily.Links, items);
    }

    // ================= Text =================

    private static DropRead Text(IDropData data)
    {
        var v = data.Has(DropFormats.UnicodeText) ? data.Read(DropFormats.UnicodeText, DropLimits.MaxTextBytes) : DataValue.Missing;
        bool wide = true;
        if (v.Status == DataRead.Missing && data.Has(DropFormats.Text))
        {
            v = data.Read(DropFormats.Text, DropLimits.MaxTextBytes);
            wide = false;
        }
        if (v.Status == DataRead.TooBig) return new DropRead(DropFamily.Text, [], TooManyLines);
        if (v.Status == DataRead.Missing) return DropRead.None;

        var lines = TaskLines.Split(Value(v, wide));
        if (lines.Count > DropLimits.MaxItems) return new DropRead(DropFamily.Text, [], TooManyLines);
        // A line longer than any device reads from a sync file would stay on this PC alone (as with paste).
        lines.RemoveAll(l => JsText.CleanTaskText(l).Length > DeviceFile.MaxTaskText);
        return new DropRead(DropFamily.Text, lines.Select(l => new DropItem(ItemKind.Text, l)).ToList());
    }

    // ================= Decoding =================

    private static string Value(DataValue v, bool wide) => v.Text is { } t ? UpToNul(t) : v.Bytes is { } b ? (wide ? Wide(b) : Ansi(b)) : "";

    private static string UpToNul(string s) => s.IndexOf('\0') is var z and >= 0 ? s[..z] : s;

    private static string Wide(ReadOnlySpan<byte> bytes) => UpToNul(Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]));

    private static string Utf8(ReadOnlySpan<byte> bytes)
    {
        int z = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(z >= 0 ? bytes[..z] : bytes);
    }

    private static string Ansi(ReadOnlySpan<byte> bytes)
    {
        int z = bytes.IndexOf((byte)0);
        return AnsiEncoding.GetString(z >= 0 ? bytes[..z] : bytes);
    }

    private static readonly Encoding AnsiEncoding = MakeAnsi();

    private static Encoding MakeAnsi()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception) { return Encoding.Latin1; }
    }
}
