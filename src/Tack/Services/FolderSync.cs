using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Microsoft.Win32;
using Tack.Model;
using Tack.Sync;

namespace Tack.Services;

/// <summary>
/// Sync with Tack Focus through a folder on this PC (normally OneDrive's "Apps\Tack Focus"). Tack
/// never connects to anything: it reads and writes files in that folder, and the OneDrive app
/// carries them to and from the cloud. Off until you turn it on.
///
/// Nothing runs while idle: Tack reacts to the folder changing (a file system watcher), to your
/// edits (a few seconds after), to the panel opening, and to the PC waking up. Never a timer,
/// except to retry after something went wrong.
/// </summary>
internal sealed class FolderSync : IDisposable
{
    private static readonly TimeSpan AfterEdit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AfterFolderChange = TimeSpan.FromSeconds(1.5);
    /** Hiccups stay quiet until this many in a row. */
    private const int QuietFailures = 3;

    private readonly Workspace _ws;
    private readonly StateStore _store;
    private readonly string _metaPath;
    private readonly DispatcherTimer _soon;
    private FileSystemWatcher? _watcher;
    private bool _running;
    private bool _again;
    private int _failures;
    private bool _disposed;

    public FolderSync(Workspace ws, StateStore store)
    {
        _ws = ws;
        _store = store;
        _metaPath = Path.Combine(store.Folder, "sync.json");
        _soon = new DispatcherTimer(DispatcherPriority.Background);
        _soon.Tick += (_, _) =>
        {
            _soon.Stop();
            _ = RunAsync();
        };
        _ws.Changed += (_, _) =>
        {
            if (Enabled && !_running) Schedule(AfterEdit);
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public bool Enabled => _ws.Settings.SyncFolder != null;

    public SyncStatus Status { get; private set; } = SyncStatus.Off;

    public event Action? StatusChanged;

    // ---------------------------------------------------------------- turning it on and off

    public void Start()
    {
        if (!Enabled) return;
        Watch();
        Schedule(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Turns sync on with <paramref name="folder"/>, or the Tack Focus folder in OneDrive if none
    /// is given. Returns what to tell you if it can't.
    /// </summary>
    public async Task<string?> TurnOnAsync(string? folder = null)
    {
        if (folder == null)
        {
            SetStatus(SyncStatus.Off with { Message = "Looking for Tack Focus’s folder in OneDrive…" });
            folder = await Task.Run(FindOneDriveFolder);
            if (folder == null)
            {
                SetStatus(SyncStatus.Off with
                {
                    Message = "Couldn’t find Tack Focus’s folder in OneDrive on this PC. Turn on sync in Tack Focus first (it makes the folder), wait for the OneDrive app to bring it here, then try again. Or choose a folder.",
                });
                return Status.Message;
            }
        }
        try
        {
            var local = new LocalSyncFolder(folder);
            local.Prepare();
            local.KeepOnDevice();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(SyncStatus.Off with { Message = $"Tack can’t use that folder: {ex.Message}" });
            return Status.Message;
        }
        _ws.Settings.SyncFolder = folder;
        _store.Flush();
        _failures = 0;
        Watch();
        SetStatus(new SyncStatus(SyncPhase.Syncing, folder, LoadMeta()?.LastSync ?? 0, null, [], []));
        await RunAsync();
        return null;
    }

    /// <summary>Stops syncing. Nothing is deleted: tasks stay here, files stay in the folder for your other devices.</summary>
    public void TurnOff()
    {
        _soon.Stop();
        StopWatching();
        _ws.Settings.SyncFolder = null;
        _store.Flush();
        SetStatus(SyncStatus.Off);
    }

    /// <summary>Syncs now (Settings, or when the panel opens).</summary>
    public void SyncNow()
    {
        if (!Enabled) return;
        _failures = 0;
        if (_watcher == null) Watch();
        Schedule(TimeSpan.Zero);
    }

    // ---------------------------------------------------------------- finding the folder

    /// <summary>
    /// Tack Focus's folder in OneDrive on this PC: the one with Tack's marker, usually
    /// "OneDrive\Apps\Tack Focus" (the "Apps" name can be localised, so any first-level folder counts).
    /// </summary>
    public static string? FindOneDriveFolder()
    {
        var roots = new[] { "OneDriveConsumer", "OneDrive", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .OfType<string>()
            .Where(r => r.Length > 0 && Directory.Exists(r))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            int looked = 0;
            IEnumerable<string> Dirs(string path)
            {
                try { return Directory.EnumerateDirectories(path).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
            }
            // "Apps" first, then every other top-level folder; directory listings never download files.
            var tops = Dirs(root).OrderBy(d => string.Equals(Path.GetFileName(d), "Apps", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
            foreach (string top in tops)
            {
                foreach (string sub in Dirs(top))
                {
                    if (LocalSyncFolder.IsSyncFolder(sub)) return sub;
                    if (++looked > 5000) break;
                }
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- running

    private void Schedule(TimeSpan delay)
    {
        if (_disposed) return;
        _soon.Stop();
        _soon.Interval = delay;
        _soon.Start();
    }

    private void Watch()
    {
        StopWatching();
        string? root = _ws.Settings.SyncFolder;
        if (root == null || !Directory.Exists(root)) return;
        try
        {
            // The whole folder (not just "devices"), so it also notices "devices" being made or removed.
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 16 * 1024,
            };
            FileSystemEventHandler changed = (_, e) => OnFolderEvent(e.Name);
            _watcher.Created += changed;
            _watcher.Changed += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, e) => OnFolderEvent(e.Name);
            _watcher.Error += (_, _) => OnFolderEvent(null);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Error("Can't watch the sync folder", ex);
            StopWatching();
        }
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>Something changed in the folder (on a worker thread). Tack's own temp files don't count.</summary>
    private void OnFolderEvent(string? name)
    {
        if (name != null && Path.GetFileName(name).StartsWith("~$", StringComparison.Ordinal)) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Enabled && !_running) Schedule(AfterFolderChange);
            else if (_running) _again = true;
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) System.Windows.Application.Current?.Dispatcher.BeginInvoke(SyncNow);
    }

    /// <summary>One sync cycle, on the UI thread (the workspace lives there); file access is asynchronous.</summary>
    private async Task RunAsync()
    {
        if (_running)
        {
            _again = true;
            return;
        }
        string? root = _ws.Settings.SyncFolder;
        if (root == null || _disposed) return;
        _running = true;
        SetStatus(Status with { Phase = SyncPhase.Syncing, Folder = root });
        TimeSpan? retry = null;
        try
        {
            var folder = new LocalSyncFolder(root);
            if (!Directory.Exists(root))
            {
                SetStatus(Status with { Phase = SyncPhase.Waiting, Message = "The sync folder isn’t there right now (is OneDrive running?). Tack will look again when you open it." });
                return;
            }
            if (_watcher == null) Watch();
            var result = await CycleAsync(folder, root, collisionRetried: false);
            _failures = 0;
            SetStatus(new SyncStatus(SyncPhase.Synced, root, result.LastSync, null, result.Peers, result.Problems));
        }
        catch (SyncException ex)
        {
            _failures++;
            Log.Info($"Sync attempt failed ({ex.Kind}): {ex.Message}");
            retry = TimeSpan.FromSeconds(Math.Min(1800, 5 * Math.Pow(2, Math.Min(_failures - 1, 10))));
            bool lasting = ex.Kind is SyncErrorKind.Full or SyncErrorKind.Denied;
            if (lasting || _failures >= QuietFailures)
            {
                string message = ex.Kind switch
                {
                    SyncErrorKind.Full => "The disk is full, so Tack can’t save its sync file.",
                    SyncErrorKind.Denied => "Tack isn’t allowed to write in the sync folder.",
                    SyncErrorKind.Gone => "The sync folder went away (is OneDrive running?). Tack will keep trying.",
                    _ => "The OneDrive app is busy with the sync folder. Tack will keep trying.",
                };
                SetStatus(Status with { Phase = lasting ? SyncPhase.Error : SyncPhase.Waiting, Message = message });
            }
            else
            {
                // A hiccup (a file the OneDrive app is writing right now): quietly try again.
                SetStatus(Status with { Phase = Status.LastSync > 0 ? SyncPhase.Synced : SyncPhase.Syncing });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Sync failed", ex);
            _failures++;
            retry = TimeSpan.FromMinutes(Math.Min(30, 1 << Math.Min(_failures, 5)));
            SetStatus(Status with { Phase = SyncPhase.Error, Message = $"Sync stopped: {ex.Message}" });
        }
        finally
        {
            _running = false;
        }
        if (retry is { } wait) Schedule(wait);
        else if (_again)
        {
            _again = false;
            Schedule(AfterFolderChange);
        }
    }

    private async Task<(long LastSync, List<PeerInfo> Peers, List<(string, DeviceFileProblem)> Problems)> CycleAsync(LocalSyncFolder folder, string root, bool collisionRetried)
    {
        string key = Path.GetFullPath(root).TrimEnd('\\').ToLowerInvariant();
        var meta = LoadMeta() is { } m && m.Folder == key ? m : SyncMeta.Empty(key);
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var engine = new SyncEngine(_ws, folder, new SyncHooks
        {
            Label = () => ($"Tack for Windows on {Environment.MachineName}", $"Tack for Windows {version?.Major}.{version?.Minor}.{version?.Build}"),
            FlushAsync = () =>
            {
                _store.Flush();
                return Task.CompletedTask;
            },
            SaveMetaAsync = SaveMetaAsync,
            Joined = () => _ws.LeaveWelcomeBehind(App.IsWelcomeTip),
        });
        try
        {
            var result = await engine.CycleAsync(meta, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await SaveMetaAsync(meta);
            return (meta.LastSync, result.Peers, result.Problems);
        }
        catch (DeviceCollisionException) when (!collisionRetried)
        {
            // A copy of this PC's data (a copied data folder) syncs elsewhere under the same id: become a new device.
            Log.Info("Another device uses this device's sync id; taking a new one.");
            _ws.Clock.Renew(Ids.NewDeviceId());
            meta.Own = new OwnFile(null, null, null);
            await SaveMetaAsync(meta);
            _store.Flush();
            return await CycleAsync(folder, root, collisionRetried: true);
        }
    }

    // ---------------------------------------------------------------- state

    private SyncMeta? LoadMeta()
    {
        try
        {
            if (!File.Exists(_metaPath)) return null;
            return JsonSerializer.Deserialize(File.ReadAllBytes(_metaPath), SyncJsonContext.Default.SyncMeta);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Sync state unreadable; starting it afresh", ex);
            return null;
        }
    }

    private async Task SaveMetaAsync(SyncMeta meta)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(meta, SyncJsonContext.Default.SyncMeta);
        string tmp = _metaPath + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes);
        File.Move(tmp, _metaPath, overwrite: true);
    }

    private void SetStatus(SyncStatus status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _soon.Stop();
        StopWatching();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}

internal enum SyncPhase
{
    /// <summary>Not turned on.</summary>
    Off,
    Syncing,
    /// <summary>Up to date as of LastSync.</summary>
    Synced,
    /// <summary>The folder isn't reachable right now; Tack will try again by itself.</summary>
    Waiting,
    /// <summary>Something needs looking at.</summary>
    Error,
}

internal sealed record SyncStatus(
    SyncPhase Phase,
    string? Folder,
    long LastSync,
    string? Message,
    IReadOnlyList<PeerInfo> Peers,
    IReadOnlyList<(string Device, DeviceFileProblem Problem)> Problems)
{
    public static readonly SyncStatus Off = new(SyncPhase.Off, null, 0, null, [], []);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncMeta))]
internal partial class SyncJsonContext : JsonSerializerContext;
