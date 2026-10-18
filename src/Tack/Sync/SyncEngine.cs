using Tack.Model;

namespace Tack.Sync;

/// <summary>
/// Sync through a shared folder (port of web/src/sync/engine.ts, which says more). Each device
/// writes only its own file, with everything it knows; each device merges everyone else's.
/// Records merge field by field with clocks, so devices that have read the same files agree,
/// whatever the order, however late a file arrives, and however many times it's read. Deletions
/// are kept for good while sync is on, so no out-of-date copy can bring a deleted task back.
/// </summary>
internal sealed class SyncEngine(Workspace ws, ISyncFolder folder, SyncHooks hooks)
{
    /// <summary>
    /// One exchange with the folder: read what changed, merge it, and write this device's file if
    /// it now knows anything its file doesn't say. Every step can be interrupted and simply runs
    /// again next time. <paramref name="meta"/> is updated in place; save it only if this completes.
    /// </summary>
    public async Task<CycleResult> CycleAsync(SyncMeta meta, long nowMs)
    {
        string me = ws.Clock.Node;
        var result = new CycleResult();
        var entries = (await folder.ListAsync()).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

        // ---- read what changed
        var fresh = new List<DeviceFile>();
        var present = new HashSet<string>();
        FolderEntry? ownEntry = null;
        foreach (var entry in entries)
        {
            string? device = DeviceFile.DeviceOfFile(entry.Name);
            if (device == null) continue;
            if (device == me)
            {
                ownEntry = entry;
                continue;
            }
            present.Add(entry.Name);
            if (meta.Files.TryGetValue(entry.Name, out var known) && known.Tag == entry.Tag) continue;
            try
            {
                var file = DeviceFile.Parse(await folder.ReadAsync(entry), device, nowMs);
                fresh.Add(file);
                meta.Files[entry.Name] = new FileSummary(entry.Tag, device, file.Name, file.App, file.Written);
            }
            catch (DeviceFileException ex)
            {
                result.Problems.Add((device, ex.Problem));
            }
            catch (SyncException ex) when (ex.Kind == SyncErrorKind.Gone)
            {
                // Deleted between listing and reading: next time.
            }
        }
        foreach (string name in meta.Files.Keys.Where(n => !present.Contains(n)).ToList()) meta.Files.Remove(name);

        // ---- this device's own file: still ours, and as written?
        bool rewrite = ownEntry == null;
        if (ownEntry != null && ownEntry.Tag != meta.Own.Tag)
        {
            DeviceFile? own = null;
            try
            {
                own = DeviceFile.Parse(await folder.ReadAsync(ownEntry), me, nowMs);
            }
            catch (DeviceFileException)
            {
            }
            catch (SyncException ex) when (ex.Kind == SyncErrorKind.Gone)
            {
            }
            if (own != null && (own.Written == meta.Own.Written || own.Written == meta.Own.Pending))
                meta.Own = new OwnFile(ownEntry.Tag, own.Written, null);
            else if (own != null && Stamp.IsLater(own.Written, meta.Own.Written ?? Stamp.Zero))
                throw new DeviceCollisionException(me); // a copy of this device's data, syncing elsewhere
            else
                rewrite = true; // damaged, or an older version put back
        }

        // ---- merge what's new
        bool news = false;
        foreach (var file in fresh.OrderBy(f => f.Device, StringComparer.Ordinal))
        {
            if (!Stamp.IsLater(file.Written, meta.Seen.GetValueOrDefault(file.Device, Stamp.Zero))) continue;
            var seenHere = new Dictionary<string, string>(meta.Seen);
            var data = new WorkspaceData(file.Tasks, file.Sheets, file.Focus);
            // A note changed on both sides is kept twice only if neither side had seen the other's text.
            news |= ws.MergeFrom(data, null, (local, remote) =>
                !SeenBy(local.Clk.Text, file.Device, file.Seen) && !SeenBy(remote.Clk.Text, me, seenHere));
            meta.Seen[file.Device] = Later(meta.Seen.GetValueOrDefault(file.Device), file.Written);
            // Whatever that device had merged, this one now has too.
            foreach (var (d, w) in file.Seen) if (d != me) meta.Seen[d] = Later(meta.Seen.GetValueOrDefault(d), w);
            result.Merged.Add(file.Device);
        }

        if (meta.Own.Written == null && meta.Own.Pending == null && present.Count > 0) hooks.Joined?.Invoke();

        // ---- write this device's file if it knows something its file doesn't say
        bool edited = Stamp.IsLater(OwnClock(ws.ToData(), me), meta.Own.Written ?? Stamp.Zero);
        if (rewrite || edited || news)
        {
            await hooks.FlushAsync();
            string written = ws.Clock.Now();
            meta.Own = meta.Own with { Pending = written };
            await hooks.SaveMetaAsync(meta);
            var (name, app) = hooks.Label();
            byte[] bytes = DeviceFile.Write(ws.ToData(), me, name, app, written, meta.Seen);
            var entry = await folder.WriteAsync(DeviceFile.FileName(me), bytes);
            meta.Own = new OwnFile(entry.Tag, written, null);
            result.Wrote = true;
        }

        await hooks.FlushAsync();
        meta.LastSync = nowMs;
        result.Peers.AddRange(meta.Files.Values.Select(s => new PeerInfo(s.Device, s.Name, s.App, (long)Stamp.Time(s.Written))));
        return result;
    }

