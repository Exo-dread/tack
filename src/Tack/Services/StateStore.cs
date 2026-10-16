using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Tack.Model;

namespace Tack.Services;

/// <summary>On-disk shape of tack.json. Human-readable on purpose: it's your data.</summary>
internal sealed class StateDocument
{
    public int Version { get; set; } = 1;
    public List<TaskRecord> Tasks { get; set; } = new();
    public List<SheetRecord> Sheets { get; set; } = new();
    public Settings Settings { get; set; } = new();
}

internal sealed class TaskRecord
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? Completed { get; set; }
    public bool Now { get; set; }
}

internal sealed class SheetRecord
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset Updated { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StateDocument))]
internal partial class StateJsonContext : JsonSerializerContext;

/// <summary>
/// Loads and saves everything to a single JSON file.
/// Writes are debounced, atomic (temp file + replace) and keep the previous version as
/// tack.json.bak, so a crash or power cut mid-write can never lose more than the last moment of typing.
/// </summary>
internal sealed class StateStore
{
    private readonly string _path;
    private readonly DispatcherTimer _debounce;
    private Workspace? _workspace;
    private byte[]? _lastWritten;

    public StateStore(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "tack.json");
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) => Flush();
    }

    public string Folder { get; }

    public string FilePath => _path;

    /// <summary>Portable mode: a "TackData" folder next to Tack.exe keeps everything beside the app.</summary>
    public static string ResolveFolder()
    {
        string portable = Path.Combine(AppContext.BaseDirectory, "TackData");
        if (Directory.Exists(portable)) return portable;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tack");
    }

    public StateDocument Load()
    {
        TryDelete(_path + ".tmp"); // left behind only if a previous write was interrupted

        foreach (string candidate in new[] { _path, _path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                byte[] bytes = File.ReadAllBytes(candidate);
                ReadOnlySpan<byte> json = bytes;
                if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) json = json[3..]; // BOM from editors like old Notepad
                var doc = JsonSerializer.Deserialize(json, StateJsonContext.Default.StateDocument);
                if (doc == null) continue;
                if (candidate != _path) Log.Info("Recovered data from backup file.");
                else _lastWritten = bytes;
                return doc;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not read {Path.GetFileName(candidate)}", ex);
                if (candidate == _path)
                {
                    // Keep the unreadable file for the user instead of overwriting it.
                    try { File.Copy(_path, $"{_path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false); }
                    catch { /* best effort */ }
                }
            }
        }
        return new StateDocument();
    }

    /// <summary>Applies a loaded document to a fresh workspace and starts watching it for changes.</summary>
    public Workspace Attach(StateDocument doc)
    {
        doc.Settings.MigrateLegacySize();
        var ws = new Workspace(doc.Settings);
        TaskItem? now = null;
        var open = new List<TaskItem>();
        var done = new List<TaskItem>();
        foreach (var r in doc.Tasks)
        {
            if (string.IsNullOrWhiteSpace(r.Text)) continue;
            var item = new TaskItem(r.Text, string.IsNullOrEmpty(r.Id) ? null : r.Id, r.Created, r.Completed);
            if (item.IsDone) done.Add(item);
            else if (r.Now && now == null) now = item;
            else open.Add(item);
        }
        var sheets = doc.Sheets.Select(s => new NoteSheet(s.Text, string.IsNullOrEmpty(s.Id) ? null : s.Id, s.Updated)).ToList();
        ws.Populate(now, open, done, sheets);

        _workspace = ws;
        ws.Changed += (_, _) => RequestSave();
        return ws;
    }

    public void RequestSave()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Writes immediately if anything changed since the last write.</summary>
    public void Flush()
    {
        _debounce.Stop();
        if (_workspace == null) return;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Snapshot(_workspace), StateJsonContext.Default.StateDocument);
            if (_lastWritten != null && bytes.AsSpan().SequenceEqual(_lastWritten)) return;
            WriteAtomic(bytes);
            _lastWritten = bytes;
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
            RequestSave(); // try again shortly
        }
    }

    private static StateDocument Snapshot(Workspace ws)
    {
        var doc = new StateDocument { Settings = ws.Settings };
        if (ws.Now is { } now) doc.Tasks.Add(ToRecord(now, isNow: true));
        foreach (var t in ws.Open) doc.Tasks.Add(ToRecord(t, false));
        foreach (var t in ws.Done) doc.Tasks.Add(ToRecord(t, false));
        foreach (var s in ws.Sheets) doc.Sheets.Add(new SheetRecord { Id = s.Id, Text = s.Text, Updated = s.UpdatedAt });
        return doc;
    }

    private static TaskRecord ToRecord(TaskItem t, bool isNow) => new()
    {
        Id = t.Id, Text = t.Text, Created = t.CreatedAt, Completed = t.CompletedAt, Now = isNow,
    };

    private void WriteAtomic(byte[] bytes)
    {
        string tmp = _path + ".tmp";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
                else File.Move(tmp, _path);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                // Antivirus or backup tools sometimes hold the file for a moment.
                Thread.Sleep(40);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
