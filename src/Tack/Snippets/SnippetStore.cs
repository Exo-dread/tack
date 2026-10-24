using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tack.Services;
using Tack.Sync;

namespace Tack.Snippets;

/// <summary>A pasted image kept with a sheet, on this PC only. Its file is always <see cref="FileName"/>.</summary>
internal sealed record Snippet(string Id, string SheetId, DateTimeOffset Created, long Size, int Width, int Height)
{
    public string FileName => Id + ".png";

    /// <summary>When it was pasted, as people say it: the time today, the date and time before that.</summary>
    public string PastedAt
    {
        get
        {
            var local = Created.ToLocalTime();
            return local.ToString(local.Date == DateTime.Today ? "t" : "g", System.Globalization.CultureInfo.CurrentCulture);
        }
    }
}

/// <summary>Why a snippet wasn't added.</summary>
/// <remarks><see cref="SnippetRefusal.Held"/>: there would be room, but discarded snippets whose Undo is still on offer hold it.</remarks>
internal enum SnippetRefusal { None, Full, NoRoom, CantSave, Held }

/// <summary>
/// Snippets: PNG files in the snippets folder beside tack.json, and snippets.json saying which sheet each
/// belongs to. Both stay on this PC: tack.json, sync, imports and exports never see them, so on other
/// devices a sheet shows only its text. Someone who never pastes an image gets neither. The index is read
/// as strictly as any other file from disk (an entry that doesn't check out is dropped, and its file never
/// named by it: a file is always its id), and files no entry names are cleared away when Tack starts.
/// </summary>
internal sealed partial class SnippetStore
{
    public const string IndexName = "snippets.json";
    public const string FolderName = "snippets";

    /// <summary>The most snippets kept, all sheets together.</summary>
    public const int MaxCount = 50;

    /// <summary>The most bytes of snippets kept, all sheets together.</summary>
    public const long MaxTotalBytes = 50L * 1024 * 1024;

    /// <summary>snippets.json is a few KB when full; anything much bigger isn't Tack's.</summary>
    private const long MaxIndexBytes = 256 * 1024;

    private const int Version = 1;

    private readonly List<Snippet> _all = new();
    /// <summary>Off their sheets, with their files kept until the Undo that would put them back lapses: they keep their room.</summary>
    private readonly Dictionary<string, Snippet> _held = new(StringComparer.Ordinal);
    /// <summary>The files the index named when it was read, whether or not each entry was loaded: the start's tidy spares them.</summary>
    private readonly HashSet<string> _named = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// snippets.json was written by a later Tack, or it (and its .bak) couldn't be read: left exactly as it is,
    /// and so is every file in the folder, since nothing can say which of them are still wanted.
    /// </summary>
    private bool _leftAlone;

    public SnippetStore(string dataFolder)
    {
        Folder = Path.Combine(dataFolder, FolderName);
        IndexPath = Path.Combine(dataFolder, IndexName);
        Load();
    }

    /// <summary>Where the PNG files are.</summary>
    public string Folder { get; }

    public string IndexPath { get; }

    public int Count => _all.Count;

    public long TotalBytes => _all.Sum(s => s.Size);

    /// <summary>snippets.json is from a later Tack, or couldn't be read: nothing is added, saved or deleted this session.</summary>
    public bool LeftAlone => _leftAlone;

    /// <summary>A sheet's snippets were added, discarded or put back (the sheet's id).</summary>
    public event Action<string>? Changed;

    /// <summary>A sheet's snippets, oldest first.</summary>
    public IReadOnlyList<Snippet> Of(string? sheetId) =>
        sheetId == null ? [] : _all.Where(s => s.SheetId == sheetId).OrderBy(s => s.Created).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();

    public bool Any(string? sheetId) => sheetId != null && _all.Exists(s => s.SheetId == sheetId);

    public string PathOf(Snippet snippet) => Path.Combine(Folder, snippet.FileName);

    // ================= Changes =================

