using System.IO;
using System.Text.Json;
using Tack.Launcher.Core;
using Tack.Sync;

namespace Tack.Drop.Core;

internal enum AttachmentKind { File, Folder, Link }

/// <summary>
/// What a dropped item left on its task, on this PC only: a file or folder (by its path), a web page, or a copy
/// Tack saved in its attachments folder (an email from Outlook): then <see cref="Target"/> is "&lt;id&gt;\&lt;name&gt;"
/// inside that folder. Other devices only ever see the task's text.
/// </summary>
internal sealed record Attachment(AttachmentKind Kind, string Target, string Label, bool Saved = false)
{
    public static string KindName(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Folder => "folder",
        AttachmentKind.Link => "link",
        _ => "file",
    };

    public static AttachmentKind? ParseKind(string? kind) => kind switch
    {
        "file" => AttachmentKind.File,
        "folder" => AttachmentKind.Folder,
        "link" => AttachmentKind.Link,
        _ => null,
    };

    /// <summary>
    /// The same attachment made safe to keep, or null if it can't be: a known kind, a target that passes
    /// <see cref="AttachmentRules"/> (and for a saved copy, a place inside the attachments folder, never a
    /// program), a one-line label. What a drop makes and what attachments.json gives back pass the same rule.
    /// </summary>
    public static Attachment? Clean(AttachmentKind kind, string? target, string? label, bool saved, Func<string, bool> isProgram)
    {
        string? clean = kind switch
        {
            AttachmentKind.Link when !saved => AttachmentRules.CleanUrl(target)?.AbsoluteUri,
            AttachmentKind.File when saved => target != null && AttachmentRules.IsSavedTarget(target) && !isProgram(WinPath.Extension(target)) ? target : null,
            AttachmentKind.File or AttachmentKind.Folder when !saved => AttachmentRules.CleanPath(target),
            _ => null,
        };
        if (clean == null) return null;
        string name = DropNames.OneLine(label, DropLimits.MaxLabel);
        if (name.Length == 0)
            name = kind == AttachmentKind.Link ? new Uri(clean).IdnHost : DropNames.OneLine(DropNames.LastSegment(clean), DropLimits.MaxLabel);
        return new Attachment(kind, clean, name, saved);
    }
}

/// <summary>
/// attachments.json beside tack.json, and the attachments folder of saved copies: what each task's paperclip
/// opens on this PC. Written only by a drop onto the orb (and when attachments are let go); never synced,
/// imported or exported, and tack.json never mentions it. Someone who never drops anything never gets the
/// file or the folder.
/// </summary>
internal sealed class AttachmentStore
{
    public const string FileName = "attachments.json";
    public const string FolderName = "attachments";
    private const int CurrentVersion = 1;

    private readonly string _path;
    private readonly Func<string, bool> _isProgram;
    private readonly Dictionary<string, Attachment> _byTask = new(StringComparer.Ordinal);
    private readonly List<string> _order = new(); // task ids, in the order they were added

    public AttachmentStore(string dataFolder, Func<string, bool>? isProgram = null)
    {
        _path = Path.Combine(dataFolder, FileName);
        Folder = Path.Combine(dataFolder, FolderName);
        _isProgram = isProgram ?? DropNames.ProgramExtensions.Contains;
    }

    public string FilePath => _path;

    /// <summary>Where saved copies live: one folder per copy, named with 12 random hex characters.</summary>
    public string Folder { get; }

    public int Count => _order.Count;

    public bool IsFull => _order.Count >= DropLimits.MaxAttachments;

    /// <summary>
    /// attachments.json (and its .bak) was there but couldn't be read: it's kept aside, and it and the attachments
    /// folder are left exactly as they are this session (nothing added, saved, let go or swept), since nothing
    /// can say which copies are still wanted.
    /// </summary>
    public bool LeftAlone { get; private set; }

    public IEnumerable<(string TaskId, Attachment Attachment)> All => _order.Select(id => (id, _byTask[id]));

    public Attachment? For(string taskId) => _byTask.GetValueOrDefault(taskId);

    /// <summary>Keeps an attachment for a task (replacing one it had). False when Tack already keeps as many as it will.</summary>
    public bool Add(string taskId, Attachment attachment)
    {
        if (LeftAlone || !Ids.IsValid(taskId)) return false;
        if (_byTask.ContainsKey(taskId)) Remove(taskId);
        else if (IsFull) return false;
        _byTask[taskId] = attachment;
        _order.Add(taskId);
        return true;
    }

    /// <summary>Lets a task's attachment go, and its saved copy with it. True if it had one.</summary>
    public bool Remove(string taskId)
    {
        if (!_byTask.Remove(taskId, out var a)) return false;
        _order.Remove(taskId);
        if (a.Saved) DeleteCopy(a.Target);
        return true;
    }

    /// <summary>Lets go of every attachment <paramref name="keep"/> says no to (with their saved copies). Returns the task ids let go.</summary>
    public IReadOnlyList<string> Prune(Func<string, bool> keep)
    {
        var gone = _order.Where(id => !keep(id)).ToList();
        foreach (string id in gone) Remove(id);
        return gone;
    }