    /// <summary>
    /// Had <paramref name="device"/>, whose view of the others is <paramref name="seen"/>, seen this
    /// stamp? Stamps carry the id of the device that made them. Records from tack.json files
    /// (device "") count as seen by everyone.
    /// </summary>
    public static bool SeenBy(string stamp, string device, IReadOnlyDictionary<string, string> seen)
    {
        string node = Stamp.Node(stamp) ?? "";
        return node.Length == 0 || node == device || !Stamp.IsLater(stamp, seen.GetValueOrDefault(node, Stamp.Zero));
    }

    private static string Later(string? a, string? b) => Stamp.Max(a ?? Stamp.Zero, b ?? Stamp.Zero);

    /// <summary>The newest stamp this device made that's still in the workspace: anything after its last write is news.</summary>
    private static string OwnClock(WorkspaceData data, string me)
    {
        string max = Stamp.Zero;
        void Consider(string s)
        {
            if (Stamp.IsLater(s, max) && Stamp.Node(s) == me) max = s;
        }
        Consider(data.Focus.Clk);
        foreach (var t in data.Tasks)
        {
            Consider(t.Clk.Text);
            Consider(t.Clk.Completed);
            Consider(t.Clk.Order);
            Consider(t.Clk.Deleted);
        }
        foreach (var s in data.Sheets)
        {
            Consider(s.Clk.Text);
            Consider(s.Clk.Order);
            Consider(s.Clk.Deleted);
        }
        return max;
    }
}

/// <summary>The sync folder, as a provider offers it (a folder on disk here; OneDrive's API in Tack Focus).</summary>
internal interface ISyncFolder
{
    Task<IReadOnlyList<FolderEntry>> ListAsync();

    Task<byte[]> ReadAsync(FolderEntry entry);

    /// <summary>Creates or replaces a file in one go (readers see the old or the new content, never half).</summary>
    Task<FolderEntry> WriteAsync(string name, byte[] content);
}

/// <summary>A file in the sync folder. <c>Tag</c> changes whenever its content does.</summary>
internal record FolderEntry(string Name, string Tag);

internal enum SyncErrorKind { Offline, Throttled, Full, Gone, Denied, Server }

internal sealed class SyncException(SyncErrorKind kind, string message) : Exception(message)
{
    public SyncErrorKind Kind { get; } = kind;
}

/// <summary>Another device writes files under this device's id (a copied data folder): take a new id.</summary>
internal sealed class DeviceCollisionException(string device) : Exception($"Another device writes as {device}");

internal sealed class SyncHooks
{
    /// <summary>This device's name and app, for its file.</summary>
    public required Func<(string Name, string App)> Label { get; init; }

    /// <summary>Everything in the workspace is on disk (throws if it can't be saved).</summary>
    public required Func<Task> FlushAsync { get; init; }

    /// <summary>Saves the sync state (called before a write, so an interrupted one is recognised later).</summary>
    public required Func<SyncMeta, Task> SaveMetaAsync { get; init; }

    /// <summary>This device is joining a folder other devices already use.</summary>
    public Action? Joined { get; init; }
}

/// <summary>What this device keeps between cycles (sync.json, beside tack.json).</summary>
internal sealed class SyncMeta
{
    /// <summary>Which folder this is about; a different folder starts afresh.</summary>
    public string Folder { get; set; } = "";

    /// <summary>For each other device, the latest version of its file merged here (its "written").</summary>
    public Dictionary<string, string> Seen { get; set; } = new();

    /// <summary>Other devices' files, by file name, as last read.</summary>
    public Dictionary<string, FileSummary> Files { get; set; } = new();

    public OwnFile Own { get; set; } = new(null, null, null);

    /// <summary>When the last cycle completed (ms), for the status line.</summary>
    public long LastSync { get; set; }

    public static SyncMeta Empty(string folder) => new() { Folder = folder };
}

internal sealed record FileSummary(string Tag, string Device, string Name, string App, string Written);

/// <param name="Pending">A write whose answer never came back: if the folder has it, it's ours.</param>
internal sealed record OwnFile(string? Tag, string? Written, string? Pending);

internal sealed record PeerInfo(string Device, string Name, string App, long WrittenMs);

internal sealed class CycleResult
{
    public List<string> Merged { get; } = new();
    public bool Wrote { get; set; }
    public List<(string Device, DeviceFileProblem Problem)> Problems { get; } = new();
    public List<PeerInfo> Peers { get; } = new();
}