    /// <summary>
    /// Keeps an image with a sheet, if there's room (<see cref="MaxCount"/>, <see cref="MaxTotalBytes"/>), counting
    /// the discarded snippets an Undo could still put back, so that Undo never takes Notes past either.
    /// </summary>
    public (Snippet? Snippet, SnippetRefusal Refusal) Add(string sheetId, SnippetPng png, DateTimeOffset now)
    {
        if (_leftAlone) return (null, SnippetRefusal.CantSave);
        if (_all.Count >= MaxCount) return (null, SnippetRefusal.Full);
        if (TotalBytes + png.Bytes.Length > MaxTotalBytes) return (null, SnippetRefusal.NoRoom);
        if (_all.Count + _held.Count >= MaxCount || TotalBytes + _held.Values.Sum(s => s.Size) + png.Bytes.Length > MaxTotalBytes)
            return (null, SnippetRefusal.Held);
        var snippet = new Snippet(NewId(), sheetId, IsoTime.ToMilliseconds(now), png.Bytes.Length, png.Width, png.Height);
        string path = PathOf(snippet), tmp = Path.Combine(Folder, snippet.Id + ".tmp");
        try
        {
            Directory.CreateDirectory(Folder);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                fs.Write(png.Bytes);
            File.Move(tmp, path);
            _all.Add(snippet);
            Save();
        }
        catch (Exception ex)
        {
            Log.Error("Snippets: couldn't keep a pasted image", ex);
            _all.Remove(snippet);
            TryDelete(tmp);
            TryDelete(path);
            return (null, SnippetRefusal.CantSave);
        }
        Changed?.Invoke(sheetId);
        return (snippet, SnippetRefusal.None);
    }

    /// <summary>
    /// Takes snippets off their sheets (discarded, or their sheet deleted), keeping their files until the Undo
    /// that would bring them back lapses (<see cref="Forget"/>); quitting before then leaves the files to be
    /// cleared away at the next start.
    /// </summary>
    public void Detach(IReadOnlyCollection<Snippet> snippets)
    {
        if (snippets.Count == 0) return;
        var ids = snippets.Select(s => s.Id).ToHashSet();
        var off = _all.Where(s => ids.Contains(s.Id)).ToList();
        if (off.Count == 0) return;
        _all.RemoveAll(s => ids.Contains(s.Id));
        foreach (var s in off) _held[s.Id] = s;
        Save();
        foreach (string sheet in snippets.Select(s => s.SheetId).Distinct()) Changed?.Invoke(sheet);
    }

    /// <summary>Undo: puts snippets back on their sheets (those whose files are still there).</summary>
    public void Attach(IReadOnlyCollection<Snippet> snippets)
    {
        foreach (var s in snippets) _held.Remove(s.Id);
        var back = snippets.Where(s => _all.TrueForAll(o => o.Id != s.Id) && File.Exists(PathOf(s))).ToList();
        if (back.Count == 0) return;
        _all.AddRange(back);
        Save();
        foreach (string sheet in back.Select(s => s.SheetId).Distinct()) Changed?.Invoke(sheet);
    }

    /// <summary>The Undo lapsed: deletes the files of snippets that are still off their sheets.</summary>
    public void Forget(IReadOnlyCollection<Snippet> snippets)
    {
        foreach (var s in snippets)
        {
            _held.Remove(s.Id);
            if (_all.TrueForAll(o => o.Id != s.Id)) TryDelete(PathOf(s));
        }
    }

    /// <summary>
    /// At start: snippets whose sheet is gone (deleted here or on another device) go, files and all. The
    /// sheets have been loaded by then, so a sheet that isn't there now won't be.
    /// </summary>
    public void Prune(Func<string, bool> sheetIsLive)
    {
        if (_leftAlone) return;
        var gone = _all.Where(s => !sheetIsLive(s.SheetId)).ToList();
        if (gone.Count == 0) return;
        _all.RemoveAll(gone.Contains);
        Save();
        foreach (var s in gone) TryDelete(PathOf(s));
        Log.Info($"Snippets: cleared {gone.Count} whose sheet is gone.");
    }

    /// <summary>
    /// At start, against the workspace that was loaded: snippets of sheets it doesn't hold (or holds deleted)
    /// go. Unless tack.json couldn't be read (<see cref="Model.Workspace.LoadFailed"/>): then it holds none of
    /// the user's sheets, and every snippet stays for when they come back.
    /// </summary>
    public void Prune(Model.Workspace ws)
    {
        if (ws.LoadFailed)
        {
            Log.Info("Snippets: tack.json couldn't be read, so none are cleared.");
            return;
        }
        Prune(id => ws.SheetById(id) is { IsDeleted: false });
    }

