namespace Tack.Drop.Core;

/// <summary>
/// How much one drop onto the orb, and what Tack keeps for it, may be. All of it stays on this PC (nothing
/// here is synced, imported or exported), so these aren't sync limits and don't live in DeviceFile.
/// </summary>
internal static class DropLimits
{
    /// <summary>Tasks one drop makes. More than this is refused while it's dragged over the orb.</summary>
    public const int MaxItems = 20;

    /// <summary>A page title, an email's subject or a file name, as a task's text.</summary>
    public const int MaxTitle = 500;

    /// <summary>What attachments.json keeps to name an attachment.</summary>
    public const int MaxLabel = 260;

    /// <summary>A path or web address (as long as a launcher shortcut's).</summary>
    public const int MaxTarget = Launcher.Core.LauncherConfig.MaxTarget;

    /// <summary>Dropped text is read up to this much (a million characters); more is refused.</summary>
    public const int MaxTextBytes = 2 * 1024 * 1024;

    /// <summary>A web address list or a file name, read up to this much.</summary>
    public const int MaxSmallBytes = 256 * 1024;

    /// <summary>Entries a list of virtual files may claim before it's refused unread.</summary>
    public const int MaxDescriptors = 1_000;

    /// <summary>Paths a file drop may carry before it's refused unread.</summary>
    public const int MaxPaths = 10_000;

    /// <summary>One email (or other dragged file) Tack keeps a copy of.</summary>
    public const long MaxSavedBytes = 50L * 1024 * 1024;

    /// <summary>The attachments folder in all: past this, new copies aren't kept (the task is still made).</summary>
    public const long MaxFolderBytes = 500L * 1024 * 1024;

    /// <summary>Attachment records in attachments.json.</summary>
    public const int MaxAttachments = 5_000;

    /// <summary>attachments.json itself (checked before reading).</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;
}

/// <summary>Whether a piece of dragged data was there, and small enough to read.</summary>
internal enum DataRead { Missing, Ok, TooBig }

/// <summary>A dragged format's value: its bytes as the source gave them, or text (data made in this process).</summary>
internal readonly record struct DataValue(DataRead Status, byte[]? Bytes = null, string? Text = null)
{
    public static readonly DataValue Missing = new(DataRead.Missing);
    public static readonly DataValue TooBig = new(DataRead.TooBig);
}

/// <summary>
/// What a drag carries, read with bounds: the OLE data object from another app, behind an interface so the
/// rules can be tested with any data. Every read is limited before anything is copied.
/// </summary>
internal interface IDropData
{
    /// <summary>The formats on offer (for the log, and to tell what kind of drag it is).</summary>
    IReadOnlyList<string> Formats { get; }

    bool Has(string format);

    /// <summary>CF_HDROP: how many paths there are and, when that's at most <paramref name="max"/>, the paths.</summary>
    IReadOnlyList<string>? Files(int max, out int count);

    /// <summary>A format's data, unless it's larger than <paramref name="maxBytes"/>.</summary>
    DataValue Read(string format, int maxBytes);

    /// <summary>FileContents of the virtual file at <paramref name="index"/>, written to <paramref name="path"/> (deleted again if it's too big).</summary>
    DataRead SaveContents(int index, string path, long maxBytes);
}

/// <summary>The format names Tack looks for, as Windows and the apps that drag register them.</summary>
internal static class DropFormats
{
    public const string FileDrop = "FileDrop";              // CF_HDROP: files and folders from Explorer, the desktop, most apps
    public const string UnicodeText = "UnicodeText";        // CF_UNICODETEXT
    public const string Text = "Text";                      // CF_TEXT (ANSI)
    public const string UrlW = "UniformResourceLocatorW";   // a link (browsers)
    public const string Url = "UniformResourceLocator";     // the same, ANSI
    public const string MozUrl = "text/x-moz-url";          // address, newline, title (Firefox, Chromium)
    public const string UriList = "text/uri-list";          // one address per line
    public const string DescriptorW = "FileGroupDescriptorW"; // virtual files (Outlook's emails, a browser's link file)
    public const string Descriptor = "FileGroupDescriptor";  // the same, ANSI
    public const string Contents = "FileContents";          // a virtual file's bytes, by index
}

/// <summary>What one dropped item becomes.</summary>
internal enum ItemKind
{
    /// <summary>A file on disk, opened where it is.</summary>
    File,
    /// <summary>A folder on disk.</summary>
    Folder,
    /// <summary>A web page (http or https).</summary>
    Link,
    /// <summary>A virtual file (an email from Outlook) or a file from a temporary folder: Tack keeps a copy.</summary>
    Copy,
    /// <summary>A task and nothing else (a line of text, or something Tack won't keep).</summary>
    Text,
}

/// <param name="Text">The task's text.</param>
/// <param name="Target">File or folder path, or web address.</param>
/// <param name="Label">The file's name (with its extension) or the page's title.</param>
/// <param name="ContentIndex">A virtual file's index in FileContents.</param>
/// <param name="CopyFrom">A file in a temporary folder to copy.</param>
/// <param name="Note">Why an item is text alone when it might have had an attachment.</param>
internal sealed record DropItem(ItemKind Kind, string Text, string? Target = null, string? Label = null,
    int ContentIndex = -1, string? CopyFrom = null, long? Size = null, DropNote? Note = null);

/// <summary>Reasons an item comes without its attachment.</summary>
internal enum DropNote { Program, TooBig, FolderFull, StoreFull, Unreadable }

/// <summary>The kinds of drag Tack recognises, in the order it prefers them.</summary>
internal enum DropFamily { None, Files, Links, VirtualFiles, Text }

/// <summary>What a drag would make, or why it's refused.</summary>
internal sealed record DropRead(DropFamily Family, IReadOnlyList<DropItem> Items, string? Refusal = null)
{
    public static readonly DropRead None = new(DropFamily.None, []);

    public int Count => Items.Count;
}
