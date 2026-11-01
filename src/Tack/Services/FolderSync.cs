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
    /// <summary>Looks at a Google Drive folder's listing now and then (<see cref="NeedsLooking"/>).</summary>
    private readonly DispatcherTimer _look;
    private string? _lastLook;
    private bool _running;
    private bool _again;
    private int _failures;
    private bool _disposed;
    private readonly HashSet<string> _takeIn = new(); // devices whose file you asked to take in anyway
    private readonly string _claimPath;
    private readonly string _backupPath;

    /// <summary>Claim mode with nothing to claim yet: looked at again this often, or when the folder changes.</summary>
    internal static readonly TimeSpan SpareWait = TimeSpan.FromMinutes(3);

    /// <summary>What a claim-mode folder waiting for Tack Focus says.</summary>
    internal const string OpenTackFocus = "Open Tack Focus once to finish setting up Google Drive.";

    public FolderSync(Workspace ws, StateStore store)
    {
        _ws = ws;
        _store = store;
        _metaPath = Path.Combine(store.Folder, "sync.json");
        // Local only, beside sync.json: never synced, imported or exported.
        _claimPath = Path.Combine(store.Folder, "sync-claim.json");
        _backupPath = Path.Combine(store.Folder, "tack.before-google.json");
        _soon = new DispatcherTimer(DispatcherPriority.Background);
        _soon.Tick += (_, _) =>
        {
            _soon.Stop();
            _ = RunAsync();
        };
        _look = new DispatcherTimer(DispatcherPriority.Background) { Interval = LookEvery };
        _look.Tick += (_, _) => OnLook();
        _ws.Changed += (_, _) =>
        {
            if (!Enabled) return;
            if (_running) _again = true; // an edit during a cycle: another cycle after it
            else Schedule(AfterEdit);
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public bool Enabled => _ws.Settings.SyncFolder != null;

    public SyncStatus Status { get; private set; } = SyncStatus.Off;

    public event Action? StatusChanged;

    /// <summary>
    /// The sync folder is in claim mode (a folder on Google Drive whose marker says <c>"spares": true</c>,
    /// as Tack Focus makes it; or one this PC has claimed a file in), as last seen. See <see cref="LocalSyncFolder"/>.
    /// </summary>
    public bool ClaimMode { get; private set; }

    /// <summary>The app that carries the sync folder's files, for what Tack says: "Google Drive" in claim mode, otherwise "OneDrive".</summary>
    public string Provider => ClaimMode ? "Google Drive" : "OneDrive";

    // ---------------------------------------------------------------- turning it on and off

    public void Start()
    {
        if (!Enabled) return;
        // As last seen (only a record naming a claimed device pins it); each cycle looks again.
        ClaimMode = LoadClaim() is { Device: not null } claim && claim.Folder == FolderKey(_ws.Settings.SyncFolder!);
        Watch();
        Schedule(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Turns sync on with <paramref name="folder"/>, or the Tack Focus folder in OneDrive (or else in
    /// Google Drive) if none is given. Returns what to tell you if it can't.
    /// </summary>
    public async Task<string?> TurnOnAsync(string? folder = null)
    {
        if (folder == null)
        {
            SetStatus(SyncStatus.Off with { Message = "Looking for Tack Focus’s folder in OneDrive and Google Drive…" });
            var (own, other) = await Task.Run(FindOneDriveFolder);
            if (own == null)
            {
                var (google, googleOther) = await Task.Run(() => FindGoogleDriveFolder());
                own = google;
                other ??= googleOther;
            }
            folder = own;
            if (folder == null)
            {
                SetStatus(SyncStatus.Off with
                {
                    // Any other folder with Tack's marker may be another app's, or someone else's
                    // shared folder: only you can say it's yours, by choosing it.
                    Message = other != null
                        ? $"Found a Tack sync folder at {other}, but it isn’t OneDrive\\Apps\\{AppFolderName} or Google Drive’s My Drive\\{AppFolderName}. If it’s yours, pick it with Choose folder…"
                        : "Couldn’t find Tack Focus’s folder in OneDrive or Google Drive on this PC. Turn on sync in Tack Focus first (it makes the folder), wait for the OneDrive or Google Drive app to bring it here, then try again. Or choose a folder.",
                });
                return Status.Message;
            }
        }
        bool claim;
        try
        {
            claim = IsClaimFolder(folder);
            if (!claim)
            {
                var local = new LocalSyncFolder(folder);
                local.Prepare();
                local.KeepOnDevice();
            }
            else if (!Directory.Exists(folder))
            {
                // Never made here: one Tack made would be invisible to Tack Focus.
                SetStatus(SyncStatus.Off with { Message = "That folder isn’t there right now. " + OpenTackFocus });
                return Status.Message;
            }
            // In claim mode nothing is made or changed there: Tack Focus made it all.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SyncException)
        {
            SetStatus(SyncStatus.Off with { Message = $"Tack can’t use that folder: {ex.Message}" });
            return Status.Message;
        }
        ClaimMode = claim;
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

    /// <summary>
    /// Settings → Take it in anyway, for a device whose file was set aside as more than this PC takes
    /// in at once ("full"): that version is taken in next, up to what this PC's own file may carry
    /// (<see cref="SyncEngine.TakeIn"/>). Then syncs.
    /// </summary>
    public void TakeIn(string device)
    {
        if (!Enabled) return;
        _takeIn.Add(device);
        SyncNow();
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

    /// <summary>The app registration's name, and so its OneDrive app folder's: OneDrive\Apps\Tack Focus.</summary>
    private const string AppFolderName = "Tack Focus";

    /// <summary>
    /// Tack Focus's folder in OneDrive on this PC, "OneDrive\Apps\Tack Focus", if it has Tack's marker
    /// (<c>Own</c>): the only folder Tack Focus can write, so the only one taken without asking.
    /// Otherwise any other first-level sub-folder with the marker (<c>Other</c>), only to suggest: it
    /// may be Tack Focus's under a localised "Apps", but also another app's own folder (an app given
    /// only its own folder can put a marker there) or a folder someone shared with you.
    /// </summary>
    public static (string? Own, string? Other) FindOneDriveFolder()
    {
        var roots = new[] { "OneDriveConsumer", "OneDrive", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .OfType<string>()
            .Where(r => r.Length > 0 && Directory.Exists(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string root in roots)
        {
            string own = Path.Combine(root, "Apps", AppFolderName);
            if (LocalSyncFolder.IsSyncFolder(own)) return (own, null);
        }
        // Not there: the folder to suggest, one named like Tack Focus's first (under a localised "Apps").
        string? named = null, other = null;
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
                    if (LocalSyncFolder.IsSyncFolder(sub))
                    {
                        if (named == null && string.Equals(Path.GetFileName(sub), AppFolderName, StringComparison.OrdinalIgnoreCase)) named = sub;
                        other ??= sub;
                    }
                    if (++looked > 5000) break;
                }
            }
        }
        return (null, named ?? other);
    }

    /// <summary>Google Drive's own name for the folder of your files, under each account's root.</summary>
    private const string MyDrive = "My Drive";

    /// <summary>
    /// Where Google Drive for desktop keeps your files on this PC: each account as a drive letter whose
    /// volume label names Google Drive (streaming, "user@example.com - Google Drive"), with "My Drive"
    /// inside; or, mirroring, a "My Drive" folder in your user folder (so the user folder is the root).
    /// </summary>
    public static List<string> GoogleDriveRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory || !drive.IsReady) continue;
                if (drive.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase)) roots.Add(drive.RootDirectory.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A drive that isn't ready: not Google Drive's.
            }
        }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0 && Directory.Exists(Path.Combine(profile, MyDrive))) roots.Add(profile);
        return roots;
    }

    /// <summary>
    /// Tack Focus's folder in Google Drive on this PC: <c>&lt;root&gt;\&lt;first-level folder&gt;\Tack Focus</c>
    /// for each of <paramref name="roots"/> (<see cref="GoogleDriveRoots"/> if none are given). Taken by
    /// itself (<c>Own</c>) only if exactly one such folder is under a first-level folder named "My Drive"
    /// and has a marker with <c>"spares": true</c>, as Tack Focus makes it. Anyone can write such a marker
    /// (it may even be a shortcut to someone else's shared folder), so this isn't proof the folder is Tack
    /// Focus's; it's taken anyway because "My Drive\Tack Focus" is where Tack Focus puts its folder, and
    /// anything else there got there only by you putting it in your own My Drive (anyone else can at most
    /// share a folder with you, which shows under "Shared with me" until you add it). Two (two Google
    /// accounts) or one elsewhere (a localised "My Drive", a shared drive) are only suggested
    /// (<c>Other</c>). Directory listings only, and bounded.
    /// </summary>
    public static (string? Own, string? Other) FindGoogleDriveFolder(IEnumerable<string>? roots = null)
    {
        var mine = new List<string>();
        string? spares = null, other = null;
        foreach (string root in (roots ?? GoogleDriveRoots()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            List<string> tops;
            try { tops = Directory.EnumerateDirectories(root).Take(5000).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            // "My Drive" first, then every other first-level folder, in a stable order.
            foreach (string top in tops.OrderBy(d => string.Equals(Path.GetFileName(d), MyDrive, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                         .ThenBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(top, AppFolderName);
                if (!LocalSyncFolder.IsSyncFolder(candidate)) continue;
                bool claim;
                try { claim = LocalSyncFolder.HasSpares(candidate); }
                catch (SyncException) { claim = false; }
                if (claim && string.Equals(Path.GetFileName(top), MyDrive, StringComparison.OrdinalIgnoreCase)) mine.Add(candidate);
                else if (claim) spares ??= candidate;
                else other ??= candidate;
            }
        }
        if (mine.Count == 1) return (mine[0], null);
        return (null, mine.FirstOrDefault() ?? spares ?? other);
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
        if (NeedsLooking(root, ClaimMode))
        {
            // The listing as it is now (a cycle follows, which reads it anyway): a change that arrives
            // before the first look is then a change, not the baseline. Found live: one 36 s after a
            // start was folded into the first look's baseline and never synced.
            _lastLook = Fingerprint(root, DeviceFile.FileName(_ws.Clock.Node));
            _look.Start();
        }
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
        _look.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// Google Drive for desktop's drive (streaming mode) tells no file system watcher about files
    /// that change in the cloud: tested live, a peer's file changed on it with no event at all. So a
    /// Google Drive folder is also looked at every <see cref="LookEvery"/>: the devices folder's
    /// listing (names, sizes and times; nothing is opened or downloaded), and a cycle if it changed.
    /// Everywhere else (OneDrive, folders on a disk) the watcher alone, as ever: nothing runs while idle.
    /// </summary>
    internal static bool NeedsLooking(string root, bool claimMode)
    {
        return claimMode || IsOnGoogleDrive(root);
    }

    internal static readonly TimeSpan LookEvery = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The devices folder's listing, as one string: names, sizes and times of at most 2,000 entries that
    /// call for a cycle (<see cref="Counts"/>: device files and spares), leaving out this device's own file
    /// (<paramref name="own"/>, its file name), so this PC's own writes and temp files never start one.
    /// </summary>
    internal static string? Fingerprint(string root, string? own = null)
    {
        try
        {
            var devices = new DirectoryInfo(Path.Combine(root, "devices"));
            if (!devices.Exists) return "";
            var parts = new List<string>();
            int looked = 0;
            foreach (var f in devices.EnumerateFiles())
            {
                if (++looked > 20_000) break;
                if (!Counts(Path.Combine("devices", f.Name)) || string.Equals(f.Name, own, StringComparison.OrdinalIgnoreCase)) continue;
                parts.Add($"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}");
                if (parts.Count >= 2000) break;
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("\n", parts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // not there right now: nothing to compare
        }
    }

    /// <summary>A timer tick (<c>async void</c>): nothing in here may ever throw, or it would take the app down.</summary>
    private async void OnLook()
    {
        try
        {
            string? root = _ws.Settings.SyncFolder;
            if (root == null || _disposed || _running) return;
            string own = DeviceFile.FileName(_ws.Clock.Node);
            string? now = await Task.Run(() => Fingerprint(root, own));
            if (now == null || root != _ws.Settings.SyncFolder || _disposed) return;
            bool changed = _lastLook != null && now != _lastLook;
            _lastLook = now;
            if (changed && Enabled && !_running) Schedule(AfterFolderChange);
        }
        catch (Exception ex)
        {
            Log.Error("Looking at the sync folder failed", ex);
        }
    }

    /// <summary>
    /// Something changed in the folder (on a worker thread). Only device files and the devices folder
    /// count: not Tack's own temp files, nor anything else kept there (even Tack's data, if you put it there).
    /// </summary>
    private void OnFolderEvent(string? name)
    {
        if (name != null && !Counts(name)) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Enabled && !_running) Schedule(AfterFolderChange);
            else if (_running) _again = true;
        });
    }

    /// <summary>
    /// Does a change to <paramref name="name"/> (relative to the sync folder) call for a cycle? Device
    /// files and the devices folder; and, for claim mode, the spares Tack Focus makes (one may be what
    /// this PC is waiting for) and the marker (Tack Focus setting the folder up).
    /// </summary>
    internal static bool Counts(string name)
    {
        string file = Path.GetFileName(name);
        if (string.Equals(name, "devices", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, LocalSyncFolder.Marker, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(Path.GetDirectoryName(name), "devices", StringComparison.OrdinalIgnoreCase)
            && (DeviceFile.DeviceOfFile(file) != null || LocalSyncFolder.IsSpareName(file));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) System.Windows.Application.Current?.Dispatcher.BeginInvoke(SyncNow);
    }

    /// <summary>One cycle now, for tests (the timer that normally starts one never ticks without a dispatcher loop).</summary>
    internal Task RunOnceAsync() => RunAsync();

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
            if (!Directory.Exists(root))
            {
                SetStatus(Status with { Phase = SyncPhase.Waiting, Message = $"The sync folder isn’t there right now (is {Provider} running?). Tack will look again when you open it." });
                return;
            }
            ClaimMode = IsClaimFolder(root);
            if (!ClaimMode) DropUnclaimedRecord(FolderKey(root));
            var folder = new LocalSyncFolder(root, ClaimMode)
            {
                // Noted the moment a spare is renamed to this device's name, before anything is written
                // into it (a write that fails after the rename must never look like a file Tack Focus
                // can't see): from then on it's one Tack Focus sees. A pending retire is kept.
                Claimed = name =>
                {
                    string device = DeviceFile.DeviceOfFile(name) ?? _ws.Clock.Node;
                    SaveClaim(LoadClaim() is { } c && c.Folder == FolderKey(root)
                        ? c with { Device = device }
                        : new ClaimRecord(FolderKey(root), device));
                },
            };
            if (_watcher == null) Watch();
            // Never made here in claim mode: Tack Focus makes it (and the spares in it).
            if (ClaimMode && !Directory.Exists(folder.Devices)) throw new SyncException(SyncErrorKind.NoSpare, "No devices folder yet");
            var result = await CycleAsync(folder, root, collisionRetried: false);
            _failures = 0;
            SetStatus(new SyncStatus(SyncPhase.Synced, root, result.LastSync, null, result.Peers, result.Problems));
        }
        catch (SyncException ex) when (ex.Kind == SyncErrorKind.NoSpare)
        {
            // Not an error: Tack Focus finishes setting the folder up. Looked at again when a spare or
            // the devices folder appears (the watcher), or in a few minutes.
            _failures = 0;
            Log.Info($"Sync waiting for Tack Focus: {ex.Message}");
            retry = _again ? AfterFolderChange : SpareWait;
            _again = false;
            SetStatus(Status with { Phase = SyncPhase.Waiting, Message = OpenTackFocus });
        }
        catch (SyncException ex)
        {
            _failures++;
            Log.Info($"Sync attempt failed ({ex.Kind}): {ex.Message}");
            retry = TimeSpan.FromSeconds(Math.Min(1800, 5 * Math.Pow(2, Math.Min(_failures - 1, 10))));
            bool lasting = ex.Kind is SyncErrorKind.Full or SyncErrorKind.Denied or SyncErrorKind.TooLarge;
            if (lasting || _failures >= QuietFailures)
            {
                string message = ex.Kind switch
                {
                    SyncErrorKind.Full => "The disk is full, so Tack can’t save its sync file.",
                    SyncErrorKind.Denied => "Tack isn’t allowed to write in the sync folder.",
                    SyncErrorKind.TooLarge => "There’s more here than sync can carry, so this PC’s changes aren’t being shared.",
                    SyncErrorKind.Gone => $"The sync folder went away (is {Provider} running?). Tack will keep trying.",
                    _ => $"The {Provider} app is busy with the sync folder. Tack will keep trying.",
                };
                SetStatus(Status with { Phase = lasting ? SyncPhase.Error : SyncPhase.Waiting, Message = message });
            }
            else
            {
                // A hiccup (a file the OneDrive or Google Drive app is writing right now): quietly try again.
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
        string key = FolderKey(root);
        var meta = LoadMeta() is { } m && m.Folder == key ? m : SyncMeta.Empty(key);
        bool migrating = folder.Claim && await MigrateIfNeededAsync(folder, key, meta);
        // Asked for since the last cycle (one that fails forgets nothing: they're asked for again next time).
        var asked = _takeIn.ToList();
        foreach (string device in asked) SyncEngine.TakeIn(meta, device);
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var engine = new SyncEngine(_ws, folder, new SyncHooks
        {
            Label = () => ($"Tack for Windows on {Environment.MachineName}", $"Tack for Windows {version?.Major}.{version?.Minor}.{version?.Build}"),
            FlushAsync = () =>
            {
                _store.Flush();
                // Never tell other devices about records that aren't safely in tack.json here.
                if (_store.SaveFailed) throw new SyncException(SyncErrorKind.Server, "Tack couldn't save on this PC");
                return Task.CompletedTask;
            },
            SaveMetaAsync = SaveMetaAsync,
            // A migrating PC isn't joining: it was here all along (its old file is what it reads), and
            // nothing of its own is left behind.
            Joined = migrating ? null : () => _ws.LeaveWelcomeBehind(App.IsWelcomeTip),
        });
        try
        {
            var result = await engine.CycleAsync(meta, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await SaveMetaAsync(meta);
            _takeIn.ExceptWith(asked);
            if (folder.Claim) RetireIfDone(folder, key);
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

    // ---------------------------------------------------------------- claim mode (Google Drive)

    /// <summary>How sync.json and sync-claim.json name a folder.</summary>
    internal static string FolderKey(string root) => Path.GetFullPath(root).TrimEnd('\\').ToLowerInvariant();

    /// <summary>
    /// Is <paramref name="root"/> in claim mode? This PC has already claimed a file there (a claim
    /// record naming this folder and a device, so a marker that's gone or damaged for a while never lets
    /// Tack make files Tack Focus can't see); or the folder is on Google Drive (<see cref="IsOnGoogleDrive"/>)
    /// and its marker says <c>"spares": true</c>. Anywhere else such a marker is ignored: anyone who can
    /// write a OneDrive app folder can write it, and the folder works as a plain one, as before. Throws
    /// <see cref="SyncException"/> if the marker can't be read now.
    /// </summary>
    private bool IsClaimFolder(string root) =>
        LoadClaim() is { Device: not null } claim && claim.Folder == FolderKey(root)
        || IsOnGoogleDrive(root) && LocalSyncFolder.HasSpares(root);

    /// <summary>
    /// Is <paramref name="root"/> a folder Google Drive for desktop carries (the only place claim mode
    /// applies)? Tests point it at their temp folders, and set it back after.
    /// </summary>
    internal static Func<string, bool> IsOnGoogleDrive = OnGoogleDrive;

    /// <summary>
    /// On a drive Google Drive for desktop mounts (streaming mode, <see cref="OnGoogleDriveVolume"/>), or
    /// inside the mirror-mode root, <c>%USERPROFILE%\My Drive</c>. Also what <see cref="NeedsLooking"/> asks.
    /// </summary>
    internal static bool OnGoogleDrive(string root)
    {
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 0)
            {
                string mirror = Path.TrimEndingDirectorySeparator(Path.Combine(profile, MyDrive)) + Path.DirectorySeparatorChar;
                if (full.StartsWith(mirror, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return OnGoogleDriveVolume(full);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// On a drive Google Drive for desktop mounts (streaming mode)? Its root holds a
    /// <c>.shortcut-targets-by-id</c> folder (every Drive for desktop mount has one, beside "My Drive");
    /// or Drive for desktop's own settings name that drive letter as an account's mount point; or the
    /// volume label names it. The label alone isn't enough: Windows cuts labels to 32 characters, so
    /// "you@example.com - Google Drive" often shows as "you@example.com - Googl...".
    /// </summary>
    private static bool OnGoogleDriveVolume(string root)
    {
        try
        {
            string? drive = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrEmpty(drive)) return false;
            if (Directory.Exists(Path.Combine(drive, ".shortcut-targets-by-id"))) return true;
            if (drive.Length >= 2 && drive[1] == ':' && char.IsAsciiLetter(drive[0]) && DriveFsMountLetters().Contains(char.ToUpperInvariant(drive[0]))) return true;
            string label = new DriveInfo(drive).VolumeLabel;
            return label.Contains("Google Drive", StringComparison.OrdinalIgnoreCase) || label.Contains("- Googl", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// The drive letters Google Drive for desktop mounts accounts on, from its settings
    /// (HKCU\Software\Google\DriveFS, <c>PerAccountPreferences</c>:
    /// <c>{"per_account_preferences":[{"value":{"mount_point_path":"H"}}]}</c>). Read defensively and
    /// bounded: anything missing, too big or unexpected is simply no letters.
    /// </summary>
    internal static HashSet<char> DriveFsMountLetters()
    {
        var letters = new HashSet<char>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Google\DriveFS");
            if (key?.GetValue("PerAccountPreferences") is not string text || text.Length > 64 * 1024) return letters;
            return ParseMountLetters(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ObjectDisposedException)
        {
            return letters;
        }
    }

    /// <summary>The mount letters in Drive for desktop's <c>PerAccountPreferences</c> JSON (see <see cref="DriveFsMountLetters"/>); bounded.</summary>
    internal static HashSet<char> ParseMountLetters(string text)
    {
        var letters = new HashSet<char>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("per_account_preferences", out var list)
                || list.ValueKind != JsonValueKind.Array) return letters;
            int looked = 0;
            foreach (var account in list.EnumerateArray())
            {
                if (++looked > 64) break;
                if (account.ValueKind == JsonValueKind.Object
                    && account.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty("mount_point_path", out var mount) && mount.ValueKind == JsonValueKind.String
                    && mount.GetString() is { Length: >= 1 and <= 3 } m && char.IsAsciiLetter(m[0]) && (m.Length == 1 || m[1] == ':'))
                    letters.Add(char.ToUpperInvariant(m[0]));
            }
        }
        catch (JsonException)
        {
        }
        return letters;
    }

    /// <summary>
    /// A claim record that never got as far as a claimed file (<c>Device</c> null: a migration that
    /// found no spare it could take) for a folder that's no longer in claim mode is dropped: plain mode
    /// resumes, and only a record naming a device pins claim mode.
    /// </summary>
    private void DropUnclaimedRecord(string key)
    {
        if (LoadClaim() is not { Device: null } claim || claim.Folder != key) return;
        try
        {
            File.Delete(_claimPath);
            Log.Info("Sync: the folder is no longer in claim mode and nothing was claimed there; back to plain mode.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again next cycle; it pins nothing meanwhile.
        }
    }

    /// <summary>
    /// Claim mode: is this device's own file here one Tack Focus may not see? It's there, it isn't an
    /// unwritten spare (those are empty), and sync-claim.json doesn't say this device claimed it in this
    /// folder: written by a Tack from before claim mode, or by hand. Then: tack.json is copied to
    /// tack.before-google.json (for rollback), and this PC becomes a new device, as when two devices
    /// share an id (nothing is lost: its old file is read like any other, and everything merges), which
    /// claims a spare. Once that's written, the old file is renamed to retired-&lt;old id&gt;.json
    /// (<see cref="RetireIfDone"/>). Only if there's a spare to claim: until then its own file is
    /// written in place, as a plain folder's is.
    ///
    /// <para>
    /// Also, if this device claimed here before and its file is gone (another PC won the same spare, it
    /// was deleted, a Drive hiccup): a new device id, the same way, to claim a spare under. Never the same
    /// name again: Google Drive turns "delete, then rename to the same name" into a replace that keeps
    /// a file Tack Focus can't see. Once per missing file: the record then names the old id, so the new
    /// one just waits for a spare. True if this device took a new id.
    /// </para>
    /// </summary>
    private async Task<bool> MigrateIfNeededAsync(LocalSyncFolder folder, string key, SyncMeta meta)
    {
        string me = _ws.Clock.Node;
        var claim = LoadClaim() is { } c && c.Folder == key ? c : null;
        var own = new FileInfo(Path.Combine(folder.Devices, DeviceFile.FileName(me)));
        bool exists;
        long length;
        try
        {
            exists = own.Exists;
            length = exists ? own.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (claim?.Device == me)
        {
            if (exists || !HasSpare(folder)) return false;
            // Claimed here, and the file is gone: a new id, and a spare under it.
            Log.Info("Sync: this PC's claimed file in the Google Drive folder is gone; taking a new id and another spare.");
            await RenewAsync(meta);
            return true;
        }
        if (!exists) return false;
        if (length == 0)
        {
            // A spare this device renamed, whose note didn't get written: noted now (it's one Tack Focus made).
            SaveClaim(claim is { } pending ? pending with { Device = me } : new ClaimRecord(key, me));
            return false;
        }
        // Nothing to claim yet: written in place, as before; migrated once Tack Focus has made a spare.
        if (!HasSpare(folder)) return false;
        Log.Info("Sync: this PC's file in the Google Drive folder wasn't one Tack Focus made; taking a new id and a spare.");
        _store.Flush();
        if (_store.SaveFailed || !File.Exists(_store.FilePath)) throw new SyncException(SyncErrorKind.Server, "Tack couldn't save on this PC");
        try
        {
            // A whole copy of tack.json, old device id and all: put back, it's this PC as it was. Only
            // the first is kept: a later migration's copy would no longer be this PC from before Google Drive.
            if (!File.Exists(_backupPath)) File.Copy(_store.FilePath, _backupPath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncException(SyncErrorKind.Offline, $"Tack couldn't keep a copy of tack.json: {ex.Message}");
        }
        // Noted first, so the old file is retired even if the claim waits for a spare (no device id yet).
        SaveClaim(new ClaimRecord(key, null, me));
        await RenewAsync(meta);
        return true;
    }

    private static bool HasSpare(LocalSyncFolder folder)
    {
        try
        {
            return folder.Spares().Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A new device id, as when two devices share one: nothing is lost, its old file is read like any other.</summary>
    private async Task RenewAsync(SyncMeta meta)
    {
        _ws.Clock.Renew(Ids.NewDeviceId());
        meta.Own = new OwnFile(null, null, null);
        await SaveMetaAsync(meta);
        _store.Flush();
    }

    /// <summary>After a cycle in claim mode: an old file waiting to be retired is, once this device's claimed file is written.</summary>
    private void RetireIfDone(LocalSyncFolder folder, string key)
    {
        if (LoadClaim() is not { Retire: { } old } claim || claim.Folder != key || claim.Device != _ws.Clock.Node || old == claim.Device) return;
        if (!File.Exists(Path.Combine(folder.Devices, DeviceFile.FileName(claim.Device)))) return;
        if (folder.Retire(old)) Log.Info($"Sync: renamed this PC's old file to {LocalSyncFolder.RetiredName(old)}.");
        // Done either way: one that couldn't be renamed is left where it is (a device that no longer writes).
        SaveClaim(claim with { Retire = null });
    }

    /// <summary>sync-claim.json: small, local, and read like any file that may have been edited (bounded, validated).</summary>
    internal ClaimRecord? LoadClaim()
    {
        try
        {
            var info = new FileInfo(_claimPath);
            if (!info.Exists || info.Length > 4096) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_claimPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Str("folder") is not { Length: > 0 } folder) return null;
            string? device = Str("device") is { } d && DeviceFile.IsDeviceId(d) ? d : null;
            string? retire = Str("retire") is { } r && DeviceFile.IsDeviceId(r) ? r : null;
            return new ClaimRecord(folder, device, retire);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Written through to the disk, then swapped in, as sync.json is.</summary>
    private void SaveClaim(ClaimRecord claim)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(claim, SyncJsonContext.Default.ClaimRecord);
        string tmp = _claimPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _claimPath, overwrite: true);
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
        // Written through to the disk, then swapped in: a power cut leaves the old state or the new.
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await fs.WriteAsync(bytes);
            await fs.FlushAsync();
        }
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

/// <summary>
/// sync-claim.json, beside sync.json (local only): this device (<c>Device</c>) claimed a spare as its
/// own file in this folder (<c>Folder</c>, as sync.json names it), noted right after the rename, so Tack
/// Focus sees it. <c>Retire</c>: the id of this PC's file from before (a migration), renamed once the
/// claimed one is written. <c>Device</c> is null while a migration waits for a spare; only a record with
/// a device pins claim mode.
/// </summary>
internal sealed record ClaimRecord(
    string Folder,
    string? Device,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Retire = null);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncMeta))]
[JsonSerializable(typeof(ClaimRecord))]
internal partial class SyncJsonContext : JsonSerializerContext;
