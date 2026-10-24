using System.IO;
using Tack.Drop.Core;
using Tack.Model;
using Tack.Sync;

namespace Tack.Drop;

/// <summary>What a drop did: the tasks it made (first item first), what to say, and how to take it back.</summary>
/// <param name="Note">Why some came without their attachment (the first reason), for the bubble.</param>
/// <param name="NotKept">How many came without the attachment they might have had.</param>
internal sealed record DropOutcome(IReadOnlyList<TaskItem> Added, string? Message, string? Note, Action? Undo, bool Refused = false, int NotKept = 0)
{
    public static readonly DropOutcome Nothing = new([], null, null, null);

    public static DropOutcome Refuse(string why) => new([], why, null, null, Refused: true);
}

/// <summary>
/// A drop onto the orb, from the data to the tasks: one task per item in the space on show, through
/// <see cref="Workspace"/> like any other new task, with what it points to kept beside it on this PC
/// (<see cref="AttachmentStore"/>). An email (or any dragged virtual file) is saved into the attachments
/// folder and the task points to that copy. Undo deletes the tasks; their attachments go once that Undo has
/// lapsed (<see cref="Keep"/>).
/// </summary>
internal sealed class DropIntake
{
    private readonly Workspace _ws;
    private readonly AttachmentStore _store;
    private readonly DropContext _ctx;
    private readonly Action<string> _markFromOutside;

    public DropIntake(Workspace ws, AttachmentStore store, DropContext ctx, Action<string>? markFromOutside = null)
    {
        _ws = ws;
        _store = store;
        _ctx = ctx;
        _markFromOutside = markFromOutside ?? (_ => { });
    }

    public AttachmentStore Store => _store;

    /// <summary>While it's dragged over the orb: what it is and how many, nothing read from disk.</summary>
    public DropRead Preview(IDropData data) => DropReader.Read(data, _ctx, preview: true);

    /// <summary>Makes the tasks (in <paramref name="space"/>), saves any copies and records the attachments.</summary>
    public DropOutcome Take(IDropData data, string space)
    {
        var read = DropReader.Read(data, _ctx);
        if (read.Refusal != null) return DropOutcome.Refuse(read.Refusal);
        if (read.Count == 0) return DropOutcome.Nothing;

        long room = DropLimits.MaxFolderBytes - _store.FolderBytes();
        int slots = _store.LeftAlone ? 0 : DropLimits.MaxAttachments - _store.Count;
        var made = new List<(string Text, Attachment? Attachment)>();
        var notes = new List<(DropNote Note, bool Email)>();
        foreach (var item in read.Items)
        {
            var (text, attachment, note) = item.Kind != ItemKind.Text && slots <= 0
                ? (item.Text, null, _store.LeftAlone ? DropNote.StoreLeftAlone : DropNote.StoreFull)
                : Make(item, data, ref room);
            if (attachment != null) slots--;
            if (note is { } n) notes.Add((n, DropNames.IsEmail(item.Label ?? "")));
            made.Add((text.Length > 0 ? text : "Untitled", attachment));
        }

        // Added last first, so the first item ends up on top (as a pasted list does).
        var added = new TaskItem[made.Count];
        for (int i = made.Count - 1; i >= 0; i--) added[i] = _ws.AddTask(made[i].Text, space: space);
        bool any = false;
        for (int i = 0; i < made.Count; i++)
        {
            if (made[i].Attachment is not { } a || !_store.Add(added[i].Id, a)) continue;
            added[i].AttachmentTip = AttachmentRules.Tip(a);
            any = true;
        }
        if (any) Save();

        string message = added.Length == 1 ? "Added a task" : $"Added {added.Length} tasks";
        return new DropOutcome(added, message, Describe(notes), () => { foreach (var t in added) _ws.Delete(t); }, NotKept: notes.Count);
    }

    private (string Text, Attachment? Attachment, DropNote? Note) Make(DropItem item, IDropData data, ref long room)
    {
        switch (item.Kind)
        {
            case ItemKind.File or ItemKind.Folder or ItemKind.Link:
            {
                var kind = item.Kind switch { ItemKind.Folder => AttachmentKind.Folder, ItemKind.Link => AttachmentKind.Link, _ => AttachmentKind.File };
                var a = Attachment.Clean(kind, item.Target, item.Label, saved: false, _ctx.IsProgram);
                return (item.Text, a, null);
            }
            case ItemKind.Copy:
                return Save(item, data, ref room);
            default:
                return (item.Text, null, item.Note);
        }
    }