    // ================= snippets.json =================

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^([0-9a-f]{32})\.(png|tmp)$")]
    private static partial Regex OwnFile();

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// Reads the index (or its backup), then clears away files it doesn't name. No index and no folder: nothing
    /// at all. An index that's there but can't be read (nor its backup) clears nothing: it's kept aside, as an
    /// unreadable tack.json is, and it and the folder are left exactly as they are.
    /// </summary>
    private void Load()
    {
        bool found = false, read = false;
        foreach (string candidate in new[] { IndexPath, IndexPath + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                found = true;
                if (new FileInfo(candidate).Length > MaxIndexBytes) throw new InvalidDataException("snippets.json is too large");
                Read(File.ReadAllBytes(candidate));
                read = true;
                break;
            }
            catch (Exception ex)
            {
                _all.Clear();
                _named.Clear();
                _leftAlone = false;
                Log.Error($"Snippets: couldn't read {Path.GetFileName(candidate)}", ex);
            }
        }
        if (found && !read)
        {
            _leftAlone = true;
            Unreadable.KeepAside(File.Exists(IndexPath) ? IndexPath : IndexPath + ".bak");
            Log.Info("Snippets: snippets.json couldn't be read; it and the snippets folder are left as they are.");
        }
        if (_leftAlone || !Directory.Exists(Folder)) return;

        // Files no entry names: discarded snippets whose Undo hadn't lapsed when Tack closed, or leftovers.
        // Only files that look like Tack's own are touched; one the index names but that's past a cap stays.
        var named = _named;
        try
        {
            foreach (string file in Directory.EnumerateFiles(Folder).Take(10_000))
            {
                string name = Path.GetFileName(file);
                if (OwnFile().IsMatch(name) && !named.Contains(name)) TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Snippets: couldn't tidy the snippets folder", ex);
        }
    }

    /// <summary>
    /// The index's entries, each checked on its own: an id of 32 hex digits (the file is always that id
    /// plus .png, whatever the entry says), a sheet id as Tack makes them, a date, a size in range, and a file
    /// that's really there. Unknown fields are ignored; a later version's file is left alone.
    /// </summary>
    private void Read(byte[] json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("snippets.json isn't an object");
        if (root.TryGetProperty("version", out var v) && (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int version) || version > Version))
        {
            _leftAlone = true;
            Log.Info("Snippets: snippets.json is from a later version of Tack; left as it is.");
            return;
        }
        if (!root.TryGetProperty("snippets", out var list) || list.ValueKind != JsonValueKind.Array) return;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        int seen = 0;
        foreach (var e in list.EnumerateArray())
        {
            if (++seen > MaxCount * 4) break; // far past anything Tack writes
            if (Entry(e) is not { } s || !ids.Add(s.Id)) continue;
            // Past a cap an entry isn't loaded, but its file isn't a leftover either: the tidy spares it.
            _named.Add(s.FileName);
            if (_all.Count >= MaxCount) continue;
            var file = new FileInfo(PathOf(s));
            if (!file.Exists || file.Length <= 0 || file.Length > SnippetImage.MaxSnippetBytes || total + file.Length > MaxTotalBytes) continue;
            total += file.Length;
            _all.Add(s with { Size = file.Length });
        }
    }

    private static Snippet? Entry(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? id = Text(e, "id"), sheet = Text(e, "sheetId"), file = Text(e, "file"), created = Text(e, "created");
        if (id == null || !IdPattern().IsMatch(id) || !Ids.IsValid(sheet)) return null;
        if (file != null && file != id + ".png") return null;
        if (IsoTime.Parse(created) is not { } when) return null;
        if (!Int(e, "width", SnippetImage.MaxStoredSide * 8, out int width) || !Int(e, "height", SnippetImage.MaxStoredSide * 8, out int height)) return null;
        return new Snippet(id, sheet!, when, 0, width, height);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Int(JsonElement e, string name, int max, out int value)
    {
        value = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value) && value >= 1 && value <= max;
    }

    /// <summary>Atomic write (temp file, then replace, keeping snippets.json.bak), like launcher.json.</summary>
    private void Save()
    {
        if (_leftAlone) return;
        using var buffer = new MemoryStream();
        // Readable as written: the dates' + left as it is (every value is hex digits, a date or a number).
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteStartArray("snippets");
            foreach (var s in _all)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("sheetId", s.SheetId);
                w.WriteString("file", s.FileName);
                w.WriteString("created", IsoTime.Format(s.Created));
                w.WriteNumber("size", s.Size);
                w.WriteNumber("width", s.Width);
                w.WriteNumber("height", s.Height);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        try
        {
            string tmp = IndexPath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(IndexPath)) File.Replace(tmp, IndexPath, IndexPath + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, IndexPath);
        }
        catch (Exception ex)
        {
            // Tried again with the next change; until then what's on screen is what counts.
            Log.Error("Snippets: couldn't save snippets.json", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Log.Error("Snippets: couldn't delete a file", ex); }
    }
}
