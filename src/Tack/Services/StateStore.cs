using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Tack.Model;
using Tack.Sync;

namespace Tack.Services;

/// <summary>On-disk shape of tack.json. Human-readable on purpose: it's your data.</summary>
internal sealed class StateDocument
{
    public int Version { get; set; } = 1;
    public List<TaskRecord> Tasks { get; set; } = new();
    public List<SheetRecord> Sheets { get; set; } = new();
    public Settings Settings { get; set; } = new();

    /// <summary>Clocks, sort keys and deletions for merging with Tack Focus (see <see cref="StateFile"/>). Older versions ignore it.</summary>
    public TackFocusSection? TackFocus { get; set; }
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
    private DateTime _loadedFileTime = DateTime.UtcNow;

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

    /// <summary>The last save failed (it's retried shortly). Sync waits for a good save before writing.</summary>
    public bool SaveFailed { get; private set; }

    /// <summary>
    /// tack.json (or its .bak) was there but neither could be read, so this session started empty. What's kept
    /// beside tack.json (snippets, attachments) then stays exactly as it is until the next start: it belongs
    /// to data that may yet come back (the unreadable file is kept aside).
    /// </summary>
    public bool LoadFailed { get; private set; }

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

        LoadFailed = false;
        foreach (string candidate in new[] { _path, _path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            LoadFailed = true; // until one of them reads
            try
            {
                byte[] bytes = File.ReadAllBytes(candidate);
                ReadOnlySpan<byte> json = bytes;
                if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) json = json[3..]; // BOM from editors like old Notepad
                var doc = JsonSerializer.Deserialize(json, StateJsonContext.Default.StateDocument);
                if (doc == null) continue;
                _loadedFileTime = File.GetLastWriteTimeUtc(candidate);
                if (candidate != _path) Log.Info("Recovered data from backup file.");
                else _lastWritten = bytes;
                LoadFailed = false;
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
        if (LoadFailed) Log.Info("Started empty: tack.json couldn't be read. Snippets and attachments are left as they are.");
        return new StateDocument();
    }

    /// <summary>Applies a loaded document to a fresh workspace and starts watching it for changes.</summary>
    public Workspace Attach(StateDocument doc)
    {
        doc.Settings.MigrateLegacySize();
        var clock = new Hlc(StateFile.Device(doc) ?? Ids.NewDeviceId());
        long fileMs = new DateTimeOffset(_loadedFileTime, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var data = StateFile.ToData(doc, clock, fileMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var ws = new Workspace(doc.Settings, clock, data) { LoadFailed = LoadFailed };

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
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(StateFile.ToDocument(_workspace), StateJsonContext.Default.StateDocument);
            if (_lastWritten != null && bytes.AsSpan().SequenceEqual(_lastWritten)) return;
            WriteAtomic(bytes);
            _lastWritten = bytes;
            SaveFailed = false;
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
            SaveFailed = true;
            RequestSave(); // try again shortly
        }
    }

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