    /// <summary>A copy in its own folder in the attachments folder, under the name it was dragged with.</summary>
    private (string Text, Attachment? Attachment, DropNote? Note) Save(DropItem item, IDropData data, ref long room)
    {
        string name = DropNames.SafeFileName(item.Label, "Untitled");
        if (_ctx.IsProgram(Launcher.Core.WinPath.Extension(name))) return (item.Text, null, DropNote.Program);
        long cap = Math.Min(DropLimits.MaxSavedBytes, room);
        if (cap <= 0) return (item.Text, null, DropNote.FolderFull);

        string id = Ids.NewId();
        string folder = Path.Combine(_store.Folder, id);
        string path = Path.Combine(folder, name);
        try
        {
            Directory.CreateDirectory(folder);
            DataRead result;
            if (item.CopyFrom != null)
            {
                long length = new FileInfo(item.CopyFrom).Length;
                if (length > DropLimits.MaxSavedBytes) return Fail(DropNote.TooBig);
                if (length > cap) return Fail(DropNote.FolderFull);
                File.Copy(item.CopyFrom, path); // its own Mark of the Web, if it had one, comes along
                result = DataRead.Ok;
            }
            else
            {
                result = data.SaveContents(item.ContentIndex, path, cap);
                if (result == DataRead.Ok) _markFromOutside(path);
            }
            if (result == DataRead.TooBig) return Fail(cap < DropLimits.MaxSavedBytes ? DropNote.FolderFull : DropNote.TooBig);
            if (result != DataRead.Ok) return Fail(DropNote.Unreadable);

            room -= new FileInfo(path).Length;
            string text = item.Text;
            if (DropNames.IsEmail(name) && _ctx.EmailSubject(path) is { Length: > 0 } subject) text = subject;
            var a = Attachment.Clean(AttachmentKind.File, $"{id}\\{name}", name, saved: true, _ctx.IsProgram);
            if (a == null) return Fail(DropNote.Unreadable);
            return (text, a, null);
        }
        catch (Exception)
        {
            return Fail(DropNote.Unreadable);
        }

        (string, Attachment?, DropNote?) Fail(DropNote note)
        {
            DeleteFolder(folder);
            return (item.Text, null, note);
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (string file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
        catch (Exception) { /* swept up later */ }
    }

    /// <summary>Why an item came without its attachment, the first reason only (the bubble has room for one line).</summary>
    private static string? Describe(List<(DropNote Note, bool Email)> notes)
    {
        if (notes.Count == 0) return null;
        var (first, email) = notes[0];
        int n = notes.Count(x => x.Note == first);
        string what = n == 1 ? (email ? "1 email" : "1 file") : $"{n} files";
        string verb = n == 1 ? "wasn't" : "weren't";
        return first switch
        {
            DropNote.Program => n == 1 ? "A program isn't kept, only its name" : "Programs aren't kept, only their names",
            DropNote.TooBig => $"{what} {verb} kept: over {DropLimits.MaxSavedBytes / (1024 * 1024)} MB",
            DropNote.FolderFull => $"{what} {verb} kept: the attachments folder is full",
            DropNote.StoreFull => $"Tack keeps {DropLimits.MaxAttachments:N0} attachments at most",
            DropNote.StoreLeftAlone => "No paperclip: Tack couldn't read attachments.json",
            _ => $"{what} couldn't be read",
        };
    }

    // ================= Letting attachments go =================

    /// <summary>A peer's Undo can take a while to arrive through OneDrive: its deletions are held this long.</summary>
    internal static readonly TimeSpan PeerGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether a task's attachment stays (asked only while no Undo is on offer, so nothing deleted here can come
    /// back): it stays with a live task (open or done); it goes with a task deleted here, or deleted elsewhere
    /// more than <see cref="PeerGrace"/> ago, or forgotten altogether. All of them stay while tack.json couldn't
    /// be read (<see cref="Workspace.LoadFailed"/>): the tasks they belong to may yet come back, and the
    /// emails kept for them exist nowhere else.
    /// </summary>
    public static bool Keep(Workspace ws, string taskId, double nowMs)
    {
        if (ws.LoadFailed) return true;
        if (ws.TaskById(taskId) is not { } task) return false;
        if (!task.IsDeleted) return true;
        if (Stamp.Node(task.Clk.Deleted) == ws.Clock.Node) return false;
        return nowMs - Stamp.Time(task.Clk.Deleted) < PeerGrace.TotalMilliseconds;
    }

    /// <summary>Lets go of the attachments <see cref="Keep"/> says no to, and their saved copies. Returns how many.</summary>
    public int Prune(double nowMs)
    {
        var gone = _store.Prune(id => Keep(_ws, id, nowMs));
        foreach (string id in gone)
            if (_ws.TaskById(id) is { } t) t.AttachmentTip = null;
        if (gone.Count > 0) Save();
        return gone.Count;
    }

    /// <summary>attachments.json, written now; if that fails, the attachments still work and the next write has them.</summary>
    private void Save()
    {
        try { _store.Save(); }
        catch (Exception ex) { Services.Log.Error("Drop: couldn't save attachments.json", ex); }
    }

    /// <summary>Puts each task's paperclip on (after loading).</summary>
    public void ShowAttachments()
    {
        foreach (var (id, a) in _store.All)
            if (_ws.TaskById(id) is { } t) t.AttachmentTip = AttachmentRules.Tip(a);
    }
}