    /// <summary>What the attachments folder holds now, in bytes (its copies, one level down; links aren't followed).</summary>
    public long FolderBytes()
    {
        long total = 0;
        try
        {
            var root = new DirectoryInfo(Folder);
            if (!root.Exists) return 0;
            int dirs = 0;
            foreach (var dir in root.EnumerateDirectories())
            {
                if (++dirs > DropLimits.MaxAttachments * 2) return long.MaxValue / 2; // never this many of Tack's own: count it as full
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                foreach (var file in dir.EnumerateFiles()) total += file.Length;
            }
            foreach (var file in root.EnumerateFiles()) total += file.Length;
        }
        catch (Exception) { /* an unreadable folder counts as what was read */ }
        return total;
    }

    /// <summary>
    /// Saved copies no attachment points to any more (Tack stopped between saving one and writing
    /// attachments.json), older than <paramref name="olderThanUtc"/>: deleted. Only Tack's own folders
    /// (12 hex characters), only the files directly in them, never through a link.
    /// </summary>
    public int SweepOrphans(DateTime olderThanUtc)
    {
        if (LeftAlone) return 0;
        int swept = 0;
        try
        {
            var root = new DirectoryInfo(Folder);
            if (!root.Exists) return 0;
            var used = new HashSet<string>(_byTask.Values.Where(a => a.Saved).Select(a => a.Target[..12]), StringComparer.OrdinalIgnoreCase);
            int seen = 0;
            foreach (var dir in root.EnumerateDirectories())
            {
                if (++seen > DropLimits.MaxAttachments * 2) break;
                if (!IsCopyFolderName(dir.Name) || used.Contains(dir.Name) || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (dir.LastWriteTimeUtc > olderThanUtc) continue;
                if (DeleteFolder(dir)) swept++;
            }
        }
        catch (Exception) { /* next time */ }
        return swept;
    }

    private static bool IsCopyFolderName(string name) => name.Length == 12 && name.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private void DeleteCopy(string target)
    {
        try
        {
            if (AttachmentRules.SavedPath(Folder, target) is not { } path) return;
            var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
            if (dir.Exists && !dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) DeleteFolder(dir);
        }
        catch (Exception) { /* swept up later */ }
    }

    /// <summary>The files directly in one of Tack's copy folders, then the folder (only if that leaves it empty).</summary>
    private static bool DeleteFolder(DirectoryInfo dir)
    {
        try
        {
            foreach (var file in dir.EnumerateFiles())
            {
                if (file.Attributes.HasFlag(FileAttributes.ReadOnly)) file.Attributes &= ~FileAttributes.ReadOnly;
                file.Delete();
            }
            dir.Delete(recursive: false);
            return true;
        }
        catch (Exception) { return false; }
    }

    // ================= attachments.json =================

    /// <summary>
    /// Reads attachments.json (or its .bak): unknown fields are ignored, every known one is checked, and an
    /// entry that fails is dropped on its own; the rest load. A missing file is no attachments, and creates nothing.
    /// One that's there but can't be read, nor its .bak, leaves everything as it is (<see cref="LeftAlone"/>).
    /// </summary>
    public void Load(Action<string, Exception?>? onError = null)
    {
        _byTask.Clear();
        _order.Clear();
        LeftAlone = false;
        bool found = false;
        foreach (string candidate in new[] { _path, _path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                found = true;
                if (new FileInfo(candidate).Length > DropLimits.MaxFileBytes) throw new InvalidDataException("attachments.json is too large");
                Parse(File.ReadAllBytes(candidate));
                return;
            }
            catch (Exception ex)
            {
                _byTask.Clear();
                _order.Clear();
                onError?.Invoke($"Could not read {Path.GetFileName(candidate)}", ex);
            }
        }
        if (!found) return;
        LeftAlone = true;
        Services.Unreadable.KeepAside(File.Exists(_path) ? _path : _path + ".bak");
        onError?.Invoke("attachments.json couldn't be read; it and the attachments folder are left as they are", null);
    }

    /// <summary>Takes in a file's entries (see <see cref="Load"/>). Throws if it isn't JSON with an "attachments" list.</summary>
    internal void Parse(ReadOnlySpan<byte> json)
    {
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) json = json[3..];
        using var doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("attachments", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("not an attachments file");
        foreach (var entry in list.EnumerateArray())
        {
            if (IsFull) break;
            try
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                string? task = Str(entry, "task");
                if (task == null || !Ids.IsValid(task) || _byTask.ContainsKey(task)) continue;
                if (Attachment.ParseKind(Str(entry, "kind")) is not { } kind) continue;
                bool saved = entry.TryGetProperty("saved", out var s) && s.ValueKind == JsonValueKind.True;
                if (Attachment.Clean(kind, Str(entry, "target"), Str(entry, "label"), saved, _isProgram) is not { } clean) continue;
                _byTask[task] = clean;
                _order.Add(task);
            }
            catch (Exception) { /* this entry only (a lone surrogate, say) */ }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public byte[] Serialize()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", CurrentVersion);
            w.WriteStartArray("attachments");
            foreach (var (task, a) in All)
            {
                w.WriteStartObject();
                w.WriteString("task", task);
                w.WriteString("kind", Attachment.KindName(a.Kind));
                w.WriteString("target", a.Target);
                w.WriteString("label", a.Label);
                if (a.Saved) w.WriteBoolean("saved", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Atomic write (temp file, then replace, keeping attachments.json.bak), as launcher.json is written.</summary>
    public void Save()
    {
        if (LeftAlone) return; // never over a file that couldn't be read
        byte[] bytes = Serialize();
        string tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, _path);
    }
}
