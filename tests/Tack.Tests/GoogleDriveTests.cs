using System.IO;
using System.Text;
using System.Text.Json;
using Tack.Model;
using Tack.Services;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Sync through a Google Drive folder Tack Focus made (claim mode, docs/GOOGLE-DRIVE.md): Tack for
/// Windows never makes a file there, takes a spare as its own, moves a PC that synced there before
/// onto a claimed file without changing its data, survives two PCs taking one spare, and finds the
/// folder. All in temp folders: never this PC's Google Drive.
/// </summary>
internal static class GoogleDriveTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("google drive: claim mode makes nothing, waits for devices and a spare, then claims one and writes in place", ClaimBasics);
        yield return ("google drive: a plain chosen folder is still prepared and written as before", PlainFolderUnchanged);
        yield return ("google drive: the spare a device takes is stable, in sorted order, and spread across devices", SpareChoice);
        yield return ("google drive: the folder watcher reacts to spares and the marker, not to temp files", WatcherCounts);
        yield return ("google drive: a Google Drive folder is also looked at (its watcher hears nothing from the cloud); the listing only", Looks);
        yield return ("google drive: a PC that synced here before migrates: same records, new id, backup, retired file, claim record; once", Migrates);
        yield return ("google drive: a PC that synced here before writes in place until there's a spare, then migrates once", MigratesAfterWaiting);
        yield return ("google drive: a claim is noted at the rename, before the write, keeping the retire; a failed write never migrates again; the first backup is kept", ClaimNotedAtRename);
        yield return ("google drive: a claimed file that's gone means a new id and another spare (never the same name), once", ClaimedFileGone);
        yield return ("google drive: claim mode only on Google Drive: its mounts, Drive for desktop's mount letters, or My Drive mirrored", DetectsGoogleDrive);
        yield return ("google drive: two PCs claiming one spare at once both end up everywhere, nothing lost", SimultaneousClaims);
        yield return ("google drive: finds My Drive\\Tack Focus with spares; anything else is only suggested", FindsGoogleFolder);
    }

    // ---------------------------------------------------------------- claim mode

    private static void ClaimBasics()
    {
        using var box = new Box();
        string marker = File.ReadAllText(Path.Combine(MakeGoogleFolder(box.Root, spares: 0, devices: false), LocalSyncFolder.Marker));
        string[] Top() => Directory.EnumerateFileSystemEntries(box.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

        Check.Equal<string?>(null, box.Run(() => box.Sync.TurnOnAsync(box.Root)), "turned on");
        Check.True(box.Sync.ClaimMode, "claim mode");
        Check.Equal(SyncPhase.Waiting, box.Sync.Status.Phase, "no devices folder: waiting");
        Check.Equal(FolderSync.OpenTackFocus, box.Sync.Status.Message, "says to open Tack Focus");
        Check.Equal("tack-sync.json", string.Join(",", Top()), "nothing made: no devices, no README");
        Check.Equal(marker, File.ReadAllText(Path.Combine(box.Root, LocalSyncFolder.Marker)), "the marker untouched");
        Check.True(!File.Exists(box.ClaimPath), "no claim record yet");

        Directory.CreateDirectory(box.Devices);
        box.Run(() => box.Sync.RunOnceAsync());
        Check.Equal(SyncPhase.Waiting, box.Sync.Status.Phase, "no spare: waiting");
        Check.Equal(FolderSync.OpenTackFocus, box.Sync.Status.Message, "says to open Tack Focus, not an error");
        Check.Equal(0, Directory.EnumerateFileSystemEntries(box.Devices).Count(), "nothing made in devices");

        for (int i = 1; i <= 3; i++) File.WriteAllBytes(Path.Combine(box.Devices, Spare(i)), []);
        box.Ws.AddTask("From the PC");
        box.Run(() => box.Sync.RunOnceAsync());
        Check.Equal(SyncPhase.Synced, box.Sync.Status.Phase, $"synced ({box.Sync.Status.Message})");
        string me = box.Ws.Clock.Node;
        string own = Path.Combine(box.Devices, DeviceFile.FileName(me));
        Check.True(File.Exists(own), "its own file, claimed");
        Check.Equal(2, Directory.EnumerateFiles(box.Devices, "spare-*").Count(), "one spare taken");
        Check.Equal(3, Directory.EnumerateFileSystemEntries(box.Devices).Count(), "no temp file or anything else left");
        Check.Equal("devices,tack-sync.json", string.Join(",", Top()), "still nothing made beside devices");
        var file = DeviceFile.Parse(File.ReadAllBytes(own), me, Now());
        Check.True(file.Tasks.Any(t => t.Text == "From the PC"), "written as a device file");
        var claim = box.Sync.LoadClaim();
        Check.Equal(FolderSync.FolderKey(box.Root), claim?.Folder, "claim record: the folder");
        Check.Equal(me, claim?.Device, "claim record: the device");

        // The next write goes over that same file: no second spare taken.
        box.Ws.AddTask("Later");
        box.Run(() => box.Sync.RunOnceAsync());
        Check.Equal(SyncPhase.Synced, box.Sync.Status.Phase, "synced again");
        Check.Equal(2, Directory.EnumerateFiles(box.Devices, "spare-*").Count(), "written in place");
        Check.True(DeviceFile.Parse(File.ReadAllBytes(own), me, Now()).Tasks.Any(t => t.Text == "Later"), "the new task in the same file");
        Check.Equal(me, box.Ws.Clock.Node, "same id: no migration");
    }

    private static void PlainFolderUnchanged()
    {
        using var box = new Box();
        Directory.CreateDirectory(box.Root);
        box.Ws.AddTask("Plain");
        Check.Equal<string?>(null, box.Run(() => box.Sync.TurnOnAsync(box.Root)), "turned on");
        Check.True(!box.Sync.ClaimMode, "not claim mode");
        Check.Equal("OneDrive", box.Sync.Provider, "says OneDrive");
        Check.True(File.Exists(Path.Combine(box.Root, "README.txt")) && File.Exists(Path.Combine(box.Root, LocalSyncFolder.Marker)), "prepared as before");
        Check.True(File.Exists(Path.Combine(box.Devices, DeviceFile.FileName(box.Ws.Clock.Node))), "its own file made");
        Check.True(!File.Exists(box.ClaimPath), "no claim record");
    }

    private static void SpareChoice()
    {
        Check.Equal(LocalSyncFolder.SpareIndex("pcAAAAAA", 3), LocalSyncFolder.SpareIndex("pcAAAAAA", 3), "stable");
        // The same as Tack Focus would compute: the first 4 bytes of SHA-256, big-endian, modulo the count.
        byte[] hash = System.Security.Cryptography.SHA256.HashData("pcAAAAAA"u8);
        Check.Equal((int)(((uint)hash[0] << 24 | (uint)hash[1] << 16 | (uint)hash[2] << 8 | hash[3]) % 7), LocalSyncFolder.SpareIndex("pcAAAAAA", 7), "the first 4 bytes of SHA-256");
        var counts = new int[3];
        for (int i = 0; i < 600; i++) counts[LocalSyncFolder.SpareIndex(Ids.NewDeviceId(), 3)]++;
        Check.True(counts.All(c => c > 120), $"spread across spares ({string.Join(",", counts)})");

        using var temp = new TempDir();
        MakeGoogleFolder(temp.Path, spares: 0);
        string devices = Path.Combine(temp.Path, "devices");
        // Made out of order: picked from the sorted list.
        var names = new[] { Spare(0xc0ffee), Spare(0x1), Spare(0xbeef), Spare(0xa) };
        foreach (string n in names) File.WriteAllBytes(Path.Combine(devices, n), []);
        var sorted = names.Order(StringComparer.Ordinal).ToList();
        var folder = new LocalSyncFolder(temp.Path, claim: true);
        Check.Sequence(sorted, folder.Spares(), "spares, sorted");
        string expected = sorted[LocalSyncFolder.SpareIndex("pcAAAAAA", sorted.Count)];
        folder.WriteAsync("pcAAAAAA.json", "{}"u8.ToArray()).GetAwaiter().GetResult();
        Check.True(!File.Exists(Path.Combine(devices, expected)), $"took the spare its id picks ({expected})");
        Check.Equal(3, folder.Spares().Count, "and only that one");
    }

    private static void WatcherCounts()
    {
        Check.True(FolderSync.Counts(Path.Combine("devices", Spare(7))), "a spare appearing");
        Check.True(FolderSync.Counts(Path.Combine("devices", "pcAAAAAA.json")), "a device file");
        Check.True(FolderSync.Counts("devices"), "the devices folder");
        Check.True(FolderSync.Counts("tack-sync.json"), "the marker");
        Check.True(!FolderSync.Counts(Path.Combine("devices", "~$pcAAAAAA.json.tmp")), "not a temp file");
        Check.True(!FolderSync.Counts(Path.Combine("devices", "spare-0123.json")), "not a near-spare");
        Check.True(!FolderSync.Counts(Path.Combine("devices", "retired-pcAAAAAA.json")), "not a retired file");
        Check.True(!FolderSync.Counts(Spare(7)), "not a spare outside devices");
    }

    private static void Looks()
    {
        using var temp = new TempDir();
        Check.True(FolderSync.NeedsLooking(temp.Path, claimMode: true), "claim mode: looked at");
        Check.True(!FolderSync.NeedsLooking(temp.Path, claimMode: false), "a folder on a disk: the watcher alone, as ever");

        Check.Equal("", FolderSync.Fingerprint(temp.Path), "no devices folder yet");
        string devices = Path.Combine(temp.Path, "devices");
        Directory.CreateDirectory(devices);
        string peer = Path.Combine(devices, "pcBBBBBB.json");
        File.WriteAllText(peer, "{}");
        string? before = FolderSync.Fingerprint(temp.Path);
        Check.Equal(before, FolderSync.Fingerprint(temp.Path), "unchanged: the same");
        // A peer's file rewritten by the Drive app: a new size and time, nothing opened to tell.
        File.WriteAllText(peer, "{ \"changed\": true }");
        File.SetLastWriteTimeUtc(peer, DateTime.UtcNow.AddMinutes(1));
        Check.True(FolderSync.Fingerprint(temp.Path) != before, "a changed file: a different listing");
        string? after = FolderSync.Fingerprint(temp.Path);
        File.WriteAllBytes(Path.Combine(devices, Spare(3)), []);
        Check.True(FolderSync.Fingerprint(temp.Path) != after, "a new spare: a different listing");

        // This PC's own writes never start a cycle: its temp files, its own file, nor anything else kept there.
        string own = Path.Combine(devices, "pcAAAAAA.json");
        File.WriteAllText(own, "{}");
        string? mine = FolderSync.Fingerprint(temp.Path, "pcAAAAAA.json");
        File.WriteAllText(Path.Combine(devices, "~$pcAAAAAA.json.tmp"), "half");
        Check.Equal(mine, FolderSync.Fingerprint(temp.Path, "pcAAAAAA.json"), "its temp file: the same listing");
        File.WriteAllText(own, "{ \"written\": \"again\" }");
        File.SetLastWriteTimeUtc(own, DateTime.UtcNow.AddMinutes(2));
        Check.Equal(mine, FolderSync.Fingerprint(temp.Path, "pcAAAAAA.json"), "its own file rewritten: the same listing");
        File.WriteAllText(Path.Combine(devices, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(devices, LocalSyncFolder.RetiredName("pcCCCCCC")), "{}");
        File.WriteAllBytes(Path.Combine(devices, "spare-0123.json"), []);
        Check.Equal(mine, FolderSync.Fingerprint(temp.Path, "pcAAAAAA.json"), "other files, retired files, near-spares: the same listing");
        File.Delete(Path.Combine(devices, Spare(3)));
        Check.True(FolderSync.Fingerprint(temp.Path, "pcAAAAAA.json") != mine, "a spare taken: a different listing");
    }

    private static void DetectsGoogleDrive()
    {
        using var temp = new TempDir();
        Check.True(!FolderSync.OnGoogleDrive(temp.Path), "a temp folder on this PC's disk: not Google Drive");
        // Mirror mode: inside %USERPROFILE%\My Drive (the path only; nothing there is looked at).
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Check.True(FolderSync.OnGoogleDrive(Path.Combine(profile, "My Drive", "Tack Focus")), "My Drive mirrored: Google Drive");
        Check.True(!FolderSync.OnGoogleDrive(Path.Combine(profile, "My Drive2", "Tack Focus")), "a folder merely named like it: no");
        Check.True(!FolderSync.OnGoogleDrive(Path.Combine(profile, "OneDrive", "Apps", "Tack Focus")), "OneDrive: no");
        Check.True(!FolderSync.OnGoogleDrive(@"\\server\share\Tack Focus"), "a share: no, no error");
        // Drive for desktop's settings: the letters it mounts accounts on, read defensively.
        string prefs = "{\"per_account_preferences\":[{\"key\":\"1\",\"value\":{\"mount_point_path\":\"H\"}},{\"key\":\"2\",\"value\":{\"mount_point_path\":\"i:\"}}]}";
        Check.Equal("H,I", string.Join(",", FolderSync.ParseMountLetters(prefs).Order()), "mount letters");
        foreach (string bad in new[] { "", "not json", "[]", "{\"per_account_preferences\":{}}", "{\"per_account_preferences\":[1,{\"value\":{\"mount_point_path\":7}},{\"value\":{\"mount_point_path\":\"..\\\\x\"}},{\"value\":{\"mount_point_path\":\"\"}}]}", new string('[', 10_000) })
            Check.Equal(0, FolderSync.ParseMountLetters(bad).Count, $"no letters, no error: {bad[..Math.Min(bad.Length, 40)]}");
    }

    // ---------------------------------------------------------------- migration

    private static void Migrates()
    {
        using var box = new Box();
        MakeGoogleFolder(box.Root, spares: 3);
        Fill(box.Ws);
        box.Store.Flush();
        string old = box.Ws.Clock.Node;
        OlderTackSynced(box);
        var before = SyncTests.Sorted(box.Ws.ToData());
        byte[] oldFile = File.ReadAllBytes(Path.Combine(box.Devices, DeviceFile.FileName(old)));

        Check.Equal<string?>(null, box.Run(() => box.Sync.TurnOnAsync(box.Root)), "turned on");
        Check.Equal(SyncPhase.Synced, box.Sync.Status.Phase, $"synced ({box.Sync.Status.Message})");
        string me = box.Ws.Clock.Node;
        Check.True(me != old && DeviceFile.IsDeviceId(me), "a new device id");
        Check.Data(before, SyncTests.Sorted(box.Ws.ToData()), "the same records, deletions, notes and spaces");

        // tack.before-google.json: a whole tack.json, under the old id, with the same records.
        string backup = Path.Combine(box.Data, "tack.before-google.json");
        Check.True(File.Exists(backup), "tack.before-google.json kept");
        var doc = JsonSerializer.Deserialize(File.ReadAllBytes(backup), StateJsonContext.Default.StateDocument)!;
        Check.Equal(old, StateFile.Device(doc), "the backup is this PC as it was (old id)");
        var saved = StateFile.ToData(doc, new Hlc(old), Now(), Now());
        Check.Equal(string.Join(",", before.Tasks.Select(t => t.Id)), string.Join(",", SyncTests.Sorted(saved).Tasks.Select(t => t.Id)), "the backup's tasks");
        Check.Equal(string.Join(",", before.Sheets.Select(t => t.Text)), string.Join(",", SyncTests.Sorted(saved).Sheets.Select(t => t.Text)), "the backup's notes");
        Check.Equal(me, StateFile.Device(JsonSerializer.Deserialize(File.ReadAllBytes(box.Store.FilePath), StateJsonContext.Default.StateDocument)!), "tack.json has the new id");

        // The folder: a spare claimed and written, the old file retired (kept, as it was).
        Check.True(!File.Exists(Path.Combine(box.Devices, DeviceFile.FileName(old))), "the old file is gone from its name");
        string retired = Path.Combine(box.Devices, LocalSyncFolder.RetiredName(old));
        Check.True(File.Exists(retired) && File.ReadAllBytes(retired).AsSpan().SequenceEqual(oldFile), "renamed to retired-<old id>.json, unchanged");
        Check.Equal(2, Directory.EnumerateFiles(box.Devices, "spare-*").Count(), "one spare taken");
        var mine = DeviceFile.Parse(File.ReadAllBytes(Path.Combine(box.Devices, DeviceFile.FileName(me))), me, Now());
        var was = DeviceFile.Parse(oldFile, old, Now());
        Check.Equal(RecordIds(was), RecordIds(mine), "the new file carries every record the old one did");
        Check.True(mine.Seen.ContainsKey(old), "and says it has seen everything the old id wrote");
        var claim = box.Sync.LoadClaim();
        Check.Equal(new ClaimRecord(FolderSync.FolderKey(box.Root), me), claim, "claim record: this folder, the new id, nothing left to retire");

        // Once only.
        box.Ws.AddTask("After the move");
        box.Run(() => box.Sync.RunOnceAsync());
        Check.Equal(SyncPhase.Synced, box.Sync.Status.Phase, "synced again");
        Check.Equal(me, box.Ws.Clock.Node, "no second migration");
        Check.Equal(2, Directory.EnumerateFiles(box.Devices, "spare-*").Count(), "no second spare");
        Check.Equal(1, Directory.EnumerateFiles(box.Devices, "retired-*").Count(), "one retired file");
    }

    private static void MigratesAfterWaiting()
    {
        using var box = new Box();
        MakeGoogleFolder(box.Root, spares: 0);
        Fill(box.Ws);
        box.Store.Flush();
        string old = box.Ws.Clock.Node;
        OlderTackSynced(box);
        var before = SyncTests.Sorted(box.Ws.ToData());

        box.Run(() => box.Sync.TurnOnAsync(box.Root));
        // No spare to claim: nothing started, its file written in place as before, nothing alarming.
        Check.Equal(SyncPhase.Synced, box.Sync.Status.Phase, $"synced in place ({box.Sync.Status.Message})");
        Check.Equal<string?>(null, box.Sync.Status.Message, "nothing said");
        Check.Equal(old, box.Ws.Clock.Node, "the same id");
        Check.True(!File.Exists(Path.Combine(box.Data, "tack.before-google.json")), "no backup: no migration");
        Check.True(!File.Exists(box.ClaimPath), "no claim record");
        box.Ws.AddTask("While waiting");
        box.Run(() => box.Sync.RunOnceAsync());
        Check.Equal(old, box.Ws.Clock.Node, "still the same id");
        var inPlace = DeviceFile.Parse(File.ReadAllBytes(Path.Combine(box.Devices, DeviceFile.FileName(old))), old, Now());
        Check.True(inPlace.Tasks.Any(t => t.Text == "While waiting"), "its file written in place");
        before = SyncTests.Sorted(box.Ws.ToData());

        File.WriteAllBytes(Path.Combine(box.Devices, Spare(42)), []);
        box.Run(() => box.Sync.RunOnceAsync());
        Check.Equal(SyncPhase.Synced, box.Sync.Status.Phase, $"synced ({box.Sync.Status.Message})");
        string me = box.Ws.Clock.Node;
        Check.True(me != old, "a spare: migrated, a new id");
        Check.True(File.Exists(Path.Combine(box.Data, "tack.before-google.json")), "backup kept");
        Check.True(File.Exists(Path.Combine(box.Devices, DeviceFile.FileName(me))), "the spare claimed");
        Check.True(File.Exists(Path.Combine(box.Devices, LocalSyncFolder.RetiredName(old))), "the old file retired");
        Check.Equal(new ClaimRecord(FolderSync.FolderKey(box.Root), me), box.Sync.LoadClaim(), "claim record");
        Check.Data(before, SyncTests.Sorted(box.Ws.ToData()), "the same records");
        box.Run(() => box.Sync.RunOnceAsync());
        Check.Equal(me, box.Ws.Clock.Node, "once");
    }

    /// <summary>
    /// The rename happens, then the write fails (here: the spares are read-only, so the rename works and
    /// writing over the claimed file doesn't). The claim is noted already, with the old file still to
    /// retire, so the next cycle writes into the same file: no second migration, the old file retired.
    /// </summary>
    private static void ClaimNotedAtRename()
    {
        using var box = new Box();
        MakeGoogleFolder(box.Root, spares: 2);
        foreach (string spare in Directory.EnumerateFiles(box.Devices, "spare-*")) File.SetAttributes(spare, FileAttributes.ReadOnly);
        Fill(box.Ws);
        box.Store.Flush();
        string old = box.Ws.Clock.Node;
        OlderTackSynced(box);
        // A copy from an earlier migration: the true pre-Google tack.json, never written over.
        string backup = Path.Combine(box.Data, "tack.before-google.json");
        File.WriteAllText(backup, "the first copy");
        string key = FolderSync.FolderKey(box.Root);
        try
        {
            box.Run(() => box.Sync.TurnOnAsync(box.Root));
            string me = box.Ws.Clock.Node;
            string own = Path.Combine(box.Devices, DeviceFile.FileName(me));
            Check.True(me != old, "migrating: a new id");
            Check.True(box.Sync.Status.Phase != SyncPhase.Synced, "the write failed");
            Check.True(File.Exists(own) && new FileInfo(own).Length == 0, "the spare renamed, nothing written into it");
            Check.Equal(new ClaimRecord(key, me, old), box.Sync.LoadClaim(), "the claim noted at the rename, the retire kept");
            Check.Equal("the first copy", File.ReadAllText(backup), "the first backup kept");

            File.SetAttributes(own, FileAttributes.Normal);
            box.Run(() => box.Sync.RunOnceAsync());
            Check.Equal(SyncPhase.Synced, box.Sync.Status.Phase, $"synced ({box.Sync.Status.Message})");
            Check.Equal(me, box.Ws.Clock.Node, "no second migration");
            Check.True(new FileInfo(own).Length > 0, "written into the same file");
            Check.Equal(1, Directory.EnumerateFiles(box.Devices, "spare-*").Count(), "no second spare taken");
            Check.True(File.Exists(Path.Combine(box.Devices, LocalSyncFolder.RetiredName(old))), "the old file retired");
            Check.Equal(new ClaimRecord(key, me), box.Sync.LoadClaim(), "nothing left to retire");
            Check.Equal("the first copy", File.ReadAllText(backup), "the first backup still kept");
        }
        finally
        {
            foreach (string f in Directory.EnumerateFiles(box.Devices)) File.SetAttributes(f, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// This PC claimed a file, then it went (another PC won the same spare, or it was deleted). Google
    /// Drive turns "delete, then rename to the same name" into a replace that keeps a file Tack Focus
    /// can't see, so it takes a new id and another spare: once, and with no spare it waits.
    /// </summary>
    private static void ClaimedFileGone()
    {
        using var box = new Box();
        MakeGoogleFolder(box.Root, spares: 3);
        box.Ws.AddTask("Mine");
        box.Run(() => box.Sync.TurnOnAsync(box.Root));
        Check.Equal(SyncPhase.Synced, box.Sync.Status.Phase, $"synced ({box.Sync.Status.Message})");
        string first = box.Ws.Clock.Node;
        string key = FolderSync.FolderKey(box.Root);
        Check.Equal(new ClaimRecord(key, first), box.Sync.LoadClaim(), "claimed");

        File.Delete(Path.Combine(box.Devices, DeviceFile.FileName(first)));
        box.Ws.AddTask("After");
        box.Run(() => box.Sync.RunOnceAsync());
        Check.Equal(SyncPhase.Synced, box.Sync.Status.Phase, $"synced again ({box.Sync.Status.Message})");
        string second = box.Ws.Clock.Node;
        Check.True(second != first && DeviceFile.IsDeviceId(second), "a new id");
        Check.True(!File.Exists(Path.Combine(box.Devices, DeviceFile.FileName(first))), "never the same name again");
        var file = DeviceFile.Parse(File.ReadAllBytes(Path.Combine(box.Devices, DeviceFile.FileName(second))), second, Now());
        Check.Equal("After,Mine", string.Join(",", file.Tasks.Select(t => t.Text).Order()), "everything in the new file");
        Check.Equal(new ClaimRecord(key, second), box.Sync.LoadClaim(), "the claim under the new id");
        Check.Equal(1, Directory.EnumerateFiles(box.Devices, "spare-*").Count(), "one more spare taken");
        for (int i = 0; i < 3; i++) box.Run(() => box.Sync.RunOnceAsync());
        Check.Equal(second, box.Ws.Clock.Node, "once: the file is there, no more new ids");

        // Gone again, with no spare to take: waits, under the same id, however often it looks.
        File.Delete(Path.Combine(box.Devices, DeviceFile.FileName(second)));
        foreach (string spare in Directory.EnumerateFiles(box.Devices, "spare-*")) File.Delete(spare);
        for (int i = 0; i < 3; i++)
        {
            box.Run(() => box.Sync.RunOnceAsync());
            Check.Equal(SyncPhase.Waiting, box.Sync.Status.Phase, "no spare: waiting");
            Check.Equal(second, box.Ws.Clock.Node, "no new id without a spare");
        }
        File.WriteAllBytes(Path.Combine(box.Devices, Spare(77)), []);
        box.Run(() => box.Sync.RunOnceAsync());
        string third = box.Ws.Clock.Node;
        Check.Equal(SyncPhase.Synced, box.Sync.Status.Phase, $"a spare: synced ({box.Sync.Status.Message})");
        Check.True(third != second && File.Exists(Path.Combine(box.Devices, DeviceFile.FileName(third))), "a new id, the spare claimed");
        box.Run(() => box.Sync.RunOnceAsync());
        Check.Equal(third, box.Ws.Clock.Node, "and only one");
    }

    /// <summary>Tasks (one done, one deleted), notes and a space.</summary>
    private static void Fill(Workspace ws)
    {
        ws.AddTask("Buy milk");
        var done = ws.AddTask("Done already");
        ws.Complete(done);
        var gone = ws.AddTask("Deleted");
        ws.Delete(gone);
        ws.AddSheet().Text = "Plan\n- one";
        ws.AddSpace("Work");
    }

    /// <summary>A Tack from before claim mode synced through this folder: its own file made on the PC, and its sync.json.</summary>
    private static void OlderTackSynced(Box box)
    {
        var meta = SyncMeta.Empty(FolderSync.FolderKey(box.Root));
        new SyncEngine(box.Ws, new LocalSyncFolder(box.Root), new SyncHooks
        {
            Label = () => ("Tack for Windows on TEST", "Tack for Windows 1.9.0"),
            FlushAsync = () => Task.CompletedTask,
            SaveMetaAsync = _ => Task.CompletedTask,
        }).CycleAsync(meta, Now()).GetAwaiter().GetResult();
        File.WriteAllBytes(Path.Combine(box.Data, "sync.json"), JsonSerializer.SerializeToUtf8Bytes(meta, SyncJsonContext.Default.SyncMeta));
    }

    private static string RecordIds(DeviceFile f) =>
        string.Join(",", f.Tasks.Select(t => t.Id).Concat(f.Sheets.Select(s => s.Id)).Concat(f.Spaces.Select(s => s.Id)).Order(StringComparer.Ordinal));

    // ---------------------------------------------------------------- two PCs on one spare

    /// <summary>
    /// Two PCs rename the same spare at the same moment. Google Drive keeps one rename: on the PCs, the
    /// loser's file is renamed to the winner's name (with the loser's bytes in it), and the loser's is gone.
    /// Both find out on their next cycle (the engine's own-file check) and nothing is lost.
    /// </summary>
    private static void SimultaneousClaims()
    {
        using var temp = new TempDir();
        MakeGoogleFolder(temp.Path, spares: 2);
        long wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var a = new ClaimPc("pcAAAAAA", temp.Path, () => wall);
        var b = new ClaimPc("pcBBBBBB", temp.Path, () => wall);
        a.Ws.AddTask("From A");
        b.Ws.AddTask("From B");
        string devices = Path.Combine(temp.Path, "devices");
        string fileA = Path.Combine(devices, "pcAAAAAA.json"), fileB = Path.Combine(devices, "pcBBBBBB.json");

        a.Sync();
        b.Sync();
        Check.True(File.Exists(fileA) && File.Exists(fileB), "both claimed (as each PC saw it)");
        // Drive keeps A's rename of the spare: B's file goes, and its bytes land in A's file.
        File.Move(fileB, fileA, overwrite: true);
        // Tack Focus makes spares again.
        File.WriteAllBytes(Path.Combine(devices, Spare(0xfeed)), []);
        File.WriteAllBytes(Path.Combine(devices, Spare(0xface)), []);

        for (int i = 0; i < 3; i++)
        {
            wall += 1000;
            a.Sync();
            b.Sync();
        }
        Check.True(File.Exists(fileA) && File.Exists(fileB), "both files there again");
        Check.Equal("pcAAAAAA", DeviceIn(fileA), "A's file is A's again");
        Check.Equal("pcBBBBBB", DeviceIn(fileB), "B claimed another spare");
        Check.Equal("From A,From B", string.Join(",", a.Ws.Open.Select(t => t.Text).Order()), "A has both");
        Check.Equal("From A,From B", string.Join(",", b.Ws.Open.Select(t => t.Text).Order()), "B has both");
        Check.Equal(Json.Views(a.Ws), Json.Views(b.Ws), "both PCs show the same");
        // Also the other way: the winner's bytes left in place, the loser's file just gone.
        File.Delete(fileB);
        b.Ws.AddTask("B again");
        for (int i = 0; i < 2; i++)
        {
            wall += 1000;
            b.Sync();
            a.Sync();
        }
        Check.True(a.Ws.Open.Any(t => t.Text == "B again"), "B's later task reaches A after it claims once more");
    }

    // ---------------------------------------------------------------- finding the folder

    private static void FindsGoogleFolder()
    {
        using var temp = new TempDir();
        string g = Path.Combine(temp.Path, "G");
        string h = Path.Combine(temp.Path, "H");
        Directory.CreateDirectory(Path.Combine(g, "My Drive", "Taxes"));
        Directory.CreateDirectory(Path.Combine(g, "Shared drives"));
        Directory.CreateDirectory(h);
        Check.Equal<(string?, string?)>((null, null), FolderSync.FindGoogleDriveFolder([g, h]), "nothing yet");
        Check.Equal<(string?, string?)>((null, null), FolderSync.FindGoogleDriveFolder([Path.Combine(temp.Path, "missing")]), "a root that isn't there");

        string Marked(string root, string top, bool spares)
        {
            string dir = Path.Combine(root, top, "Tack Focus");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, LocalSyncFolder.Marker), spares ? "{\"format\":\"tack-sync\",\"version\":1,\"spares\":true}" : "{}");
            return dir;
        }
        // A localised My Drive, a shared drive: only suggested.
        string localised = Marked(g, "Meine Ablage", spares: true);
        Check.Equal<(string?, string?)>((null, localised), FolderSync.FindGoogleDriveFolder([g, h]), "under a localised My Drive: a suggestion");
        string noSpares = Marked(h, "My Drive", spares: false);
        Check.Equal<(string?, string?)>((null, localised), FolderSync.FindGoogleDriveFolder([g, h]), "spares suggested before a plain marker");
        Check.Equal<(string?, string?)>((null, noSpares), FolderSync.FindGoogleDriveFolder([h]), "a marker without spares: a suggestion");
        string own = Marked(g, "My Drive", spares: true);
        Check.Equal<(string?, string?)>((own, null), FolderSync.FindGoogleDriveFolder([g, h]), "My Drive\\Tack Focus with spares: taken by itself");
        // Two accounts, each with one: you choose.
        string second = Marked(h, Path.Combine("My Drive"), spares: true);
        Check.True(second == noSpares, "same folder, now with spares");
        var (none, suggested) = FolderSync.FindGoogleDriveFolder([g, h]);
        Check.True(none == null && suggested != null, "two Google accounts with the folder: only suggested");
        // Deeper than the first level is never looked at.
        Directory.CreateDirectory(Path.Combine(temp.Path, "I", "My Drive", "Deep"));
        Marked(Path.Combine(temp.Path, "I", "My Drive"), "Deep", spares: true);
        Check.Equal<(string?, string?)>((null, null), FolderSync.FindGoogleDriveFolder([Path.Combine(temp.Path, "I")]), "only <root>\\<first-level>\\Tack Focus");
    }

    // ---------------------------------------------------------------- helpers

    private static string? DeviceIn(string path) => JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.GetProperty("device").GetString();

    internal static string Spare(long n) => $"spare-{n:x16}.json";

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A folder as Tack Focus makes it in Google Drive: marker with spares, README, devices with empty spares.</summary>
    internal static string MakeGoogleFolder(string root, int spares, bool devices = true)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, LocalSyncFolder.Marker), "{\"format\":\"tack-sync\",\"version\":1,\"spares\":true}");
        if (!devices) return root;
        File.WriteAllText(Path.Combine(root, "README.txt"), "Made by Tack Focus");
        Directory.CreateDirectory(Path.Combine(root, "devices"));
        for (int i = 1; i <= spares; i++) File.WriteAllBytes(Path.Combine(root, "devices", Spare(0x1000 + i)), []);
        return root;
    }

    /// <summary>A PC syncing through a claim-mode folder, as FolderSync does it (engine and folder).</summary>
    internal sealed class ClaimPc
    {
        private readonly SyncMeta _meta;

        public ClaimPc(string id, string root, Func<long> wall)
        {
            Ws = new Workspace(new Settings(), new Hlc(id, wall), null, () => DateTimeOffset.FromUnixTimeMilliseconds(wall()));
            Folder = new LocalSyncFolder(root, claim: true);
            _meta = SyncMeta.Empty(root);
            Wall = wall;
        }

        public Workspace Ws { get; }

        public LocalSyncFolder Folder { get; }

        private Func<long> Wall { get; }

        /// <summary>One cycle; a wait for a spare (or a hiccup) is what FolderSync would retry.</summary>
        public SyncErrorKind? Sync()
        {
            var engine = new SyncEngine(Ws, Folder, new SyncHooks
            {
                Label = () => (Ws.Clock.Node, "Tack for Windows test"),
                FlushAsync = () => Task.CompletedTask,
                SaveMetaAsync = _ => Task.CompletedTask,
            });
            try
            {
                engine.CycleAsync(_meta, Wall()).GetAwaiter().GetResult();
                return null;
            }
            catch (SyncException ex)
            {
                return ex.Kind;
            }
        }
    }

    /// <summary>A FolderSync over a temp data folder ("data", never %LOCALAPPDATA%\Tack) and a temp sync folder ("drive").</summary>
    internal sealed class Box : IDisposable
    {
        private readonly TempDir _temp = new();
        private readonly Func<string, bool> _wasOnGoogleDrive = FolderSync.IsOnGoogleDrive;

        /// <param name="onGoogleDrive">Whether the temp "drive" folder counts as Google Drive's (claim mode
        /// applies only there); false is a folder anywhere else, such as OneDrive's.</param>
        public Box(bool onGoogleDrive = true)
        {
            Data = Path.Combine(_temp.Path, "data");
            Root = Path.Combine(_temp.Path, "drive", "Tack Focus");
            string drive = FolderSync.FolderKey(Path.Combine(_temp.Path, "drive")) + "\\";
            FolderSync.IsOnGoogleDrive = path => onGoogleDrive && FolderSync.FolderKey(path).StartsWith(drive, StringComparison.Ordinal);
            Store = new StateStore(Data);
            Ws = Store.Attach(new StateDocument { Settings = new Settings { Hotkey = "" } });
            Sync = new FolderSync(Ws, Store);
        }

        public string Data { get; }
        public string Root { get; }
        public string Devices => Path.Combine(Root, "devices");
        public string ClaimPath => Path.Combine(Data, "sync-claim.json");
        public StateStore Store { get; }
        public Workspace Ws { get; }
        public FolderSync Sync { get; }

        /// <summary>Runs FolderSync's async work to the end here (no dispatcher loop: continuations run on the pool).</summary>
        public T Run<T>(Func<Task<T>> work)
        {
            var saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try { return Task.Run(work).GetAwaiter().GetResult(); }
            finally { SynchronizationContext.SetSynchronizationContext(saved); }
        }

        public void Run(Func<Task> work) => Run(async () => { await work(); return 0; });

        /// <summary>Writes sync-claim.json as a Tack would have left it.</summary>
        public void WriteClaim(ClaimRecord claim) =>
            File.WriteAllBytes(ClaimPath, JsonSerializer.SerializeToUtf8Bytes(claim, SyncJsonContext.Default.ClaimRecord));

        public void Dispose()
        {
            FolderSync.IsOnGoogleDrive = _wasOnGoogleDrive;
            Sync.Dispose();
            Store.Flush();
            _temp.Dispose();
        }
    }

    internal sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("tack-google-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
