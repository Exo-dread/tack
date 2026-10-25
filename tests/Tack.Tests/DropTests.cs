using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Tack.Drop;
using Tack.Drop.Core;
using Tack.Interop;
using Tack.Launcher.Core;
using Tack.Model;
using Tack.Services;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Dropping onto the orb, in this process: real WPF data objects, filled the way the app they'd come from
/// fills them (in-process with SetData, or through OLE as another process hands them over), read by the same
/// code the orb uses. Hostile drags included: huge lists, javascript: links, network and device paths,
/// endless streams, 10,000 lines of text. Everything goes to a temporary folder.
/// </summary>
internal static class DropTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("drop: files and folders from Explorer become tasks named after them, first on top, each linked where it is", FilesAndFolders);
        yield return ("drop: an email from Outlook (a .msg compound file, an .eml stream) is saved in the attachments folder and named by its subject", Emails);
        yield return ("drop: an email offered as a file in a temporary folder is copied, never linked where it'll vanish", TemporaryFiles);
        yield return ("drop: a browser's link takes the page's title, else its address, and never its .url file", Links);
        yield return ("drop: text becomes one task a line, as a paste makes them, with nothing attached", Text);
        yield return ("drop: the tasks go into the space on show, and Undo takes them all back", SpaceAndUndo);
        yield return ("drop: the orb never answers Move, so nothing is ever deleted where it came from", NeverMove);
        yield return ("drop, hostile: more than 20 of anything is refused before any of it is read, however it's dressed up", HugeLists);
        yield return ("drop, hostile: javascript:, file:, data: and addresses with a password in them are never links", BadLinks);
        yield return ("drop, hostile: network paths are kept without asking the server; device paths, streams and relative paths never", Paths);
        yield return ("drop, hostile: 10,000 lines of text (or 21, or 2 MB) are refused; 20 are taken", ManyLines);
        yield return ("drop, hostile: a virtual file can't name its way out of its folder, pass for a PDF or be a program", HostileNames);
        yield return ("drop, hostile: an endless or oversized email isn't kept and leaves nothing behind; the task still is", HostileSizes);
        yield return ("attachments: the folder and the store have caps; past them the task is made without its attachment", Caps);
        yield return ("attachments: a task deleted for good lets go of its attachment and saved copy; Undo on offer, a live task or a peer's fresh delete keep it", LettingGo);
        yield return ("attachments: copies nothing points to are swept, only Tack's own folders, and attachments.json reads back what it wrote", SweepAndRoundTrip);
        yield return ("attachments: a tack.json that can't be read (nor its .bak) lets go of none of them, and sweeps no copy, all session", KeptWhileTackJsonUnreadable);
        yield return ("attachments: the paperclip opens by the launcher's rules, and a missing file says so", Opening);
    }

    // ================= Helpers =================

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tack-drop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Workspace NewWorkspace() => new(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);

    /// <summary>A probe that answers from what it's told, and remembers every path it was asked about.</summary>
    private sealed class Probe(IEnumerable<string>? files = null, IEnumerable<string>? folders = null) : IFileProbe
    {
        private readonly HashSet<string> _files = new(files ?? [], StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _folders = new(folders ?? [], StringComparer.OrdinalIgnoreCase);
        public List<string> Asked { get; } = new();
        public bool FileExists(string path) { Asked.Add(path); return _files.Contains(path) || File.Exists(path); }
        public bool DirectoryExists(string path) { Asked.Add(path); return _folders.Contains(path) || Directory.Exists(path); }
        public string ExpandEnvironment(string path) => throw new TestFailure($"a dropped path is never expanded ({path})");
    }

    private static DropContext Ctx(IFileProbe? probe = null, Func<string, bool>? temporary = null) => new()
    {
        Probe = probe ?? new Probe(),
        IsNetwork = WinPath.IsUnc,
        IsTemporary = temporary ?? (_ => false),
        IsProgram = DesktopDrop.IsProgram,
        EmailSubject = DesktopDrop.EmailSubject,
    };

    private sealed class Setup : IDisposable
    {
        public Setup(DropContext? ctx = null)
        {
            Folder = TempDir();
            Ws = NewWorkspace();
            Store = new AttachmentStore(Folder, DesktopDrop.IsProgram);
            Intake = new DropIntake(Ws, Store, ctx ?? Ctx(), DesktopDrop.MarkFromOutside);
        }

        public string Folder { get; }
        public Workspace Ws { get; }
        public AttachmentStore Store { get; }
        public DropIntake Intake { get; }

        public DropOutcome Take(System.Windows.IDataObject data, string space = SpaceField.Main) => Intake.Take(new OleDropData(data), space);

        public DropRead Preview(System.Windows.IDataObject data) => Intake.Preview(new OleDropData(data));

        public void Dispose()
        {
            try { Directory.Delete(Folder, recursive: true); } catch { }
        }
    }

    private static string[] Texts(DropOutcome o) => o.Added.Select(t => t.Text).ToArray();

    /// <summary>The Mark of the Web's zone on a file (0 when it has none).</summary>
    private static int Zone(string path)
    {
        try
        {
            string mark = File.ReadAllText(path + ":Zone.Identifier");
            int at = mark.IndexOf("ZoneId=", StringComparison.Ordinal);
            return at >= 0 ? mark[at + 7] - '0' : 0;
        }
        catch (Exception) { return 0; }
    }

    private static NativeMethods.IStorage MsgStorage(string path, string subject)
    {
        Marshal.ThrowExceptionForHR(NativeMethods.StgCreateDocfile(path, NativeMethods.STGM_CREATE | NativeMethods.STGM_READWRITE | NativeMethods.STGM_SHARE_EXCLUSIVE, 0, out var storage));
        Marshal.ThrowExceptionForHR(storage.CreateStream("__substg1.0_0037001F", NativeMethods.STGM_READWRITE | NativeMethods.STGM_SHARE_EXCLUSIVE, 0, 0, out var stream));
        var bytes = Encoding.Unicode.GetBytes(subject);
        stream.Write(bytes, bytes.Length, IntPtr.Zero);
        Marshal.ReleaseComObject(stream);
        Marshal.ThrowExceptionForHR(storage.Commit(0));
        return storage;
    }

    private static byte[] Eml(string subjectHeader, string body = "Hello") =>
        Encoding.UTF8.GetBytes($"From: Coordinator <bim@example.com>\r\nTo: you@example.com\r\nSubject: {subjectHeader}\r\nMIME-Version: 1.0\r\n\r\n{body}\r\nSubject: not this one\r\n");

    // ================= Files, emails, links, text =================

    private static void FilesAndFolders()
    {
        using var s = new Setup();
        string models = Path.Combine(s.Folder, "Models");
        Directory.CreateDirectory(models);
        string report = Path.Combine(models, "Clash report L3.pdf");
        File.WriteAllText(report, "%PDF");
        string sub = Path.Combine(models, "Level 3");
        Directory.CreateDirectory(sub);
        string gone = Path.Combine(models, "Moved away.dwg");

        // An ANSI DROPFILES (fWide 0), as some older apps write it.
        var narrow = new MemoryStream();
        narrow.Write(BitConverter.GetBytes(20u));
        narrow.Write(new byte[16]);
        narrow.Write(Encoding.ASCII.GetBytes(report + "\0\0"));
        Check.Sequence([report], OleDropData.ParseDropFiles(narrow.ToArray(), 20, out int narrowCount)!.ToList(), "an ANSI file list");
        Check.Equal(1, narrowCount, "of one");
        Check.Equal(null, OleDropData.ParseDropFiles(new byte[] { 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0 }, 20, out _), "a list that points past its end is no list");

        // Explorer's CF_HDROP, in this process (SetData) and from another (OLE).
        foreach (var data in new[]
        {
            new DataObject(DataFormats.FileDrop, new[] { report, sub, gone }),
            new OleSource().Bytes(DataFormats.FileDrop, OleSource.DropFiles([report, sub, gone])).ToData(),
        })
        {
            var preview = s.Preview(data);
            Check.Equal(DropFamily.Files, preview.Family, "seen as files");
            Check.Equal(3, preview.Count, "counted without looking at the disk");
            Check.Equal(DragDropEffects.Link, DropModule.Effect(preview, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link), "linked, not copied");

            var o = s.Take(data);
            Check.Sequence(["Clash report L3", "Level 3"], Texts(o), "named after them, without the extension; the missing one left out");
            Check.Equal("Added 2 tasks", o.Message, "the toast");
            Check.True(s.Ws.Open[0] == o.Added[0] && s.Ws.Open[1] == o.Added[1], "first on top, in order");
            var file = s.Store.For(o.Added[0].Id)!;
            Check.Equal(new Attachment(AttachmentKind.File, report, "Clash report L3.pdf"), file, "the file, where it is");
            Check.Equal(new Attachment(AttachmentKind.Folder, sub, "Level 3"), s.Store.For(o.Added[1].Id), "the folder, where it is");
            Check.Equal("Open Clash report L3.pdf", o.Added[0].AttachmentTip, "the paperclip says what it opens");
            Check.True(File.Exists(s.Store.FilePath), "attachments.json written");
            Check.True(!Directory.Exists(s.Store.Folder), "nothing copied for files on disk");
        }
        // Synced task text is all other devices see.
        string json = Encoding.UTF8.GetString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Services.StateFile.ToDocument(s.Ws), Services.StateJsonContext.Default.StateDocument));
        Check.True(!json.Contains("Clash report L3.pdf", StringComparison.Ordinal) && !json.Contains(models.Replace("\\", "\\\\"), StringComparison.Ordinal), "tack.json has the task's text, never the path");
    }

    private static void Emails()
    {
        using var s = new Setup();
        string msgSource = Path.Combine(s.Folder, "source.msg");
        byte[] eml = Eml("=?utf-8?B?Q2/DtnJkaW5hdGlvbiBtZWV0aW5n?= =?iso-8859-1?Q?_=E9tage_3?=");
        var source = new OleSource()
            .Bytes(DropFormats.DescriptorW, OleSource.Descriptor(("RE Clash report L3.msg", null, false), ("Coordination meeting.eml", eml.Length, false)))
            .Storage(DropFormats.Contents, () => MsgStorage(msgSource, "RE: Clash report L3"), 0)
            .StreamOf(DropFormats.Contents, eml, 1)
            .Wide(DropFormats.UnicodeText, "From\tSubject\tReceived\nBIM\tRE: Clash report L3\tToday");

        var data = source.ToData();
        var preview = s.Preview(data);
        Check.Equal(DropFamily.VirtualFiles, preview.Family, "virtual files win over the text Outlook offers too");
        Check.Equal(DragDropEffects.Copy, DropModule.Effect(preview, DragDropEffects.Copy | DragDropEffects.Move), "a copy, never a move");
        Check.True(!source.Asked.Any(a => a.Format == DropFormats.Contents), "nothing fetched while it's only dragged over");

        var o = s.Take(data);
        Check.Sequence(["RE: Clash report L3", "Coördination meeting étage 3"], Texts(o), "named by their subjects");
        Check.True(source.Asked.Contains((DropFormats.Contents, 0)) && source.Asked.Contains((DropFormats.Contents, 1)), "each fetched by its index");
        for (int i = 0; i < 2; i++)
        {
            var a = s.Store.For(o.Added[i].Id)!;
            Check.True(a.Saved && a.Kind == AttachmentKind.File, $"{i}: a saved copy");
            string path = AttachmentRules.SavedPath(s.Store.Folder, a.Target)!;
            Check.True(path.StartsWith(s.Store.Folder + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(path), $"{i}: in the attachments folder ({path})");
            Check.Equal("Open email", o.Added[i].AttachmentTip, $"{i}: the paperclip opens the email");
            Check.True(Zone(path) == 3 || !new DriveInfo(path[..1]).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase),
                $"{i}: marked as from outside this PC");
        }
        var emlCopy = AttachmentRules.SavedPath(s.Store.Folder, s.Store.For(o.Added[1].Id)!.Target)!;
        Check.Sequence(eml, File.ReadAllBytes(emlCopy), "the .eml as it was");
        Check.Equal("Coordination meeting.eml", Path.GetFileName(emlCopy), "under the name it was dragged with");
        Check.Equal("RE: Clash report L3", DesktopDrop.MsgSubject(AttachmentRules.SavedPath(s.Store.Folder, s.Store.For(o.Added[0].Id)!.Target)!), "the .msg a real compound file");

        // An older app's ANSI list of virtual files.
        var ansi = new byte[4 + 332];
        BitConverter.GetBytes(1u).CopyTo(ansi, 0);
        BitConverter.GetBytes(0x04u).CopyTo(ansi, 4);
        BitConverter.GetBytes(0x80u).CopyTo(ansi, 4 + 36);
        Encoding.ASCII.GetBytes("Minutes.eml").CopyTo(ansi, 4 + 72);
        var old = s.Take(new OleSource().Bytes(DropFormats.Descriptor, ansi).StreamOf(DropFormats.Contents, Eml("Minutes of the BIM meeting"), 0).ToData());
        Check.Sequence(["Minutes of the BIM meeting"], Texts(old), "an ANSI descriptor too");

        // The same email in this process: one virtual file, as WPF holds it.
        var local = new DataObject();
        local.SetData(DropFormats.DescriptorW, new MemoryStream(OleSource.Descriptor(("Site visit.eml", null, false))));
        local.SetData(DropFormats.Contents, new MemoryStream(Eml("Site visit, Friday")));
        var one = s.Take(local);
        Check.Sequence(["Site visit, Friday"], Texts(one), "in this process too");
        Check.True(s.Store.For(one.Added[0].Id) is { Saved: true }, "saved");
    }

    private static void TemporaryFiles()
    {
        string temp = TempDir();
        string msg = Path.Combine(temp, "Weekly coordination.eml");
        File.WriteAllBytes(msg, Eml("Weekly coordination: L3 clashes"));
        string tool = Path.Combine(temp, "setup.exe");
        File.WriteAllBytes(tool, [0x4D, 0x5A]);
        using var s = new Setup(Ctx(temporary: p => p.StartsWith(temp, StringComparison.OrdinalIgnoreCase)));
        var o = s.Take(new DataObject(DataFormats.FileDrop, new[] { msg, tool }));
        Check.Sequence(["Weekly coordination: L3 clashes", "setup"], Texts(o), "named by the subject; the program by its name");
        var a = s.Store.For(o.Added[0].Id)!;
        Check.True(a.Saved, "copied into the attachments folder");
        File.Delete(msg);
        Check.True(File.Exists(AttachmentRules.SavedPath(s.Store.Folder, a.Target)!), "and still there once the original is cleaned away");
        Check.Equal(null, s.Store.For(o.Added[1].Id), "a program from a temporary folder is neither copied nor linked");
        Check.Equal(1, o.NotKept, "and the toast says one wasn't kept");
        try { Directory.Delete(temp, recursive: true); } catch { }

        // Where this PC's apps keep files they clean away (only the path is looked at).
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var (path, temporary) in new[]
        {
            (Path.Combine(Path.GetTempPath(), "chrome_drag1234", "Clash report.eml"), true),
            (Path.Combine(local, "Packages", "Microsoft.OutlookForWindows_8wekyb3d8bbwe", "LocalCache", "x", "Clash report.eml"), true),
            (Path.Combine(local, "Microsoft", "Olk", "Attachments", "Model review.pdf"), true),
            (Path.Combine(local, "Microsoft", "Windows", "INetCache", "Content.Outlook", "AB12CD34", "Minutes.docx"), true),
            (Path.Combine(local, "SomeApp", "cache", "Weekly.msg"), true),
            (Path.Combine(local, "SomeApp", "Model review.pdf"), false),
            (Path.Combine(documents, "Riverside", "Weekly.msg"), false),
            (Path.Combine(documents, "Riverside", "Clash report L3.pdf"), false),
            (@"\\fileserver\Projects\Weekly.msg", false),
        })
            Check.Equal(temporary, DesktopDrop.IsTemporary(path), $"temporary: {path}");
    }

    private static void Links()
    {
        using var s = new Setup();
        // Chromium/Edge: the address, the address with its title, and a .url file for the desktop.
        var chromium = new OleSource()
            .Wide(DropFormats.UrlW, "https://acc.autodesk.com/docs/files/projects/riverside")
            .Wide(DropFormats.MozUrl, "https://acc.autodesk.com/docs/files/projects/riverside\nRiverside Hospital – Files")
            .Bytes(DropFormats.DescriptorW, OleSource.Descriptor(("Riverside Hospital – Files.url", null, false)))
            .Bytes(DropFormats.Contents, Encoding.ASCII.GetBytes("[InternetShortcut]\r\nURL=https://acc.autodesk.com/\r\n"), 0)
            .Wide(DropFormats.UnicodeText, "https://acc.autodesk.com/docs/files/projects/riverside");
        var preview = s.Preview(chromium.ToData());
        Check.Equal(DropFamily.Links, preview.Family, "a link, not its .url file");
        var o = s.Take(chromium.ToData());
        Check.Sequence(["Riverside Hospital – Files"], Texts(o), "the page's title");
        Check.Equal(new Attachment(AttachmentKind.Link, "https://acc.autodesk.com/docs/files/projects/riverside", "Riverside Hospital – Files"), s.Store.For(o.Added[0].Id), "the address kept");
        Check.Equal("Open acc.autodesk.com", o.Added[0].AttachmentTip, "the paperclip names the site, whatever the title says");
        Check.True(!Directory.Exists(s.Store.Folder), "no .url file saved");

        // Just an address (no title offered): the address is the task.
        var bare = new DataObject();
        bare.SetData(DropFormats.UrlW, new MemoryStream(Encoding.Unicode.GetBytes("http://intranet.local/wiki/Clash_rules\0")));
        Check.Sequence(["http://intranet.local/wiki/Clash_rules"], Texts(s.Take(bare)), "the address, when there's no title");

        // An older browser's ANSI address.
        var ansi = new OleSource().Bytes(DropFormats.Url, Encoding.ASCII.GetBytes("https://bim360.example/model\0")).ToData();
        Check.Sequence(["https://bim360.example/model"], Texts(s.Take(ansi)), "the ANSI address too");

        // A title from the .url file's name when that's all there is.
        var named = new OleSource()
            .Wide(DropFormats.UrlW, "https://example.com/a")
            .Bytes(DropFormats.DescriptorW, OleSource.Descriptor(("Model checker.url", null, false)));
        Check.Sequence(["Model checker"], Texts(s.Take(named.ToData())), "or the shortcut's name");

        // Firefox's list of links: address and title, twice; and text/uri-list.
        var two = new DataObject();
        two.SetData(DropFormats.MozUrl, new MemoryStream(Encoding.Unicode.GetBytes("https://a.example/\nFirst\nhttps://b.example/\nSecond")));
        Check.Sequence(["First", "Second"], Texts(s.Take(two)), "several links");
        var list = new DataObject();
        list.SetData(DropFormats.UriList, new MemoryStream(Encoding.UTF8.GetBytes("# from a list\r\nhttps://c.example/\r\nhttps://c.example/\r\n")));
        Check.Sequence(["https://c.example/"], Texts(s.Take(list)), "a uri-list, twice the same address once");
    }

    private static void Text()
    {
        using var s = new Setup();
        foreach (var data in new System.Windows.IDataObject[]
        {
            new DataObject(DataFormats.UnicodeText, "- Check the clash report\r\n- Send the model\r\n\r\n3. Book the room"),
            new OleSource().Wide(DropFormats.UnicodeText, "- Check the clash report\n- Send the model\n\n3. Book the room").ToData(),
            new OleSource().Bytes(DropFormats.Text, Encoding.ASCII.GetBytes("- Check the clash report\n- Send the model\n3. Book the room\0")).ToData(),
        })
        {
            Check.Equal(DragDropEffects.Copy, DropModule.Effect(s.Preview(data), DragDropEffects.Copy | DragDropEffects.Move), "copied: the text stays where it was");
            var o = s.Take(data);
            Check.Sequence(["Check the clash report", "Send the model", "Book the room"], Texts(o), "a line each, markers off");
            Check.True(o.Added.All(t => s.Store.For(t.Id) == null && t.AttachmentTip == null), "nothing attached");
        }
        Check.True(!File.Exists(s.Store.FilePath), "attachments.json only once there's an attachment");
        var one = s.Take(new DataObject(DataFormats.UnicodeText, "Call the plumber"));
        Check.Equal("Added a task", one.Message, "one task");
    }

    private static void SpaceAndUndo()
    {
        using var s = new Setup();
        var work = s.Ws.AddSpace("Work");
        string file = Path.Combine(s.Folder, "Clash report L3.pdf");
        File.WriteAllText(file, "x");
        var o = s.Take(new DataObject(DataFormats.FileDrop, new[] { file }), work.Id);
        Check.Equal(work.Id, o.Added[0].Space, "in the space on show");
        o.Undo!();
        Check.True(o.Added.All(t => t.IsDeleted), "Undo deletes them");
        Check.True(s.Store.For(o.Added[0].Id) != null, "the attachment waits until that Undo can't come back");
        Check.Equal(1, s.Intake.Prune(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), "then goes");
        Check.Equal(null, o.Added[0].AttachmentTip, "and the paperclip with it");
    }

    private static void NeverMove()
    {
        var files = new DropRead(DropFamily.Files, [new DropItem(ItemKind.File, "x")]);
        var text = new DropRead(DropFamily.Text, [new DropItem(ItemKind.Text, "x")]);
        var mail = new DropRead(DropFamily.VirtualFiles, [new DropItem(ItemKind.Copy, "x")]);
        Check.Equal(DragDropEffects.None, DropModule.Effect(text, DragDropEffects.Move), "a source that only allows Move gets nothing");
        Check.Equal(DragDropEffects.Copy, DropModule.Effect(mail, DragDropEffects.Move | DragDropEffects.Copy), "an email is copied");
        Check.Equal(DragDropEffects.Link, DropModule.Effect(files, DragDropEffects.Move | DragDropEffects.Link), "files are linked");
        Check.Equal(DragDropEffects.Copy, DropModule.Effect(files, DragDropEffects.Move | DragDropEffects.Copy), "or copied");
        Check.Equal(DragDropEffects.None, DropModule.Effect(DropRead.None, DragDropEffects.All), "nothing Tack knows");
        Check.Equal(DragDropEffects.None, DropModule.Effect(new DropRead(DropFamily.Files, [], DropReader.TooMany), DragDropEffects.All), "a refused drag");
    }

    // ================= Hostile drags =================

    private static void HugeLists()
    {
        using var s = new Setup();
        var clock = Stopwatch.StartNew();
        void Refused(System.Windows.IDataObject data, string what, string message)
        {
            var preview = s.Preview(data);
            Check.Equal(message, preview.Refusal, $"{what}: refused while dragged over");
            Check.Equal(DragDropEffects.None, DropModule.Effect(preview, DragDropEffects.All), $"{what}: no drop");
            var o = s.Take(data);
            Check.True(o.Refused && o.Added.Count == 0, $"{what}: and on a drop anyway, nothing added");
        }

        var many = Enumerable.Range(0, 100_000).Select(i => $@"C:\a{i}").ToList();
        Refused(new OleSource().Bytes(DataFormats.FileDrop, OleSource.DropFiles(many)).ToData(), "100,000 short paths", DropReader.TooMany);
        Refused(new OleSource().Bytes(DataFormats.FileDrop, OleSource.DropFiles(many.Select(p => p + new string('x', 200)))).ToData(), "a 40 MB file list", DropReader.TooMany);
        Refused(new DataObject(DataFormats.FileDrop, Enumerable.Range(0, 21).Select(i => $@"C:\f{i}.txt").ToArray()), "21 files", DropReader.TooMany);
        Refused(new OleSource().Bytes(DropFormats.DescriptorW, OleSource.Descriptor(uint.MaxValue, ("a.eml", 1, false))).ToData(), "a descriptor claiming 4 billion files", DropReader.TooMany);
        Refused(new OleSource().Bytes(DropFormats.DescriptorW, OleSource.Descriptor(Enumerable.Range(0, 21).Select(i => ($"m{i}.eml", (long?)1, false)).ToArray())).ToData(), "21 emails", DropReader.TooMany);
        Refused(new OleSource().Wide(DropFormats.MozUrl, string.Join("\n", Enumerable.Range(0, 10_000).Select(i => $"https://x.example/{i}\nt{i}"))).ToData(), "10,000 links", DropReader.TooMany);
        Refused(new OleSource().Bytes(DropFormats.UriList, Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Range(0, 50).Select(i => $"https://x.example/{i}")))).ToData(), "a uri-list of 50", DropReader.TooMany);
        Check.True(clock.ElapsedMilliseconds < 5000, $"all of it quickly ({clock.ElapsedMilliseconds} ms)");
        Check.Equal(0, s.Ws.Open.Count, "not one task");

        // 20 is fine; folders in a descriptor don't count.
        var twenty = OleSource.Descriptor(Enumerable.Range(0, 20).Select(i => ($"Folder\\m{i}.eml", (long?)5, false)).Prepend(("Folder", null, true)).ToArray());
        var src = new OleSource().Bytes(DropFormats.DescriptorW, twenty);
        for (int i = 1; i <= 20; i++) src.StreamOf(DropFormats.Contents, Eml($"Mail {i}"), i);
        var o = s.Take(src.ToData());
        Check.Equal(20, o.Added.Count, "twenty emails in a folder: twenty tasks");
        Check.Equal("Mail 1", o.Added[0].Text, "in order");
    }

    private static void BadLinks()
    {
        using var s = new Setup();
        foreach (string bad in new[]
        {
            "javascript:alert(document.cookie)", "JaVaScRiPt:alert(1)", " javascript:alert(1)", "vbscript:msgbox(1)",
            "data:text/html,<script>alert(1)</script>", "file:///C:/Windows/System32/calc.exe", @"file://\\evil\share\x.exe",
            @"\\evil\share\x.exe", "ms-settings:privacy", "search-ms:query=x", "https://user:secret@acc.autodesk.com/", "http://google.com@evil.example/",
            "https://exa mple.com/", "ftp://files.example/", "acc.autodesk.com",
        })
        {
            var data = new OleSource().Wide(DropFormats.UrlW, bad).Wide(DropFormats.MozUrl, bad + "\nTrusted title").ToData();
            Check.True(s.Preview(data).Family != DropFamily.Links, $"'{bad}': not a link");
            var o = s.Take(data);
            Check.Equal(0, o.Added.Count, $"'{bad}': nothing made from it");

            // With the text a browser offers beside it, it's text: a task that never opens anything.
            var withText = new OleSource().Wide(DropFormats.UrlW, bad).Wide(DropFormats.UnicodeText, bad).ToData();
            var t = s.Take(withText);
            Check.True(t.Added.All(x => s.Store.For(x.Id) == null && x.AttachmentTip == null), $"'{bad}': as text, nothing attached");
        }
        var twoLines = new OleSource().Wide(DropFormats.UrlW, "https://example.com/\nhttps://evil.example/").ToData();
        Check.Equal(0, s.Take(twoLines).Added.Count, "an address with a line break in it is no address");
        Check.Equal(null, AttachmentRules.CleanUrl("https://acc.autodesk.com/" + new string('a', 2100)), "too long");
        Check.True(AttachmentRules.CleanUrl("HTTPS://ACC.autodesk.com/Docs") != null, "any case of http(s)");
    }

    private static void Paths()
    {
        var probe = new Probe();
        using var s = new Setup(Ctx(probe));
        string[] paths =
        [
            @"\\fileserver\Projects\Riverside\Clash report L3.pdf",
            @"\\fileserver\Projects\Riverside\Phase 2.01",
            @"//fileserver/Projects/Riverside/Coordination.nwd",
            @"\\?\C:\Windows\System32\cmd.exe",
            @"\\?\UNC\evil\share\x.exe",
            @"\\.\pipe\spoolss",
            @"\\.\PhysicalDrive0",
            @"\??\C:\Windows\notepad.exe",
            @"\\evil",
            @"\\evil\",
            @"C:\Models\report.pdf:hidden.exe",
            @"C:\Models\..\Windows\notepad.exe",
            @"Models\relative.pdf",
            @"C:relative.pdf",
            "C:\\Models\\line\nbreak.pdf",
            @"C:\Models\what?.pdf",
            @"C:\" + new string('a', 3000) + ".pdf",
            "",
        ];
        var o = s.Take(new DataObject(DataFormats.FileDrop, paths));
        Check.Sequence(["Clash report L3", "Phase 2.01", "Coordination"], Texts(o), "network paths kept, nothing else");
        Check.Equal(0, probe.Asked.Count(p => WinPath.IsUnc(p)), "the server never asked");
        Check.Equal(new Attachment(AttachmentKind.File, @"\\fileserver\Projects\Riverside\Clash report L3.pdf", "Clash report L3.pdf"), s.Store.For(o.Added[0].Id), "a file on the share");
        Check.Equal(AttachmentKind.Folder, s.Store.For(o.Added[1].Id)!.Kind, "a name without a file's extension is a folder");
        Check.Equal(@"\\fileserver\Projects\Riverside\Coordination.nwd", s.Store.For(o.Added[2].Id)!.Target, "forward slashes made Windows ones");
        foreach (string p in paths.Skip(3))
            Check.Equal(null, AttachmentRules.CleanPath(p), $"never kept: {p[..Math.Min(p.Length, 40)]}");
    }

    private static void ManyLines()
    {
        using var s = new Setup();
        foreach (var (text, what) in new[]
        {
            (string.Join("\n", Enumerable.Range(0, 10_000).Select(i => $"Line {i}")), "10,000 lines"),
            (string.Join("\r\n", Enumerable.Range(0, 21).Select(i => $"Line {i}")), "21 lines"),
            (new string('x', 1_100_000), "2 MB on one line"),
        })
        {
            var data = new DataObject(DataFormats.UnicodeText, text);
            Check.Equal(DropReader.TooManyLines, s.Preview(data).Refusal, $"{what}: refused");
            Check.Equal(0, s.Take(data).Added.Count, $"{what}: nothing added");
        }
        var ok = s.Take(new DataObject(DataFormats.UnicodeText, string.Join("\n", Enumerable.Range(1, 20).Select(i => $"Line {i}")) + "\n\n\n"));
        Check.Equal(20, ok.Added.Count, "20 lines (blank ones don't count)");
        var longLine = s.Take(new DataObject(DataFormats.UnicodeText, "Short\n" + new string('y', DeviceFile.MaxTaskText + 1)));
        Check.Sequence(["Short"], Texts(longLine), "a line longer than sync carries is left out, as with paste");
    }

    private static void HostileNames()
    {
        using var s = new Setup();
        string outside = Path.Combine(s.Folder, "evil.txt");
        var src = new OleSource()
            .Bytes(DropFormats.DescriptorW, OleSource.Descriptor(
                (@"..\..\evil.txt", 3, false),
                ("C:\\Windows\\win.ini", 3, false),
                ("invoice\u202Efdp.exe", 3, false),
                ("report.pdf.exe", 3, false),
                ("CON.eml", 3, false),
                ("notes:hidden.txt", 3, false),
                ("..", 3, false),
                ("run.lnk", 3, false),
                ("Model review.pdf", 3, false)))
            .StreamOf(DropFormats.Contents, "abc"u8.ToArray(), 0)
            .StreamOf(DropFormats.Contents, "abc"u8.ToArray(), 1)
            .StreamOf(DropFormats.Contents, "MZ!"u8.ToArray(), 2)
            .StreamOf(DropFormats.Contents, "MZ!"u8.ToArray(), 3)
            .StreamOf(DropFormats.Contents, "abc"u8.ToArray(), 4)
            .StreamOf(DropFormats.Contents, "abc"u8.ToArray(), 5)
            .StreamOf(DropFormats.Contents, "abc"u8.ToArray(), 6)
            .StreamOf(DropFormats.Contents, "abc"u8.ToArray(), 7)
            .StreamOf(DropFormats.Contents, "abc"u8.ToArray(), 8);
        var o = s.Take(src.ToData());
        Check.Equal(9, o.Added.Count, "a task for each");
        Check.True(!File.Exists(outside) && !File.Exists(Path.Combine(Path.GetDirectoryName(s.Folder)!, "evil.txt")), "nothing written outside the attachments folder");

        var saved = o.Added.Select(t => s.Store.For(t.Id)).ToList();
        Check.Equal("evil.txt", Path.GetFileName(saved[0]!.Target), "..\\..\\ goes");
        Check.Equal("win.ini", Path.GetFileName(saved[1]!.Target), "so does a drive");
        Check.True(saved[2] == null && saved[3] == null && saved[7] == null, "programs aren't kept, however they're named");
        Check.True(!o.Added[2].Text.Contains('\u202E'), "and a name can't turn itself around");
        Check.Equal("_CON.eml", Path.GetFileName(saved[4]!.Target), "a device name is never a file's");
        Check.Equal("noteshidden.txt", Path.GetFileName(saved[5]!.Target), "no streams inside a file");
        Check.Equal("Untitled", Path.GetFileName(saved[6]!.Target), "a name that's nothing at all");
        Check.Equal("Model review.pdf", Path.GetFileName(saved[8]!.Target), "an ordinary name as it was");
        foreach (var a in saved.Where(a => a != null))
        {
            string full = AttachmentRules.SavedPath(s.Store.Folder, a!.Target)!;
            Check.True(Path.GetDirectoryName(Path.GetDirectoryName(full)) == s.Store.Folder, $"one folder down in the attachments folder ({a.Target})");
        }
        Check.True(Directory.EnumerateFiles(s.Store.Folder, "*", SearchOption.AllDirectories).All(f => !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)), "no program on disk");
    }

    private static void HostileSizes()
    {
        using var s = new Setup();
        var endless = new EndlessComStream();
        var src = new OleSource()
            .Bytes(DropFormats.DescriptorW, OleSource.Descriptor(("Endless.eml", null, false), ("Claims 2 GB.eml", 2L << 30, false), ("Small.eml", 10, false)))
            .Stream(DropFormats.Contents, () => endless, 0)
            .Stream(DropFormats.Contents, () => new EndlessComStream(), 1)
            .StreamOf(DropFormats.Contents, Eml("Small one"), 2);
        var o = s.Take(src.ToData());
        Check.Sequence(["Endless", "Claims 2 GB", "Small one"], Texts(o), "a task for each");
        Check.True(endless.Served <= DropLimits.MaxSavedBytes + 200_000, $"the endless one read only up to the cap ({endless.Served:N0})");
        Check.True(!src.Asked.Contains((DropFormats.Contents, 1)), "the one claiming 2 GB never read");
        Check.True(s.Store.For(o.Added[0].Id) == null && s.Store.For(o.Added[1].Id) == null, "neither kept");
        Check.True(s.Store.For(o.Added[2].Id) is { Saved: true }, "the small one is");
        Check.Equal(1, Directory.GetDirectories(s.Store.Folder).Length, "and nothing else left behind");
        Check.Equal(2, o.NotKept, "two not kept");
        Check.True(o.Note!.Contains("50 MB", StringComparison.Ordinal), $"and why ({o.Note})");
    }

    // ================= Attachments =================

    private static void Caps()
    {
        using var s = new Setup();
        // The folder nearly full: a copy that doesn't fit isn't kept; the task is still made.
        string big = Path.Combine(s.Store.Folder, "0123456789ab");
        Directory.CreateDirectory(big);
        using (var f = new FileStream(Path.Combine(big, "big.bin"), FileMode.Create)) f.SetLength(DropLimits.MaxFolderBytes - 5);
        var src = new OleSource()
            .Bytes(DropFormats.DescriptorW, OleSource.Descriptor(("Over.eml", null, false)))
            .StreamOf(DropFormats.Contents, Eml("Over the top"), 0);
        var o = s.Take(src.ToData());
        Check.Sequence(["Over"], Texts(o), "the task, named by the file (the copy never made)");
        Check.Equal(null, s.Store.For(o.Added[0].Id), "without its copy");
        Check.True(o.Note!.Contains("full", StringComparison.Ordinal), $"said so ({o.Note})");
        Check.Equal(1, Directory.GetDirectories(s.Store.Folder).Length, "no partial copy");
        Directory.Delete(big, recursive: true);

        // The store full: tasks without attachments.
        string file = Path.Combine(s.Folder, "a.pdf");
        File.WriteAllText(file, "x");
        for (int i = 0; s.Store.Count < DropLimits.MaxAttachments; i++)
            s.Store.Add($"t{i:x10}", new Attachment(AttachmentKind.File, file, "a.pdf"));
        var full = s.Take(new DataObject(DataFormats.FileDrop, new[] { file }));
        Check.Equal(1, full.Added.Count, "the task");
        Check.Equal(null, s.Store.For(full.Added[0].Id), "without the attachment");
        Check.Equal(DropLimits.MaxAttachments, s.Store.Count, "never more than the cap");
    }

    private static void LettingGo()
    {
        using var s = new Setup();
        var src = new OleSource()
            .Bytes(DropFormats.DescriptorW, OleSource.Descriptor(("Keep.eml", null, false), ("Delete.eml", null, false), ("Done.eml", null, false)))
            .StreamOf(DropFormats.Contents, Eml("Keep"), 0)
            .StreamOf(DropFormats.Contents, Eml("Delete"), 1)
            .StreamOf(DropFormats.Contents, Eml("Done"), 2);
        var o = s.Take(src.ToData());
        var (keep, delete, done) = (o.Added[0], o.Added[1], o.Added[2]);
        string deleted = AttachmentRules.SavedPath(s.Store.Folder, s.Store.For(delete.Id)!.Target)!;
        s.Ws.Complete(done);
        s.Ws.Delete(delete);
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Check.True(DropIntake.Keep(s.Ws, keep.Id, now) && DropIntake.Keep(s.Ws, done.Id, now), "open and done tasks keep theirs");
        Check.True(!DropIntake.Keep(s.Ws, delete.Id, now), "a task deleted here lets go (once no Undo is on offer)");
        Check.Equal(1, s.Intake.Prune(now), "one let go");
        Check.True(!File.Exists(deleted) && !Directory.Exists(Path.GetDirectoryName(deleted)), "its saved copy and folder gone");
        Check.True(s.Store.For(keep.Id) != null && s.Store.For(done.Id) != null, "the others stay");
        var reloaded = new AttachmentStore(s.Folder, DesktopDrop.IsProgram);
        reloaded.Load();
        Check.Equal(2, reloaded.Count, "and attachments.json says so");

        // A peer's delete: held for a while (its Undo may be on its way), then let go.
        var peer = NewWorkspace();
        var task = peer.AddTask("From the phone");
        var data = peer.ToData();
        var t = data.Tasks[0];
        string stamp = Stamp.Encode((long)now - 60_000, 0, "phone001");
        data = data with { Tasks = [t with { Deleted = true, Clk = t.Clk with { Deleted = stamp } }] };
        var ws = new Workspace(new Settings(), new Hlc("desk0001"), data);
        Check.True(DropIntake.Keep(ws, task.Id, now), "a minute after another device deleted it: kept");
        Check.True(!DropIntake.Keep(ws, task.Id, now + DropIntake.PeerGrace.TotalMilliseconds), "past the grace: let go");
        Check.True(!DropIntake.Keep(ws, "f00000000000", now), "a task this PC no longer knows: let go");
        Check.True(!DropIntake.Keep(NewWorkspace(), "f00000000000", now), "even when it knows no task at all (tack.json read, and empty)");
        Check.True(DropIntake.Keep(new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty) { LoadFailed = true }, "f00000000000", now),
            "unless tack.json couldn't be read");
    }

    private static void KeptWhileTackJsonUnreadable()
    {
        Ui.Run(_ =>
        {
            string folder = TempDir();
            try
            {
                var state = new StateStore(folder);
                var ws = state.Attach(state.Load());
                var task = ws.AddTask("RFI 112: slab edge at grid 7");
                state.Flush();
                // An email kept by a drop from Outlook, three days ago, and a copy nothing names (an orphan).
                var store = new AttachmentStore(folder, DesktopDrop.IsProgram);
                string mail = Path.Combine(store.Folder, "0123456789ab", "RFI 112.msg"), orphan = Path.Combine(store.Folder, "aaaaaaaaaaaa");
                Directory.CreateDirectory(Path.GetDirectoryName(mail)!);
                File.WriteAllText(mail, "pretend .msg");
                Directory.CreateDirectory(orphan);
                File.WriteAllText(Path.Combine(orphan, "lost.eml"), "x");
                Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-3));
                Check.True(store.Add(task.Id, Attachment.Clean(AttachmentKind.File, "0123456789ab\\RFI 112.msg", "RFI 112.msg", saved: true, DesktopDrop.IsProgram)!), "kept");
                store.Save();
                ws.AddTask("Second");
                state.Flush(); // tack.json.bak too

                File.WriteAllText(state.FilePath, "{");
                File.WriteAllText(state.FilePath + ".bak", "{");
                var lost = new StateStore(folder);
                var empty = lost.Attach(lost.Load());
                Check.True(empty.LoadFailed, "started empty: tack.json couldn't be read");
                var view = new SpaceView(empty);
                var module = new DropModule(folder, empty, view, new DropContext { Probe = Launcher.Core.RealFileProbe.Instance, IsProgram = DesktopDrop.IsProgram });
                module.Prune();
                module.SweepOrphans();
                Check.True(module.Store.Count == 1 && File.Exists(mail), "at start: the attachment and its email stay");
                Check.True(Directory.Exists(orphan), "and nothing is swept");
                empty.AddTask("Call the site office"); // carrying on
                module.Prune(); // an Undo lapsing, or midnight
                Check.True(module.Store.Count == 1 && File.Exists(mail), "nor later in the session, with tasks in the list again");
                var reloaded = new AttachmentStore(folder, DesktopDrop.IsProgram);
                reloaded.Load();
                Check.True(reloaded.For(task.Id) != null, "attachments.json still says so");
                lost.Flush(); // nothing left waiting to be saved once the folder goes
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
            }
        });
    }

    private static void SweepAndRoundTrip()
    {
        using var s = new Setup();
        var src = new OleSource()
            .Bytes(DropFormats.DescriptorW, OleSource.Descriptor(("Kept.eml", null, false)))
            .StreamOf(DropFormats.Contents, Eml("Kept"), 0);
        var o = s.Take(src.ToData());
        string orphan = Path.Combine(s.Store.Folder, "aaaaaaaaaaaa");
        string mine = Path.Combine(s.Store.Folder, "My notes");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "lost.eml"), "x");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "keep.txt"), "x");
        Check.Equal(0, s.Store.SweepOrphans(DateTime.UtcNow.AddMinutes(-10)), "a fresh one waits");
        Check.Equal(1, s.Store.SweepOrphans(DateTime.UtcNow.AddMinutes(1)), "an old one goes");
        Check.True(!Directory.Exists(orphan) && Directory.Exists(mine) && File.Exists(AttachmentRules.SavedPath(s.Store.Folder, s.Store.For(o.Added[0].Id)!.Target)!),
            "only Tack's own, and never one in use");

        string file = Path.Combine(s.Folder, "Plan ü.pdf");
        File.WriteAllText(file, "x");
        var more = s.Take(new DataObject(DataFormats.FileDrop, new[] { file }));
        var copy = new AttachmentStore(s.Folder, DesktopDrop.IsProgram);
        copy.Load();
        Check.Sequence(s.Store.All.ToList(), copy.All.ToList(), "attachments.json reads back what it wrote");
        Check.True(File.Exists(s.Store.FilePath + ".bak"), "and keeps the one before");
        Check.Equal(2, copy.Count, "both");
    }

    private static void Opening()
    {
        string folder = TempDir();
        string present = Path.Combine(folder, "Clash report L3.pdf");
        File.WriteAllText(present, "x");
        var probe = new Probe();
        bool IsProgram(string ext) => DesktopDrop.IsProgram(ext);
        LaunchPlan? Plan(Attachment a, out string? problem) => AttachmentRules.Plan(a, Path.Combine(folder, AttachmentStore.FolderName), probe, IsProgram, out problem);

        var link = Plan(new Attachment(AttachmentKind.Link, "https://acc.autodesk.com/docs", "ACC"), out _);
        Check.Equal("https://acc.autodesk.com/docs", link?.FileName, "a page opens in the browser");
        Check.True(link!.Arguments == null && link.WorkingDirectory == null, "with nothing beside it");
        Check.Equal(null, Plan(new Attachment(AttachmentKind.Link, "javascript:alert(1)", "x"), out var p1), "a hand-edited javascript: never");
        Check.Equal("Tack can't open this address.", p1, "and says so");

        Check.Equal(present, Plan(new Attachment(AttachmentKind.File, present, "Clash report L3.pdf"), out _)?.FileName, "a file opens itself");
        Check.Equal(null, Plan(new Attachment(AttachmentKind.File, Path.Combine(folder, "gone.pdf"), "gone.pdf"), out var p2), "a missing file doesn't");
        Check.Equal("This file can't be found. Was it moved or deleted?", p2, "and says so");
        Check.Equal(null, Plan(new Attachment(AttachmentKind.Folder, Path.Combine(folder, "Nowhere"), "Nowhere"), out var p3), "a missing folder");
        Check.Equal("This folder can't be found. Was it moved or deleted?", p3, "says so");
        Check.Equal(folder, Plan(new Attachment(AttachmentKind.Folder, folder, "x"), out _)?.FileName, "a folder opens in Explorer");

        string copies = Path.Combine(folder, AttachmentStore.FolderName, "0123456789ab");
        Directory.CreateDirectory(copies);
        File.WriteAllText(Path.Combine(copies, "Mail.eml"), "x");
        Check.Equal(Path.Combine(copies, "Mail.eml"), Plan(new Attachment(AttachmentKind.File, @"0123456789ab\Mail.eml", "Mail.eml", Saved: true), out _)?.FileName, "a saved copy, from inside the folder");
        Check.Equal(null, Plan(new Attachment(AttachmentKind.File, @"0123456789ab\..\..\x.eml", "x", Saved: true), out _), "never from outside it");
        Check.Equal(null, Plan(new Attachment(AttachmentKind.File, @"0123456789ab\Mail.exe", "Mail.exe", Saved: true), out var p4), "never a saved program");
        Check.True(p4 != null, "and says why");
        Check.Equal(null, Plan(new Attachment(AttachmentKind.File, @"0123456789ab\Other.eml", "Other.eml", Saved: true), out var p5), "a saved copy that's gone");
        Check.Equal("Tack's copy of this can't be found.", p5, "says so");
        Check.Equal(@"\\fileserver\Projects\x.pdf",
            AttachmentRules.Plan(new Attachment(AttachmentKind.File, @"\\fileserver\Projects\x.pdf", "x.pdf"), folder, RealFileProbe.Instance, IsProgram, out _)?.FileName,
            "a network file isn't looked for (the server could stall Tack): Windows says if it's gone");
        try { Directory.Delete(folder, recursive: true); } catch { }
    }
}
