using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tack.Model;
using Tack.Services;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Spaces (Work, Dev, Personal...), the groundwork: the new field on tasks and sheets, space
/// records, their merge, files, workspace operations and sync (the same cases as Tack Focus's
/// own tests; tests/vectors check the two apps agree). Nothing on screen uses them yet.
/// </summary>
internal static class SpacesTests
{
    private static readonly long Now = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private const long Day = 86_400_000;
    private const string Date = "2026-10-21T09:00:00.000Z";

    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("spaces, merge: a task's or sheet's space, the later clock wins", MergeLaterWins);
        yield return ("spaces, merge: a copy from a version without spaces never undoes an assignment", MergeOldCopy);
        yield return ("spaces, merge: equal clocks, different spaces: both sides pick the same", MergeTie);
        yield return ("spaces, merge: a note's conflict copy stays in the losing text's space", MergeConflictCopy);
        yield return ("spaces, merge: space records field by field, in any order", MergeSpaceRecords);
        yield return ("spaces, readers: a missing, invalid or implausible space or clock means Main; the record is still read", ReadersNormalise);
        yield return ("spaces, readers: space records, valid and not; names are one line", ReadersSpaceRecords);
        yield return ("spaces, device file: a space name over 100 characters is cut to 100; the space still comes in", DeviceFileLongName);
        yield return ("spaces, device file: round trip with spaces, moves and deletions", DeviceFileRoundTrip);
        yield return ("spaces, device file: with no spaces, exactly the keys Tack 1.3 writes", DeviceFileWithoutSpaces);
        yield return ("spaces, tack.json: round trip; the top level still lists every task and sheet", TackJsonRoundTrip);
        yield return ("spaces, tack.json: with no spaces, not a single space key", TackJsonWithoutSpaces);
        yield return ("spaces, tack.json: a deleted space is saved without its name or the name's clock, so an undo brings the name back", TackJsonDeletedName);
        yield return ("spaces, tack.json: deleted spaces kept while syncing, forgotten after 90 days without", PurgesDeletedSpaces);
        yield return ("spaces, import: a file brings its spaces and assignments, and Undo takes them back", ImportAndUndo);
        yield return ("spaces: the effective space of \"\", main, a live, a deleted and an unknown space", Effective);
        yield return ("spaces, workspace: add, rename (Main too), delete and undo, one stamp each", WorkspaceSpaceOps);
        yield return ("spaces, workspace: a name is kept to 100 characters, never cutting a character in half", WorkspaceLongNames);
        yield return ("spaces, workspace: moving a task or sheet stamps only its space; order and focus stay", WorkspaceMoves);
        yield return ("spaces, workspace: every change is saved, merged ones too", WorkspaceChanges);
        yield return ("spaces, workspace: Restore puts spaces and assignments back", WorkspaceRestore);
        yield return ("spaces, sync: an assignment survives an older Tack editing the same task", SyncOldVersionEdit);
        yield return ("spaces, sync: a deleted space's tasks show in Main everywhere, and Undo brings them back", SyncDeleteAndUndo);
        yield return ("spaces, sync: a move or a rename alone makes the device write its file", SyncSpaceOnlyChanges);
        yield return ("spaces, sync: a space with a long name, and what's in it, reach every device alike", SyncLongName);
        yield return ("spaces, sync: joining leaves a welcome task or sheet behind only if never touched, a move to a space included", SyncWelcomeMoved);
        yield return ("spaces, sync: each peer file is read once more after the upgrade, then skipped while unchanged", SyncRereadOnce);
        yield return ("spaces, sync: the reader marker in sync.json", SyncMetaReader);
        yield return ("spaces, sync: a file an older Tack wrote under this PC's id is written again in full", SyncOlderWriter);
        yield return ("spaces, sync: the writer marker in sync.json", SyncMetaWriter);
        yield return ("spaces, hostile files: junk in spaces and deleted.spaces is skipped, never the file", HostileJunk);
        yield return ("spaces, hostile files: spaces count toward the record limit", HostileLimit);
        yield return ("spaces, hostile files: half a character in a space name makes the file unreadable, never an error", HostileHalfCharacter);
    }

    // ---------------------------------------------------------------- merge

    private static void MergeLaterWins()
    {
        var plain = Task("t1");
        var inWork = InSpace(plain, "work", S(Now, 1));
        var inDev = InSpace(plain, "dev", S(Now, 2));
        Check.Equal((inWork, true), Merge.Task(plain, inWork), "an assignment reaches a copy without one");
        Check.Equal((inWork, false), Merge.Task(inWork, plain), "and stays against it");
        Check.Equal((inDev, true), Merge.Task(inWork, inDev), "a later move wins");
        Check.Equal((inDev, false), Merge.Task(inDev, inWork), "an earlier one doesn't");
        var back = InSpace(plain, SpaceField.Main, S(Now, 3));
        Check.Equal(back, Merge.Task(inDev, back).Value, "an explicit move back to Main wins like any move");

        var sheet = Sheet("s1");
        var sheetInWork = InSpace(sheet, "work", S(Now, 1));
        var sheetInDev = InSpace(sheet, "dev", S(Now, 2));
        Check.Equal(sheetInWork, Merge.Sheet(sheet, sheetInWork).Value, "sheets: an assignment arrives");
        Check.Equal(sheetInDev, Merge.Sheet(sheetInDev, sheetInWork).Value, "sheets: the later move wins");
        Check.Equal(sheetInDev, Merge.Sheet(sheetInWork, sheetInDev).Value, "sheets: either way round");
    }

    private static void MergeOldCopy()
    {
        var plain = Task("t1");
        var inWork = InSpace(plain, "work", S(Now, 1));
        // An older Tack edits the text later; its copy has no space and no clock for it.
        var oldEdit = plain with { Text = "edited", Clk = plain.Clk with { Text = S(Now, 5, "old00001") } };
        var expected = inWork with { Text = "edited", Clk = inWork.Clk with { Text = oldEdit.Clk.Text } };
        Check.Equal(expected, Merge.Task(inWork, oldEdit).Value, "the text arrives, the space stays");
        Check.Equal(expected, Merge.Task(oldEdit, inWork).Value, "either way round");

        var sheet = InSpace(Sheet("s1"), "work", S(Now, 1));
        var oldDelete = Sheet("s1") with { Deleted = true, Clk = Sheet("s1").Clk with { Deleted = S(Now, 5, "old00001") } };
        var merged = Merge.Sheet(sheet, oldDelete).Value;
        Check.True(merged.Deleted && merged.Space == "work" && merged.Clk.Space == sheet.Clk.Space, "a sheet deleted by an older Tack keeps its space");
    }

    private static void MergeTie()
    {
        var plain = Task("t1");
        var a = InSpace(plain, "aaa", S(Now, 1));
        var b = InSpace(plain, "bbb", S(Now, 1));
        Check.Equal(b, Merge.Task(a, b).Value, "the larger JSON value wins");
        Check.Equal(b, Merge.Task(b, a).Value, "on both sides");
        var sa = InSpace(Sheet("s1"), "aaa", S(Now, 1));
        var sb = InSpace(Sheet("s1"), "bbb", S(Now, 1));
        Check.Equal(sb, Merge.Sheet(sa, sb).Value, "sheets too");
        Check.Equal(sb, Merge.Sheet(sb, sa).Value, "sheets too, either way round");
    }

    private static void MergeConflictCopy()
    {
        var sheet = Sheet("s1") with { Text = "base" };
        // Written in Work here, then rewritten (later) on another device that hadn't seen it.
        var mine = InSpace(sheet with { Text = "mine", Clk = sheet.Clk with { Text = S(Now, 1, "aaaa0001") } }, "work", S(Now, 1, "aaaa0001"));
        var theirs = sheet with { Text = "theirs", Clk = sheet.Clk with { Text = S(Now, 2, "bbbb0001") } };
        var (value, _, conflict) = Merge.Sheet(mine, theirs, Stamp.Zero);
        Check.Equal("theirs", value.Text, "the later text stays in place");
        Check.Equal("work", value.Space, "and the sheet stays in its space");
        Check.True(conflict != null, "the losing text is kept");
        Check.Equal(new SheetData(Ids.Derived($"s1:{mine.Clk.Text}"), "mine", mine.Updated, OrderKey.After(value.Order), false, "work",
            new SheetClocks(mine.Clk.Text, mine.Clk.Text, mine.Clk.Text, mine.Clk.Space)), conflict, "in the loser's space, with its clock");

        // The losing text had no space: neither has its copy.
        var later = InSpace(sheet with { Text = "later", Clk = sheet.Clk with { Text = S(Now, 3, "aaaa0001") } }, "dev", S(Now, 3, "aaaa0001"));
        var (v2, _, c2) = Merge.Sheet(later, theirs, Stamp.Zero);
        Check.Equal(("later", "dev"), (v2.Text, v2.Space), "the winner keeps its own space");
        Check.Equal(("theirs", "", Stamp.Zero), (c2!.Text, c2.Space, c2.Clk.Space), "the copy is in Main, unassigned");
        Check.Equal(c2, Merge.Sheet(theirs, later, Stamp.Zero).Conflict, "every device makes the same copy");
    }

    private static void MergeSpaceRecords()
    {
        var work = new SpaceData("work", "Work", "V", false, SpaceClocks.All(S(Now, 1)));
        var renamed = work with { Name = "Job", Clk = work.Clk with { Name = S(Now, 3) } };
        var moved = work with { Order = "W", Clk = work.Clk with { Order = S(Now, 2) } };
        var both = work with { Name = "Job", Order = "W", Clk = new SpaceClocks(S(Now, 3), S(Now, 2), work.Clk.Deleted) };
        Check.Equal(both, Merge.Space(renamed, moved).Value, "a rename and a move both land");
        Check.Equal(both, Merge.Space(moved, renamed).Value, "either way round");
        Check.Equal((work, true), Merge.Space(null, work), "a new space");
        Check.Equal((work, false), Merge.Space(work, work), "the same space: no change");

        var dead = DeviceFile.DeadSpace("work", S(Now, 4));
        var deleted = work with { Deleted = true, Clk = work.Clk with { Deleted = S(Now, 4) } };
        Check.Equal(deleted, Merge.Space(work, dead).Value, "a deletion from a device file wins, the name stays");
        Check.Equal(deleted, Merge.Space(dead, work).Value, "either way round");
        Check.Equal(work, Merge.Space(deleted, work with { Deleted = false, Clk = work.Clk with { Deleted = S(Now, 5) } }).Value with { Clk = work.Clk },
            "an undo (later) brings it back");

        var zed = work with { Name = "Zed" };
        Check.Equal(zed, Merge.Space(work, zed).Value, "equal clocks: the larger JSON value");
        Check.Equal(zed, Merge.Space(zed, work).Value, "on both sides");
    }

    // ---------------------------------------------------------------- readers

    private static void ReadersNormalise()
    {
        string valid = Q(S(Now, 1));
        var cases = new (string? Space, string? Clk, string Expected)[]
        {
            (null, null, ""),
            (Q("work"), null, ""),
            (null, valid, ""),
            (Q("work"), valid, "work"), // no such space here yet: kept
            (Q("main"), valid, "main"),
            (Q(new string('x', 64)), valid, new string('x', 64)),
            (Q(new string('x', 65)), valid, ""),
            (Q(""), valid, ""),
            (Q("a b"), valid, ""),
            (Q("work/"), valid, ""),
            ("5", valid, ""),
            ("null", valid, ""),
            ("{}", valid, ""),
            ("[\"work\"]", valid, ""),
            (Q("work"), Q(Stamp.Zero), ""),
            (Q("work"), Q("yesterday"), ""),
            (Q("work"), "5", ""),
            (Q("work"), "null", ""),
            (Q("work"), Q(S(Now + 400 * Day, 0)), ""), // more than a year and a day ahead
            (Q(""), Q(S(Now, 1)), ""),
        };
        foreach (var (space, clk, expected) in cases)
        {
            string what = $"space {space ?? "(none)"}, clock {clk ?? "(none)"}";
            string expectedClk = expected.Length > 0 ? S(Now, 1) : Stamp.Zero;
            using var task = JsonDocument.Parse(TaskJson("t1", space, clk));
            var t = TackFile.ReadTaskRecord(task.RootElement, Now, clean: true);
            Check.True(t != null, $"{what}: the task is read");
            Check.Equal((expected, expectedClk), (t!.Space, t.Clk.Space), $"{what}: task");
            using var sheet = JsonDocument.Parse(SheetJson("s1", space, clk));
            var s = TackFile.ReadSheetRecord(sheet.RootElement, Now);
            Check.True(s != null, $"{what}: the sheet is read");
            Check.Equal((expected, expectedClk), (s!.Space, s.Clk.Space), $"{what}: sheet");

            // The same through a device file and a tackFocus section.
            var file = DeviceFile.Parse(Encoding.UTF8.GetBytes(DeviceJson("pppp0001", tasks: $"[{TaskJson("t1", space, clk)}]", sheets: $"[{SheetJson("s1", space, clk)}]")), "pppp0001", Now);
            Check.Equal((expected, expected), (file.Tasks.Single().Space, file.Sheets.Single().Space), $"{what}: device file");
            using var section = JsonDocument.Parse($"{{\"format\":1,\"focus\":{{\"id\":null,\"clk\":\"{Stamp.Zero}\"}},\"tasks\":[{TaskJson("t1", space, clk)}],\"sheets\":[{SheetJson("s1", space, clk)}]}}");
            var data = TackFile.ReadSection(section.RootElement, Now, clean: false)!;
            Check.Equal((expected, expected), (data.Tasks.Single().Space, data.Sheets.Single().Space), $"{what}: tack.json");
        }
    }

    private static void ReadersSpaceRecords()
    {
        SpaceData? Read(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return TackFile.ReadSpaceRecord(doc.RootElement, Now);
        }
        string at = S(Now, 1);
        Check.Equal(new SpaceData("w1", "Work stuff", "V", false, SpaceClocks.All(at)), Read(SpaceJson("w1", "  Work \n\t stuff  ")), "a valid record; the name made one line");
        Check.Equal("", Read(SpaceJson("w1", ""))!.Name, "an empty name is fine");
        Check.Equal("", Read(SpaceJson("w1", null))!.Name, "so is none");
        Check.Equal(OrderKey.Mid, Read(SpaceJson("w1", "Work", order: "5"))!.Order, "an order that isn't a key");
        Check.Equal("a1", Read(SpaceJson("w1", "Work", order: Q("a1")))!.Order, "an order key is kept");
        Check.Equal(OrderKey.Mid, Read(SpaceJson("w1", "Work", order: Q("a0")))!.Order, "and one that isn't, isn't");
        Check.True(Read(SpaceJson("w1", "Work", deleted: "true"))!.Deleted, "deleted");
        Check.True(!Read(SpaceJson("w1", "Work", deleted: "\"yes\""))!.Deleted, "only true is deleted");
        Check.Equal("main", Read(SpaceJson("main", "Home"))!.Id, "Main's own record");
        Check.Equal(new string('x', 100), Read(SpaceJson("w1", new string('x', 150)))!.Name, "a name over 100 characters is cut to 100");
        Check.Equal(new string('x', 99), Read(SpaceJson("w1", new string('x', 99) + "\U0001F600"))!.Name, "never between the halves of a character");
        Check.Equal(new string('y', 100), Read(SpaceJson("w1", " " + new string('y', 100) + " "))!.Name, "cleaned first");

        foreach (string bad in new[]
        {
            "5", "\"w1\"", "null", "[]",
            SpaceJson("a b", "Work"),
            SpaceJson("", "Work"),
            SpaceJson(new string('x', 65), "Work"),
            SpaceJson("w1", "Work", clk: "null"),
            SpaceJson("w1", "Work", clk: "{}"),
            SpaceJson("w1", "Work", clk: $"{{\"name\":{Q(at)},\"order\":{Q(at)}}}"),
            SpaceJson("w1", "Work", clk: $"{{\"name\":{Q(S(Now + 400 * Day, 0))},\"order\":{Q(at)},\"deleted\":{Q(at)}}}"),
            SpaceJson("w1", "Work", clk: $"{{\"name\":\"soon\",\"order\":{Q(at)},\"deleted\":{Q(at)}}}"),
            "{\"name\":\"Work\",\"order\":\"V\",\"deleted\":false,\"clk\":{}}",
        })
        {
            Check.True(Read(bad) == null, $"skipped: {bad}");
        }
    }

    private static void DeviceFileLongName()
    {
        string spaces = $"[{SpaceJson("long", new string('x', 101), order: Q("X"))},{SpaceJson("fits", new string('x', 100))},{SpaceJson("spaced", " a  b ")}]";
        var file = DeviceFile.Parse(Encoding.UTF8.GetBytes(DeviceJson("pppp0001", spaces: spaces)), "pppp0001", Now);
        Check.Equal("fits,long,spaced", string.Join(",", file.Spaces.Select(s => s.Id).Order(StringComparer.Ordinal)), "every space comes in");
        Check.Equal(new SpaceData("long", new string('x', 100), "X", false, SpaceClocks.All(S(Now, 1))), file.Spaces.First(s => s.Id == "long"), "the long name cut to 100, the rest as it was");
        Check.Equal("a b", file.Spaces.First(s => s.Id == "spaced").Name, "names cleaned");
    }

    // ---------------------------------------------------------------- files

    /// <summary>A workspace with spaces in use: assignments, Main renamed, a deleted space and a deleted task that was in one.</summary>
    private static (Workspace Ws, SpaceData Work, SpaceData Dev, TaskItem InWork, TaskItem InMain, NoteSheet Sheet) WithSpaces(Func<long>? wall = null)
    {
        var ws = NewWs("desk0001", wall);
        var work = ws.AddSpace("Work");
        var dev = ws.AddSpace("Dev");
        var inWork = ws.AddTask("In Work");
        ws.MoveTaskToSpace(inWork.Id, work.Id);
        var inMain = ws.AddTask("In Main");
        var sheet = ws.Sheets[0];
        sheet.Text = "Work notes";
        ws.MoveSheetToSpace(sheet.Id, work.Id);
        ws.RenameSpace(SpaceField.Main, "Home");
        var gone = ws.AddTask("Gone");
        ws.MoveTaskToSpace(gone.Id, dev.Id);
        ws.Delete(gone);
        ws.DeleteSpace(dev.Id);
        return (ws, ws.SpaceById(work.Id)!, ws.SpaceById(dev.Id)!, inWork, inMain, sheet);
    }

    private static void DeviceFileRoundTrip()
    {
        var (ws, work, dev, inWork, inMain, sheet) = WithSpaces();
        var data = ws.ToData();
        string written = ws.Clock.Now();
        byte[] bytes = DeviceFile.Write(data, "desk0001", "PC", "Tack", written, new Dictionary<string, string>());
        var file = DeviceFile.Parse(bytes, "desk0001", Now);

        // What a device file carries: live records whole, deleted ones as tombstones.
        var expected = new WorkspaceData(
            data.Tasks.Select(t => t.Deleted ? DeviceFile.DeadTask(t.Id, t.Clk.Deleted) : t).ToList(),
            data.Sheets.Select(s => s.Deleted ? DeviceFile.DeadSheet(s.Id, s.Clk.Deleted) : s).ToList(),
            data.Focus,
            data.Spaces.Select(s => s.Deleted ? DeviceFile.DeadSpace(s.Id, s.Clk.Deleted) : s).ToList());
        Check.Data(SyncTests.Sorted(expected), SyncTests.Sorted(new WorkspaceData(file.Tasks, file.Sheets, file.Focus, file.Spaces)), "records");

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Check.Equal("id,name,order,deleted,clk", Keys(root.GetProperty("spaces")[0]), "space keys, in order");
        Check.Equal(string.Join(",", new[] { SpaceField.Main, work.Id }.Order(StringComparer.Ordinal)),
            string.Join(",", root.GetProperty("spaces").EnumerateArray().Select(s => s.Str("id"))), "live spaces, by id");
        Check.Equal(dev.Clk.Deleted, root.GetProperty("deleted").GetProperty("spaces").Str(dev.Id), "the deleted one");
        Check.Equal("tasks,sheets,spaces", Keys(root.GetProperty("deleted")), "deletions");
        var task = root.GetProperty("tasks").EnumerateArray().First(t => t.Str("id") == inWork.Id);
        Check.Equal("id,text,created,completed,order,deleted,space,clk", Keys(task), "a task in a space");
        Check.Equal("text,completed,order,deleted,space", Keys(task.GetProperty("clk")), "and its clocks");
        Check.Equal("id,text,created,completed,order,deleted,clk", Keys(root.GetProperty("tasks").EnumerateArray().First(t => t.Str("id") == inMain.Id)), "a task in none");
        var sheetJson = root.GetProperty("sheets").EnumerateArray().First(s => s.Str("id") == sheet.Id);
        Check.Equal("id,text,updated,order,deleted,space,clk", Keys(sheetJson), "a sheet in a space");
        Check.Equal("text,order,deleted,space", Keys(sheetJson.GetProperty("clk")), "and its clocks");
        Check.Equal("format,version,device,name,app,written,seen,focus,tasks,sheets,spaces,deleted", Keys(root), "top level");
    }

    private static void DeviceFileWithoutSpaces()
    {
        var ws = NewWs("desk0001");
        var a = ws.AddTask("one");
        ws.AddTask("two");
        ws.Complete(a);
        ws.SetNow(ws.Open[0]);
        ws.Sheets[0].Text = "note";
        ws.AddSheet().Text = "second";
        ws.DeleteSheet(ws.Sheets[1]);
        ws.Delete(ws.AddTask("three"));
        byte[] bytes = DeviceFile.Write(ws.ToData(), "desk0001", "PC", "Tack", ws.Clock.Now(), new Dictionary<string, string> { ["pppp0001"] = S(Now, 0, "pppp0001") });
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Check.Equal("format,version,device,name,app,written,seen,focus,tasks,sheets,deleted", Keys(root), "top level");
        Check.Equal("tasks,sheets", Keys(root.GetProperty("deleted")), "deletions");
        foreach (var t in root.GetProperty("tasks").EnumerateArray())
        {
            Check.Equal("id,text,created,completed,order,deleted,clk", Keys(t), "task keys");
            Check.Equal("text,completed,order,deleted", Keys(t.GetProperty("clk")), "task clocks");
        }
        foreach (var s in root.GetProperty("sheets").EnumerateArray())
        {
            Check.Equal("id,text,updated,order,deleted,clk", Keys(s), "sheet keys");
            Check.Equal("text,order,deleted", Keys(s.GetProperty("clk")), "sheet clocks");
        }
        Check.True(!Encoding.UTF8.GetString(bytes).Contains("\"space", StringComparison.Ordinal), "no space key anywhere");
    }

    private static void TackJsonRoundTrip()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File, "{\"version\":1,\"tasks\":[],\"sheets\":[]}");
        var store = new StateStore(dir.Path);
        var ws = store.Attach(store.Load());
        var work = ws.AddSpace("Work");
        var dev = ws.AddSpace("Dev");
        var inWork = ws.AddTask("In Work");
        ws.MoveTaskToSpace(inWork.Id, work.Id);
        ws.AddTask("In Main");
        var sheet = ws.Sheets[0];
        sheet.Text = "Work notes";
        ws.MoveSheetToSpace(sheet.Id, work.Id);
        ws.DeleteSpace(dev.Id);
        store.Flush();

        using (var saved = JsonDocument.Parse(File.ReadAllBytes(dir.File)))
        {
            var root = saved.RootElement;
            Check.Equal("In Main,In Work", string.Join(",", root.GetProperty("tasks").EnumerateArray().Select(t => t.Str("text")).Order(StringComparer.Ordinal)), "the top level lists every task");
            Check.Equal(1, root.GetProperty("sheets").GetArrayLength(), "and every sheet");
            Check.True(!root.GetProperty("tasks")[0].TryGetProperty("space", out _), "the top level is as it always was");
            var section = root.GetProperty("tackFocus");
            Check.Equal("format,exported,device,focus,tasks,sheets,spaces", Keys(section), "the section gains spaces");
            var spaces = section.GetProperty("spaces").EnumerateArray().ToList();
            Check.Equal($"{work.Id}:Work:False,{dev.Id}::True", string.Join(",", spaces.Select(s => $"{s.Str("id")}:{s.Str("name")}:{s.GetProperty("deleted").GetBoolean()}")),
                "every space, a deleted one without its name");
            Check.Equal(Stamp.Zero, spaces[1].GetProperty("clk").Str("name"), "nor the name's clock");
            var task = section.GetProperty("tasks").EnumerateArray().First(t => t.Str("id") == inWork.Id);
            Check.Equal((work.Id, ws.TaskById(inWork.Id)!.Clk.Space), (task.Str("space"), task.GetProperty("clk").Str("space")), "a task's space and clock");
        }

        var store2 = new StateStore(dir.Path);
        var ws2 = store2.Attach(store2.Load());
        var data = ws.ToData();
        var expected = data with { Spaces = data.Spaces.Select(s => s.Deleted ? s with { Name = "", Clk = s.Clk with { Name = Stamp.Zero } } : s).ToList() };
        Check.Data(expected, ws2.ToData(), "records after opening again");
        Check.Equal(ws.Clock.Node, ws2.Clock.Node, "same device");

        byte[] first = File.ReadAllBytes(dir.File);
        store2.Flush();
        var store3 = new StateStore(dir.Path);
        store3.Attach(store3.Load());
        store3.Flush();
        Check.True(first.AsSpan().SequenceEqual(File.ReadAllBytes(dir.File)), "saving and opening again writes the same bytes");

        // Opening a file observes its spaces' clocks, so whatever is done next wins over them.
        long wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string ahead = Stamp.Encode(wall + 3_600_000, 7, "phone001");
        File.WriteAllText(dir.File, "{\"version\":1,\"tasks\":[],\"sheets\":[],\"tackFocus\":{\"format\":1,\"focus\":{\"id\":null,\"clk\":\"" + Stamp.Zero + "\"},\"tasks\":[],\"sheets\":[],"
            + $"\"spaces\":[{{\"id\":\"w\",\"name\":\"W\",\"order\":\"V\",\"deleted\":false,\"clk\":{{\"name\":\"{ahead}\",\"order\":\"{ahead}\",\"deleted\":\"{ahead}\"}}}}]}}}}");
        var clock = new Hlc("desk0001");
        var loaded = StateFile.ToData(new StateStore(dir.Path).Load(), clock, wall, wall);
        Check.Equal("w", loaded.Spaces.Single().Id, "the space is read");
        Check.True(Stamp.IsLater(clock.Now(), ahead), "and its clocks observed");
    }

    private static void TackJsonDeletedName()
    {
        // The phone names a space "!Urgent" (JSON sorts it before ""); this PC gets it, then its deletion.
        long wall = Now;
        var phone = NewWs("phone001", () => wall);
        var urgent = phone.AddSpace("!Urgent");
        using var dir = new TempDir();
        File.WriteAllText(dir.File, "{\"version\":1,\"tasks\":[],\"sheets\":[]}");
        var store = new StateStore(dir.Path);
        var pc = store.Attach(store.Load());
        pc.MergeFrom(phone.ToData());
        wall += 1000;
        var undo = phone.DeleteSpace(urgent.Id);
        pc.MergeFrom(new WorkspaceData([], [], FocusData.None, [DeviceFile.DeadSpace(urgent.Id, phone.SpaceById(urgent.Id)!.Clk.Deleted)]));
        store.Flush();
        using (var saved = JsonDocument.Parse(File.ReadAllBytes(dir.File)))
        {
            var space = saved.RootElement.GetProperty("tackFocus").GetProperty("spaces")[0];
            Check.Equal(("", Stamp.Zero), (space.Str("name"), space.GetProperty("clk").Str("name")), "saved without its name, or the name's clock");
        }

        // This PC opens its tack.json again; then the phone undoes the deletion.
        var store2 = new StateStore(dir.Path);
        var reopened = store2.Attach(store2.Load());
        wall += 1000;
        undo();
        reopened.MergeFrom(phone.ToData());
        var back = reopened.SpaceById(urgent.Id)!;
        Check.Equal(("!Urgent", false), (back.Name, back.Deleted), "the undo brings the name back");
        phone.MergeFrom(reopened.ToData());
        Check.Equal("!Urgent", phone.SpaceById(urgent.Id)!.Name, "and the phone keeps it");
    }

    private static void TackJsonWithoutSpaces()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File, "{\"version\":1,\"tasks\":[],\"sheets\":[]}");
        var store = new StateStore(dir.Path);
        var ws = store.Attach(store.Load());
        ws.Complete(ws.AddTask("done"));
        ws.Delete(ws.AddTask("deleted"));
        ws.SetNow(ws.AddTask("now"));
        ws.Sheets[0].Text = "note";
        store.Flush();
        string text = File.ReadAllText(dir.File);
        Check.True(!text.Contains("\"space", StringComparison.Ordinal), "no space key anywhere:\n" + text);
        using var doc = JsonDocument.Parse(text);
        Check.Equal("format,exported,device,focus,tasks,sheets", Keys(doc.RootElement.GetProperty("tackFocus")), "the section as Tack 1.3 writes it");
    }

    private static void PurgesDeletedSpaces()
    {
        long wall = Now;
        var ws = NewWs("desk0001", () => wall);
        var old = ws.AddSpace("Old");
        var recent = ws.AddSpace("Recent");
        ws.DeleteSpace(old.Id);
        wall += 80 * Day;
        ws.DeleteSpace(recent.Id);
        wall += 20 * Day;
        ws.KeepDeletions = () => true;
        ws.RefreshDay();
        Check.True(ws.SpaceById(old.Id) != null, "kept while syncing");
        ws.KeepDeletions = () => false;
        ws.RefreshDay();
        Check.True(ws.SpaceById(old.Id) == null, "forgotten after 90 days without sync");
        Check.True(ws.SpaceById(recent.Id) != null, "a recent deletion is kept");

        // Loading does the same.
        var data = new WorkspaceData([], [], FocusData.None, [DeviceFile.DeadSpace("gone", S(Now - 91 * Day, 0)), DeviceFile.DeadSpace("kept", S(Now - 10 * Day, 0))]);
        var loaded = new Workspace(new Settings(), new Hlc("desk0001", () => Now), data, () => DateTimeOffset.FromUnixTimeMilliseconds(Now));
        Check.Equal("kept", string.Join(",", loaded.ToData().Spaces.Select(s => s.Id)), "on loading");
    }

    private static void ImportAndUndo()
    {
        var (source, work, _, inWork, _, sheet) = WithSpaces();
        byte[] file = JsonSerializer.SerializeToUtf8Bytes(StateFile.ToDocument(source), StateJsonContext.Default.StateDocument);

        var ws = NewWs("desk0002");
        var (message, undo) = FileImport.Run(ws, file, Now);
        Check.True(undo != null, $"imported ({message})");
        Check.Equal(work.Id, ws.TaskById(inWork.Id)!.Space, "the task's space");
        Check.Equal(work.Id, ws.SheetById(sheet.Id)!.Space, "the sheet's space");
        Check.Equal("Home,Work", string.Join(",", ws.SpaceList.Select(s => s.Name).Order(StringComparer.Ordinal)), "the live spaces");
        Check.Equal(work.Id, Spaces.Effective(ws.TaskById(inWork.Id)!.Space, ws.ToData().Spaces), "shown in Work");

        var (again, noUndo) = FileImport.Run(ws, file, Now);
        Check.True(noUndo == null, $"importing again changes nothing ({again})");

        undo!();
        Check.Equal($"{SpaceField.Main}:", string.Join(",", ws.SpaceList.Select(s => $"{s.Id}:{s.Name}")), "Undo deletes the spaces it brought, and Main's name goes back to its default");
        Check.True(ws.TaskById(inWork.Id)!.IsDeleted, "and the tasks");
    }

    private static void Effective()
    {
        var spaces = new[]
        {
            new SpaceData("work", "Work", "V", false, SpaceClocks.All(S(Now, 1))),
            DeviceFile.DeadSpace("old", S(Now, 2)),
            new SpaceData("main", "Home", "V", false, SpaceClocks.All(S(Now, 1))),
        };
        Check.Equal("main", Spaces.Effective("", spaces), "never assigned");
        Check.Equal("main", Spaces.Effective("main", spaces), "Main");
        Check.Equal("main", Spaces.Effective("main", []), "Main, even without its record");
        Check.Equal("work", Spaces.Effective("work", spaces), "a live space");
        Check.Equal("main", Spaces.Effective("old", spaces), "a deleted space");
        Check.Equal("main", Spaces.Effective("later", spaces), "a space not known here yet");
    }

    // ---------------------------------------------------------------- workspace operations

    private static void WorkspaceSpaceOps()
    {
        var ids = new Queue<string>(["sheet0000001", "work00000001", "dev000000001", "task00000001"]);
        var ws = NewWs("desk0001", ids: ids);
        SpaceData work = null!, dev = null!;
        Check.Equal(1, Stamps(ws, () => work = ws.AddSpace("  Work \n stuff ")), "add: one stamp");
        Check.Equal(new SpaceData("work00000001", "Work stuff", OrderKey.Mid, false, SpaceClocks.All(work.Clk.Name)), work, "add: id, cleaned name, first order key, one clock for all");
        Check.Equal(1, Stamps(ws, () => dev = ws.AddSpace("Dev")), "add again: one stamp");
        Check.Equal(OrderKey.After(work.Order), dev.Order, "after the last one");

        Check.Equal(1, Stamps(ws, () => ws.RenameSpace(dev.Id, " Development ")), "rename: one stamp");
        var renamed = ws.SpaceById(dev.Id)!;
        Check.Equal(dev with { Name = "Development", Clk = dev.Clk with { Name = renamed.Clk.Name } }, renamed, "rename: only the name and its clock");
        Check.True(Stamp.IsLater(renamed.Clk.Name, dev.Clk.Name), "a newer clock");

        Check.True(ws.SpaceById(SpaceField.Main) == null, "Main has no record yet");
        Check.Equal(1, Stamps(ws, () => ws.RenameSpace(SpaceField.Main, "Home")), "renaming Main: one stamp");
        var main = ws.SpaceById(SpaceField.Main)!;
        Check.Equal(new SpaceData(SpaceField.Main, "Home", OrderKey.Mid, false, new SpaceClocks(main.Clk.Name, Stamp.Zero, Stamp.Zero)), main, "makes its record");
        Check.Equal(0, Stamps(ws, () => ws.RenameSpace("nosuchspace", "x")), "an unknown space: nothing");
        Check.True(ws.SpaceById("nosuchspace") == null, "and no record");
        Check.Equal(0, Stamps(ws, () => ws.RenameSpace(SpaceField.Main, " Home ")), "the same name (once cleaned): nothing");
        var third = ws.AddSpace("Third");
        Check.Equal(OrderKey.After(dev.Order), third.Order, "still after the last one, Main's record included");
        var task = ws.AddTask("in dev");
        ws.MoveTaskToSpace(task.Id, dev.Id);

        Action undo = null!;
        Check.Equal(1, Stamps(ws, () => undo = ws.DeleteSpace(dev.Id)), "delete: one stamp");
        var deleted = ws.SpaceById(dev.Id)!;
        Check.Equal(renamed with { Deleted = true, Clk = renamed.Clk with { Deleted = deleted.Clk.Deleted } }, deleted, "delete: only deleted and its clock");
        Check.Equal(dev.Id, ws.TaskById(task.Id)!.Space, "its tasks keep their space");
        Check.Equal(SpaceField.Main, Spaces.Effective(ws.TaskById(task.Id)!.Space, ws.ToData().Spaces), "and show in Main");
        Check.Equal(0, Stamps(ws, () => ws.DeleteSpace(dev.Id)()), "deleting it again: nothing");
        Check.Equal(0, Stamps(ws, () => ws.RenameSpace(dev.Id, "Renamed while deleted")), "renaming a deleted space: nothing");
        Check.Equal(1, Stamps(ws, undo), "undo: one stamp");
        var back = ws.SpaceById(dev.Id)!;
        Check.True(!back.Deleted && Stamp.IsLater(back.Clk.Deleted, deleted.Clk.Deleted), "undo: back, with a newer clock");
        Check.Equal(dev.Id, Spaces.Effective(ws.TaskById(task.Id)!.Space, ws.ToData().Spaces), "its tasks are back in it");

        Action refused = null!;
        Check.Equal(0, Stamps(ws, () => refused = ws.DeleteSpace(SpaceField.Main)), "deleting Main is refused");
        refused();
        Check.True(!ws.SpaceById(SpaceField.Main)!.Deleted, "Main stays");
        Check.Equal(0, Stamps(ws, () => ws.DeleteSpace("nosuchspace")()), "an unknown space: nothing");
    }

    private static void WorkspaceLongNames()
    {
        var ws = NewWs("desk0001");
        var space = ws.AddSpace("   " + new string('x', 150));
        Check.Equal(new string('x', 100), space.Name, "typed long: cut to 100");
        string y99 = new string('y', 99);
        ws.RenameSpace(space.Id, y99 + "\U0001F600 and more");
        Check.Equal(y99, ws.SpaceById(space.Id)!.Name, "an emoji across the limit goes whole");
        ws.RenameSpace(SpaceField.Main, new string('z', 99) + "   tail");
        Check.Equal(new string('z', 99), ws.SpaceById(SpaceField.Main)!.Name, "a space left at the cut goes too");
        Check.Equal(0, Stamps(ws, () => ws.RenameSpace(space.Id, y99 + "\U0001F600 other")), "what it comes to is the name it has: nothing");

        // A snapshot from before the limit is kept to it too.
        var snapshot = ws.ToData();
        snapshot = snapshot with { Spaces = snapshot.Spaces.Select(s => s.Id == space.Id ? s with { Name = new string('w', 120) } : s).ToList() };
        ws.Restore(snapshot);
        Check.Equal(new string('w', 100), ws.SpaceById(space.Id)!.Name, "restored, cut to 100");
    }

    private static void WorkspaceMoves()
    {
        var ws = NewWs("desk0001");
        var work = ws.AddSpace("Work");
        var a = ws.AddTask("a");
        var b = ws.AddTask("b");
        var c = ws.AddTask("c");
        ws.SetNow(b);
        ws.Complete(c);
        string views = Json.Views(ws);
        var before = ws.ToData();

        foreach (var t in new[] { a, b, c }) Check.Equal(1, Stamps(ws, () => ws.MoveTaskToSpace(t.Id, work.Id)), $"{t.Text}: one stamp");
        Check.Equal(1, Stamps(ws, () => ws.MoveSheetToSpace(ws.Sheets[0].Id, work.Id)), "the sheet: one stamp");
        var after = ws.ToData();
        Check.Equal(views, Json.Views(ws), "the lists are as they were");
        Check.Equal(before.Focus, after.Focus, "the focus is as it was");
        for (int i = 0; i < before.Tasks.Count; i++)
        {
            var was = before.Tasks[i];
            var now = after.Tasks[i];
            Check.Equal(was with { Space = work.Id, Clk = was.Clk with { Space = now.Clk.Space } }, now, $"task {i}: only its space changed");
            Check.True(Stamp.IsLater(now.Clk.Space, was.Clk.Max()), $"task {i}: stamped");
        }
        Check.Equal(before.Sheets[0] with { Space = work.Id, Clk = before.Sheets[0].Clk with { Space = after.Sheets[0].Clk.Space } }, after.Sheets[0], "the sheet: only its space changed");

        ws.MoveTaskToSpace(a.Id, SpaceField.Main);
        Check.Equal((SpaceField.Main, true), (ws.TaskById(a.Id)!.Space, ws.TaskById(a.Id)!.Clk.Space != Stamp.Zero), "back to Main: explicit, with a clock");
        Check.Equal(0, Stamps(ws, () => ws.MoveTaskToSpace(a.Id, SpaceField.Main)), "already there: nothing");
        Check.Equal(0, Stamps(ws, () => ws.MoveSheetToSpace(ws.Sheets[0].Id, work.Id)), "a sheet already there: nothing");
        Check.Equal(0, Stamps(ws, () => ws.MoveTaskToSpace(a.Id, "not an id")), "not a space id: nothing");
        Check.Equal(0, Stamps(ws, () => ws.MoveTaskToSpace(a.Id, "")), "no space: nothing");
        Check.Equal(0, Stamps(ws, () => ws.MoveTaskToSpace("nosuchtask", work.Id)), "no such task: nothing");
        Check.Equal(0, Stamps(ws, () => ws.MoveSheetToSpace("nosuchsheet", work.Id)), "no such sheet: nothing");
        Check.Equal(SpaceField.Main, ws.TaskById(a.Id)!.Space, "unchanged");
    }

    private static void WorkspaceChanges()
    {
        var ws = NewWs("desk0001");
        int changes = 0;
        ws.Changed += (_, _) => changes++;
        void Saves(string what, Action op)
        {
            int was = changes;
            op();
            Check.True(changes > was, $"{what}: saved");
        }
        SpaceData work = null!;
        var task = ws.AddTask("x");
        Saves("add", () => work = ws.AddSpace("Work"));
        Saves("rename", () => ws.RenameSpace(work.Id, "Job"));
        Saves("rename Main", () => ws.RenameSpace(SpaceField.Main, "Home"));
        Saves("move a task", () => ws.MoveTaskToSpace(task.Id, work.Id));
        Saves("move a sheet", () => ws.MoveSheetToSpace(ws.Sheets[0].Id, work.Id));
        Action undo = null!;
        Saves("delete", () => undo = ws.DeleteSpace(work.Id));
        Saves("undo", undo);

        // From another device: a space alone, then only a task's space.
        var other = new SpaceData("phone0space1", "Phone's", "W", false, SpaceClocks.All(S(Now + 1000, 0, "phone001")));
        Saves("a merged space", () => Check.True(ws.MergeFrom(new WorkspaceData([], [], FocusData.None, [other])), "news"));
        Check.True(!ws.MergeFrom(new WorkspaceData([], [], FocusData.None, [other])), "the same again: nothing new");
        var moved = InSpace(ws.TaskById(task.Id)!.ToData(), other.Id, S(Now + 2000, 0, "phone001"));
        Saves("a merged move", () => Check.True(ws.MergeFrom(new WorkspaceData([moved], [], FocusData.None, [])), "news"));
        Check.Equal(other.Id, ws.TaskById(task.Id)!.Space, "the task moved");
        Check.True(Stamp.IsLater(ws.Clock.Now(), moved.Clk.Space), "the clock observed the move");
        Check.Equal(other, ws.SpaceById(other.Id), "the space arrived");
    }

    private static void WorkspaceRestore()
    {
        var ws = NewWs("desk0001");
        var work = ws.AddSpace("Work");
        var task = ws.AddTask("x");
        ws.MoveTaskToSpace(task.Id, work.Id);
        var sheet = ws.Sheets[0];
        ws.MoveSheetToSpace(sheet.Id, work.Id);
        var snapshot = ws.ToData();
        string newest = ws.Clock.Now();

        ws.RenameSpace(work.Id, "Job");
        var home = ws.AddSpace("Home");
        ws.MoveTaskToSpace(task.Id, home.Id);
        ws.MoveSheetToSpace(sheet.Id, SpaceField.Main);
        ws.DeleteSpace(work.Id);
        ws.RenameSpace(SpaceField.Main, "Mine");
        var added = ws.AddTask("added since");
        ws.MoveTaskToSpace(added.Id, home.Id);

        ws.Restore(snapshot);
        var w = ws.SpaceById(work.Id)!;
        Check.Equal(("Work", false), (w.Name, w.Deleted), "the space is as it was");
        Check.True(ws.SpaceById(home.Id)!.Deleted, "a space made since is deleted");
        Check.Equal(("", false), (ws.SpaceById(SpaceField.Main)!.Name, ws.SpaceById(SpaceField.Main)!.Deleted), "Main gets its default name back, never deleted");
        Check.Equal(work.Id, ws.TaskById(task.Id)!.Space, "the task is back in it");
        Check.Equal(work.Id, ws.SheetById(sheet.Id)!.Space, "the sheet too");
        Check.True(ws.TaskById(added.Id)!.IsDeleted, "a task made since is deleted");
        Check.True(Stamp.IsLater(ws.TaskById(task.Id)!.Clk.Space, newest) && Stamp.IsLater(w.Clk.Name, newest), "as fresh edits");

        // A task in a space that's gone (forgotten) comes back with its space.
        var fresh = NewWs("desk0002");
        fresh.Restore(snapshot);
        Check.Equal(work.Id, fresh.TaskById(task.Id)!.Space, "re-made in its space");
        Check.Equal("Work", fresh.SpaceById(work.Id)!.Name, "with the space");
        Check.True(Stamp.IsLater(fresh.TaskById(task.Id)!.Clk.Space, Stamp.Zero), "and a clock for it");
        Check.Equal(work.Id, fresh.SheetById(sheet.Id)!.Space, "the sheet too");
    }

    // ---------------------------------------------------------------- sync

    private static void SyncOldVersionEdit()
    {
        var folder = new MemoryFolder();
        long wall = Now;
        var a = new Device("aaaa0001", folder, () => wall);
        var old = new Device("bbbb0001", new OlderTackFolder(folder), () => wall); // reads and writes no spaces
        var task = a.Ws.AddTask("Write the report");
        a.Sync();
        old.Sync();

        wall += 1000;
        var work = a.Ws.AddSpace("Work");
        a.Ws.MoveTaskToSpace(task.Id, work.Id);
        a.Sync();
        wall += 1000;
        old.Sync();
        old.Ws.TaskById(task.Id)!.Text = "Write the report today"; // after the move, on a device that never saw it
        old.Sync();
        Check.True(!Encoding.UTF8.GetString(folder.Files["bbbb0001.json"].Bytes).Contains("\"space", StringComparison.Ordinal), "the older Tack's file has no spaces");

        for (int i = 0; i < 2; i++)
        {
            a.Sync();
            old.Sync();
        }
        var t = a.Ws.TaskById(task.Id)!;
        Check.Equal(("Write the report today", work.Id), (t.Text, t.Space), "the edit arrived, the assignment survived");

        var newcomer = new Device("cccc0001", folder, () => wall);
        newcomer.Sync();
        var n = newcomer.Ws.TaskById(task.Id)!;
        Check.Equal(("Write the report today", work.Id), (n.Text, n.Space), "a device that joins later gets both");
        Check.Equal("Work", newcomer.Ws.SpaceById(work.Id)!.Name, "and the space");
    }

    private static void SyncDeleteAndUndo()
    {
        var folder = new MemoryFolder();
        long wall = Now;
        var a = new Device("aaaa0001", folder, () => wall);
        var b = new Device("bbbb0001", folder, () => wall);
        var work = a.Ws.AddSpace("Work");
        var task = a.Ws.AddTask("In Work");
        a.Ws.MoveTaskToSpace(task.Id, work.Id);
        a.Sync();
        b.Sync();
        string Shown() => Spaces.Effective(b.Ws.TaskById(task.Id)!.Space, b.Ws.ToData().Spaces);
        Check.Equal(work.Id, Shown(), "in Work on the other device");

        wall += 1000;
        var undo = a.Ws.DeleteSpace(work.Id);
        a.Sync();
        b.Sync();
        Check.Equal(SpaceField.Main, Shown(), "deleted: its task shows in Main");
        Check.Equal(work.Id, b.Ws.TaskById(task.Id)!.Space, "but keeps its space");
        using (var file = JsonDocument.Parse(folder.Files["aaaa0001.json"].Bytes))
            Check.True(file.RootElement.GetProperty("deleted").GetProperty("spaces").TryGetProperty(work.Id, out _), "the deletion travels in the file");

        wall += 1000;
        undo();
        a.Sync();
        b.Sync();
        Check.Equal(work.Id, Shown(), "undo: back in Work");
        Check.Data(SyncTests.Sorted(a.Ws.ToData()), SyncTests.Sorted(b.Ws.ToData()), "both devices have the same records");
    }

    private static void SyncSpaceOnlyChanges()
    {
        var folder = new MemoryFolder();
        long wall = Now;
        var a = new Device("aaaa0001", folder, () => wall);
        var b = new Device("bbbb0001", folder, () => wall);
        var task = a.Ws.AddTask("x");
        Check.True(a.Sync().Wrote, "first sync writes");
        b.Sync();
        a.Sync(); // b's own sheet
        Check.True(!a.Sync().Wrote, "nothing new: nothing written");

        SpaceData work = null!;
        foreach (var (what, op) in new (string, Action)[]
        {
            ("adding a space", () => work = a.Ws.AddSpace("Work")),
            ("renaming it", () => a.Ws.RenameSpace(work.Id, "Job")),
            ("renaming Main", () => a.Ws.RenameSpace(SpaceField.Main, "Home")),
            ("moving a task", () => a.Ws.MoveTaskToSpace(task.Id, work.Id)),
            ("moving a sheet", () => a.Ws.MoveSheetToSpace(a.Ws.Sheets[0].Id, work.Id)),
            ("deleting a space", () => a.Ws.DeleteSpace(work.Id)),
        })
        {
            wall += 1000;
            op();
            Check.True(a.Sync().Wrote, $"{what}: written");
            var result = b.Sync();
            Check.True(result.Merged.Contains("aaaa0001") && result.Wrote, $"{what}: news for the other device, which writes too");
            Check.True(!a.Sync().Wrote, $"{what}: then quiet");
        }
        Check.Data(SyncTests.Sorted(a.Ws.ToData()), SyncTests.Sorted(b.Ws.ToData()), "both devices have the same records");
    }

    private static void SyncLongName()
    {
        var folder = new MemoryFolder();
        long wall = Now;
        var a = new Device("aaaa0001", folder, () => wall);
        var b = new Device("bbbb0001", folder, () => wall);
        var task = a.Ws.AddTask("report");
        var space = a.Ws.AddSpace(new string('x', 101));
        a.Ws.MoveTaskToSpace(task.Id, space.Id);
        a.Sync();
        b.Sync();
        string Shown(Device d) => Spaces.Effective(d.Ws.TaskById(task.Id)!.Space, d.Ws.ToData().Spaces);
        Check.Equal(space.Id, Shown(b), "in the long-named space on the other device too");

        // Deleted, then undone and renamed long: every step reaches the other device.
        wall += 1000;
        var work = a.Ws.AddSpace("Work");
        a.Ws.MoveTaskToSpace(task.Id, work.Id);
        a.Sync();
        b.Sync();
        wall += 1000;
        var undo = a.Ws.DeleteSpace(work.Id);
        a.Sync();
        b.Sync();
        Check.Equal(SpaceField.Main, Shown(b), "deleted: in Main");
        wall += 1000;
        undo();
        a.Ws.RenameSpace(work.Id, new string('y', 120));
        a.Sync();
        b.Sync();
        a.Sync();
        Check.Equal((new string('y', 100), false), (b.Ws.SpaceById(work.Id)!.Name, b.Ws.SpaceById(work.Id)!.Deleted), "undone and renamed, the same name on both");
        Check.Equal(work.Id, Shown(b), "back in it");
        Check.Data(SyncTests.Sorted(a.Ws.ToData()), SyncTests.Sorted(b.Ws.ToData()), "both devices have the same records");
    }

    private static void SyncWelcomeMoved()
    {
        var ws = NewWs("pcNEWNEW");
        var tips = new[] { "Tip one", "Tip two", "Tip three" }.Select(t => ws.AddTask(t)).ToList();
        var blank = ws.Sheets[0];
        var other = ws.AddSheet();
        ws.AddSheet().Text = "Mine";
        var work = ws.AddSpace("Work");
        ws.MoveTaskToSpace(tips[1].Id, work.Id);
        ws.MoveSheetToSpace(blank.Id, work.Id);
        ws.LeaveWelcomeBehind(text => text.StartsWith("Tip", StringComparison.Ordinal));
        Check.Equal("Tip two", string.Join(",", ws.Open.Select(t => t.Text)), "the tip moved to a space stays; the untouched ones go");
        Check.True(ws.SheetById(blank.Id) is { IsDeleted: false } && ws.SheetById(other.Id)!.IsDeleted, "the blank sheet moved to a space stays; the untouched one goes");
    }

    private static void SyncRereadOnce()
    {
        var folder = new MemoryFolder();
        long wall = Now;
        // A peer that already has spaces: one task, in Work.
        string at = S(Now, 0, "pppp0001");
        string tasks = $"[{TaskJson("t1", Q("work"), Q(at), node: "pppp0001")}]";
        string spaces = $"[{SpaceJson("work", "Work", clk: $"{{\"name\":{Q(at)},\"order\":{Q(at)},\"deleted\":{Q(at)}}}")}]";
        folder.Put("pppp0001.json", DeviceJson("pppp0001", tasks: tasks, spaces: spaces));
        var pc = new Device("desk0001", folder, () => wall);

        // As if Tack 1.3.1 had read it: the task without its space, the file noted without a reader.
        var file = DeviceFile.Parse(folder.Files["pppp0001.json"].Bytes, "pppp0001", Now);
        pc.Ws.MergeFrom(new WorkspaceData(file.Tasks.Select(t => t with { Space = "", Clk = t.Clk with { Space = Stamp.Zero } }).ToList(), [], file.Focus, []));
        pc.Meta.Files["pppp0001.json"] = new FileSummary(folder.Files["pppp0001.json"].Tag, "pppp0001", file.Name, file.App, file.Written);
        pc.Meta.Seen["pppp0001"] = file.Written;
        Check.Equal("", pc.Ws.TaskById("t1")!.Space, "the old reader dropped the assignment");

        var first = pc.Sync();
        Check.Equal(1, folder.Reads.GetValueOrDefault("pppp0001.json"), "read once more, though its tag is the same");
        Check.True(first.Merged.Contains("pppp0001"), "and merged");
        Check.Equal("work", pc.Ws.TaskById("t1")!.Space, "the assignment is learned");
        Check.Equal("Work", pc.Ws.SpaceById("work")!.Name, "and the space");
        Check.Equal<int?>(SyncEngine.Reader, pc.Meta.Files["pppp0001.json"].Reader, "noted with this reader");

        pc.Sync();
        pc.Sync();
        Check.Equal(1, folder.Reads.GetValueOrDefault("pppp0001.json"), "then skipped while unchanged");

        // A file set aside by an older reader is tried once more too, and noted with the reader.
        folder.Put("nnnn0001.json", DeviceJson("nnnn0001", version: 2));
        pc.Meta.Files["nnnn0001.json"] = new FileSummary(folder.Files["nnnn0001.json"].Tag, "nnnn0001", "", "", Stamp.Zero, "newer");
        var second = pc.Sync();
        Check.Equal(1, folder.Reads.GetValueOrDefault("nnnn0001.json"), "the set-aside file is read once more");
        Check.True(second.Problems.Contains(("nnnn0001", DeviceFileProblem.Newer)), "still newer");
        Check.Equal(((string?)"newer", (int?)SyncEngine.Reader), (pc.Meta.Files["nnnn0001.json"].Problem, pc.Meta.Files["nnnn0001.json"].Reader), "noted with the reader");
        var third = pc.Sync();
        Check.Equal(1, folder.Reads.GetValueOrDefault("nnnn0001.json"), "then not again");
        Check.True(third.Problems.Contains(("nnnn0001", DeviceFileProblem.Newer)), "but still reported");

        // A changed file is read whatever the reader.
        folder.Put("pppp0001.json", DeviceJson("pppp0001", tasks: tasks, spaces: spaces, written: S(Now, 9, "pppp0001")));
        pc.Sync();
        Check.Equal(2, folder.Reads.GetValueOrDefault("pppp0001.json"), "a new version is read");
    }

    private static void SyncMetaReader()
    {
        var meta = SyncMeta.Empty("folder");
        meta.Files["aaaa0001.json"] = new FileSummary("t1", "aaaa0001", "A", "app", S(Now, 0, "aaaa0001"), Reader: SyncEngine.Reader);
        meta.Files["bbbb0001.json"] = new FileSummary("t2", "bbbb0001", "", "", Stamp.Zero, "unreadable", SyncEngine.Reader);
        meta.Files["cccc0001.json"] = new FileSummary("t3", "cccc0001", "C", "app", S(Now, 0, "cccc0001"));
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(meta, SyncJsonContext.Default.SyncMeta));
        var files = json.RootElement.GetProperty("files");
        Check.Equal(2, files.GetProperty("aaaa0001.json").GetProperty("reader").GetInt32(), "\"reader\": 2");
        Check.Equal(2, files.GetProperty("bbbb0001.json").GetProperty("reader").GetInt32(), "on a problem too");
        Check.True(!files.GetProperty("cccc0001.json").TryGetProperty("reader", out _), "none: left out");

        // sync.json from Tack 1.3.1 has no reader.
        string old = "{\"folder\":\"f\",\"seen\":{},\"files\":{\"aaaa0001.json\":{\"tag\":\"t1\",\"device\":\"aaaa0001\",\"name\":\"A\",\"app\":\"app\",\"written\":\"" + Stamp.Zero + "\"}},\"own\":{\"tag\":null,\"written\":null,\"pending\":null},\"lastSync\":0}";
        var read = JsonSerializer.Deserialize(old, SyncJsonContext.Default.SyncMeta)!;
        Check.Equal<int?>(null, read.Files["aaaa0001.json"].Reader, "an old summary has no reader");
        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(meta, SyncJsonContext.Default.SyncMeta), SyncJsonContext.Default.SyncMeta)!;
        Check.Equal(meta.Files["aaaa0001.json"], back.Files["aaaa0001.json"], "read back");
    }

    private static void SyncOlderWriter()
    {
        var folder = new MemoryFolder();
        long wall = Now;
        var pc = new Device("desk0001", folder, () => wall);
        var task = pc.Ws.AddTask("report");
        var work = pc.Ws.AddSpace("Work");
        pc.Ws.MoveTaskToSpace(task.Id, work.Id);
        pc.Sync();
        Check.Equal<int?>(SyncEngine.Writer, pc.Meta.Own.Writer, "noted with this writer");

        // Tack 1.3.1 on this PC again (downgraded, then upgraded once more): it edits, writes the file
        // its way, without spaces, and keeps sync.json its way, without the writer.
        wall += 1000;
        pc.Ws.TaskById(task.Id)!.Text = "report, from Tack 1.3.1";
        Check.True(pc.SyncThrough(new OlderTackFolder(folder)).Wrote, "the older Tack writes its file");
        pc.Meta.Own = pc.Meta.Own with { Writer = null };
        Check.True(!Encoding.UTF8.GetString(folder.Files["desk0001.json"].Bytes).Contains("\"space", StringComparison.Ordinal), "without spaces");

        // This version: nothing new, but it writes the file again, spaces and all; then it's quiet.
        wall += 1000;
        Check.True(pc.Sync().Wrote, "written again in full");
        Check.Equal<int?>(SyncEngine.Writer, pc.Meta.Own.Writer, "noted with this writer again");
        Check.True(!pc.Sync().Wrote, "then quiet");
        var newcomer = new Device("cccc0001", folder, () => wall);
        newcomer.Sync();
        Check.Equal(("report, from Tack 1.3.1", work.Id), (newcomer.Ws.TaskById(task.Id)!.Text, newcomer.Ws.TaskById(task.Id)!.Space), "a device that reads it now gets the edit and the assignment");
        Check.Equal("Work", newcomer.Ws.SpaceById(work.Id)!.Name, "and the space");

        // A write whose answer never came back, then found in the folder: this version's, so not written twice.
        wall += 1000;
        pc.Ws.AddTask("one more");
        var lost = new LostAnswerFolder(folder);
        Check.Throws<SyncException>(() => pc.SyncThrough(lost), "the answer is lost");
        Check.True(pc.Meta.Own.Pending != null, "the write is pending");
        Check.True(!pc.Sync().Wrote, "found as written: not written again");
        Check.Equal<int?>(SyncEngine.Writer, pc.Meta.Own.Writer, "by this writer");
    }

    private static void SyncMetaWriter()
    {
        var meta = SyncMeta.Empty("folder");
        meta.Own = new OwnFile("t1", S(Now, 0, "desk0001"), null, SyncEngine.Writer);
        using (var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(meta, SyncJsonContext.Default.SyncMeta)))
            Check.Equal(2, json.RootElement.GetProperty("own").GetProperty("writer").GetInt32(), "\"writer\": 2");
        meta.Own = meta.Own with { Writer = null };
        using (var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(meta, SyncJsonContext.Default.SyncMeta)))
            Check.True(!json.RootElement.GetProperty("own").TryGetProperty("writer", out _), "none: left out");

        // sync.json from Tack 1.3.1 has no writer.
        string old = "{\"folder\":\"f\",\"seen\":{},\"files\":{},\"own\":{\"tag\":\"t1\",\"written\":\"" + Stamp.Zero + "\",\"pending\":null},\"lastSync\":0}";
        Check.Equal<int?>(null, JsonSerializer.Deserialize(old, SyncJsonContext.Default.SyncMeta)!.Own.Writer, "an old sync.json has no writer");
    }

    // ---------------------------------------------------------------- hostile files

    private static void HostileJunk()
    {
        string at = Q(S(Now, 0, "mmmm0001"));
        string clk = $"{{\"name\":{at},\"order\":{at},\"deleted\":{at}}}";
        string spaces = "[5,\"work\",null,[],{},true,"
            + $"{SpaceJson("__proto__", "Proto", clk: clk)},{SpaceJson("constructor", "Ctor", clk: clk)},{SpaceJson("a b", "Bad", clk: clk)},"
            + $"{SpaceJson("big", new string('x', 1_000_000), clk: clk)}]";
        string deleted = $"{{\"tasks\":{{}},\"sheets\":{{}},\"spaces\":{{\"gone\":{at},\"bad id\":{at},\"num\":5,\"soon\":\"never\",\"far\":{Q(S(Now + 400 * Day, 0))}}}}}";
        var file = DeviceFile.Parse(Encoding.UTF8.GetBytes(DeviceJson("mmmm0001", spaces: spaces, deleted: deleted)), "mmmm0001", Now);
        Check.Equal("__proto__,big,constructor,gone", string.Join(",", file.Spaces.Select(s => s.Id).Order(StringComparer.Ordinal)), "only well-formed spaces");
        Check.Equal(new string('x', 100), file.Spaces.First(s => s.Id == "big").Name, "a huge name cut to 100");
        Check.True(file.Spaces.First(s => s.Id == "gone").Deleted, "the deletion");
        Check.Equal(1, file.Tasks.Count, "the rest of the file is read");

        foreach (string odd in new[] { "\"spaces\"", "5", "{}", "null" })
        {
            var f = DeviceFile.Parse(Encoding.UTF8.GetBytes(DeviceJson("mmmm0001", spaces: odd, deleted: $"{{\"tasks\":{{}},\"sheets\":{{}},\"spaces\":{odd}}}")), "mmmm0001", Now);
            Check.True(f.Spaces.Count == 0 && f.Tasks.Count == 1, $"spaces = {odd}: none, and the file is read");
        }

        // Duplicates merge, the later name wins.
        string later = Q(S(Now, 5, "mmmm0001"));
        string dupes = $"[{SpaceJson("w", "Early", clk: clk)},{SpaceJson("w", "Late", clk: $"{{\"name\":{later},\"order\":{at},\"deleted\":{at}}}")},{SpaceJson("w", "Early", clk: clk)}]";
        Check.Equal("Late", DeviceFile.Parse(Encoding.UTF8.GetBytes(DeviceJson("mmmm0001", spaces: dupes)), "mmmm0001", Now).Spaces.Single().Name, "duplicates merge");

        // And it syncs like any other file, ids like "__proto__" included.
        var folder = new MemoryFolder();
        folder.Put("mmmm0001.json", DeviceJson("mmmm0001", spaces: spaces, deleted: deleted));
        var pc = new Device("desk0001", folder, () => Now);
        pc.Sync();
        Check.Equal("Ctor,Proto," + new string('x', 100), string.Join(",", pc.Ws.SpaceList.Select(s => s.Name).Order(StringComparer.Ordinal)), "merged");
        var own = DeviceFile.Parse(folder.Files["desk0001.json"].Bytes, "desk0001", Now);
        Check.Equal("__proto__,big,constructor,gone", string.Join(",", own.Spaces.Select(s => s.Id).Order(StringComparer.Ordinal)), "and passed on");
    }

    private static void HostileLimit()
    {
        static string Junk(int n) => "[" + string.Join(",", Enumerable.Repeat("{}", n)) + "]";
        string tasks = Junk(DeviceFile.MaxRecords - 2);
        Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(DeviceJson("mmmm0001", tasks: tasks, spaces: Junk(3))), "spaces count");
        string deleted3 = "{\"tasks\":{},\"sheets\":{},\"spaces\":{" + string.Join(",", Enumerable.Range(0, 3).Select(i => $"\"d{i}\":{Q(S(Now, 0))}")) + "}}";
        Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(DeviceJson("mmmm0001", tasks: tasks, deleted: deleted3)), "deleted spaces count");
        DeviceFile.Parse(Encoding.UTF8.GetBytes(DeviceJson("mmmm0001", tasks: tasks, spaces: Junk(1), deleted: "{\"tasks\":{},\"sheets\":{},\"spaces\":{\"d0\":" + Q(S(Now, 0)) + "}}")), "mmmm0001", Now); // at the limit: fine
    }

    private static void HostileHalfCharacter()
    {
        string at = Q(S(Now, 0, "mmmm0001"));
        string spaces = $"[{{\"id\":\"w\",\"name\":\"half \\ud800 a character\",\"order\":\"V\",\"deleted\":false,\"clk\":{{\"name\":{at},\"order\":{at},\"deleted\":{at}}}}}]";
        Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(DeviceJson("mmmm0001", spaces: spaces)), "a lone surrogate in a space name");
        string task = TaskJson("t1", "\"w\\ud800\"", at);
        Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(DeviceJson("mmmm0001", tasks: $"[{task}]")), "in a task's space");

        // In this PC's own tack.json: the section is set aside, the top level opens.
        using var dir = new TempDir();
        File.WriteAllText(dir.File, "{\"version\":1,\"tasks\":[{\"id\":\"a1\",\"text\":\"kept\",\"created\":\"2026-10-21T09:00:00+03:00\"}],\"sheets\":[],"
            + $"\"tackFocus\":{{\"format\":1,\"focus\":{{\"id\":null,\"clk\":\"{Stamp.Zero}\"}},\"tasks\":[],\"sheets\":[],\"spaces\":{spaces}}}}}");
        var doc = new StateStore(dir.Path).Load();
        var data = StateFile.ToData(doc, new Hlc("desk"), Now, Now);
        Check.Equal("kept", string.Join(",", data.Tasks.Where(t => !t.Deleted).Select(t => t.Text)), "the top level is used");
        Check.Equal(0, data.Spaces.Count, "and the spaces are lost, as documented");
    }

    // ---------------------------------------------------------------- helpers

    private static string S(long ms, int counter = 0, string node = "aaaa1111") => Stamp.Encode(ms, counter, node);

    /// <summary>A JSON string literal.</summary>
    private static string Q(string s) => JsonSerializer.Serialize(s);

    private static TaskData Task(string id) =>
        new(id, "task", Date, null, OrderKey.Mid, false, "", new TaskClocks(S(Now), S(Now), S(Now), Stamp.Zero, Stamp.Zero));

    private static SheetData Sheet(string id) =>
        new(id, "note", Date, OrderKey.Mid, false, "", new SheetClocks(S(Now), S(Now), Stamp.Zero, Stamp.Zero));

    private static TaskData InSpace(TaskData t, string space, string clk) => t with { Space = space, Clk = t.Clk with { Space = clk } };

    private static SheetData InSpace(SheetData s, string space, string clk) => s with { Space = space, Clk = s.Clk with { Space = clk } };

    /// <summary>A task record as JSON; space and its clock as raw JSON (null: left out).</summary>
    private static string TaskJson(string id, string? space, string? clk, string node = "aaaa1111")
    {
        string at = Q(S(Now, 0, node));
        return $"{{\"id\":\"{id}\",\"text\":\"x\",\"created\":\"{Date}\",\"completed\":null,\"order\":\"V\",\"deleted\":false"
            + (space != null ? $",\"space\":{space}" : "")
            + $",\"clk\":{{\"text\":{at},\"completed\":{at},\"order\":{at},\"deleted\":\"{Stamp.Zero}\"" + (clk != null ? $",\"space\":{clk}" : "") + "}}";
    }

    private static string SheetJson(string id, string? space, string? clk)
    {
        string at = Q(S(Now));
        return $"{{\"id\":\"{id}\",\"text\":\"note\",\"updated\":\"{Date}\",\"order\":\"V\",\"deleted\":false"
            + (space != null ? $",\"space\":{space}" : "")
            + $",\"clk\":{{\"text\":{at},\"order\":{at},\"deleted\":\"{Stamp.Zero}\"" + (clk != null ? $",\"space\":{clk}" : "") + "}}";
    }

    /// <summary>A space record as JSON (clocks all one stamp unless given as raw JSON).</summary>
    private static string SpaceJson(string id, string? name, string order = "\"V\"", string deleted = "false", string? clk = null)
    {
        string at = Q(S(Now, 1));
        return $"{{\"id\":{Q(id)}" + (name != null ? $",\"name\":{Q(name)}" : "")
            + $",\"order\":{order},\"deleted\":{deleted},\"clk\":{clk ?? $"{{\"name\":{at},\"order\":{at},\"deleted\":{at}}}"}}}";
    }

    /// <summary>A hand-made device file (one task unless given), with spaces and deletions as raw JSON.</summary>
    private static string DeviceJson(string device, string? tasks = null, string sheets = "[]", string? spaces = null, string? deleted = null, int version = 1, string? written = null)
    {
        tasks ??= $"[{TaskJson("t" + device, null, null, device)}]";
        return $"{{\"format\":\"tack-sync-device\",\"version\":{version},\"device\":\"{device}\",\"name\":\"crafted\",\"app\":\"test\",\"written\":{Q(written ?? S(Now, 1, device))},"
            + $"\"seen\":{{}},\"focus\":{{\"id\":null,\"clk\":\"{Stamp.Zero}\"}},\"tasks\":{tasks},\"sheets\":{sheets}"
            + (spaces != null ? $",\"spaces\":{spaces}" : "")
            + $",\"deleted\":{deleted ?? "{\"tasks\":{},\"sheets\":{}}"}}}";
    }

    private static DeviceFileProblem ProblemOf(string json)
    {
        try
        {
            DeviceFile.Parse(Encoding.UTF8.GetBytes(json), "mmmm0001", Now);
        }
        catch (DeviceFileException ex)
        {
            return ex.Problem;
        }
        throw new TestFailure("expected the file to be refused");
    }

    /// <summary>An object's keys, in order.</summary>
    private static string Keys(JsonElement e) => string.Join(",", e.EnumerateObject().Select(p => p.Name));

    private static Workspace NewWs(string node = "desk0001", Func<long>? wall = null, Queue<string>? ids = null)
    {
        var clock = wall ?? (() => Now);
        return new Workspace(new Settings(), new Hlc(node, clock), WorkspaceData.Empty, () => DateTimeOffset.FromUnixTimeMilliseconds(clock()),
            () => ids is { Count: > 0 } ? ids.Dequeue() : Ids.NewId());
    }

    /// <summary>How many stamps an operation took (the wall clock stands still, so each one adds 1 to the counter).</summary>
    private static int Stamps(Workspace ws, Action op)
    {
        var before = Stamp.Decode(ws.Clock.Now())!.Value;
        op();
        var after = Stamp.Decode(ws.Clock.Now())!.Value;
        if (after.Ms != before.Ms) throw new TestFailure("the wall clock moved during the operation");
        return (int)(after.Counter - before.Counter) - 1;
    }

    /// <summary>A device syncing through a folder, as FolderSync does it.</summary>
    private sealed class Device(string id, ISyncFolder folder, Func<long> wall)
    {
        public Workspace Ws { get; } = new(new Settings(), new Hlc(id, wall), null, () => DateTimeOffset.FromUnixTimeMilliseconds(wall()));

        public SyncMeta Meta { get; } = SyncMeta.Empty("folder");

        public CycleResult Sync() => SyncThrough(folder);

        /// <summary>A cycle through another view of the folder (an older Tack's, say).</summary>
        public CycleResult SyncThrough(ISyncFolder through) => new SyncEngine(Ws, through, new SyncHooks
        {
            Label = () => (Ws.Clock.Node, "test"),
            FlushAsync = () => System.Threading.Tasks.Task.CompletedTask,
            SaveMetaAsync = _ => System.Threading.Tasks.Task.CompletedTask,
        }).CycleAsync(Meta, wall()).GetAwaiter().GetResult();
    }

    /// <summary>A folder whose next write arrives, but whose answer never comes back.</summary>
    private sealed class LostAnswerFolder(ISyncFolder inner) : ISyncFolder
    {
        public Task<IReadOnlyList<FolderEntry>> ListAsync() => inner.ListAsync();

        public Task<byte[]> ReadAsync(FolderEntry entry) => inner.ReadAsync(entry);

        public async Task<FolderEntry> WriteAsync(string name, byte[] content)
        {
            await inner.WriteAsync(name, content);
            throw new SyncException(SyncErrorKind.Offline, "the answer never came");
        }
    }

    /// <summary>A folder in memory that counts reads.</summary>
    private sealed class MemoryFolder : ISyncFolder
    {
        private int _n;
        public Dictionary<string, (string Tag, byte[] Bytes)> Files { get; } = new();
        public Dictionary<string, int> Reads { get; } = new();

        public void Put(string name, string text) => Files[name] = ($"t{++_n}", Encoding.UTF8.GetBytes(text));

        public Task<IReadOnlyList<FolderEntry>> ListAsync() =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<FolderEntry>>(Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new FolderEntry(f.Key, f.Value.Tag)).ToList());

        public Task<byte[]> ReadAsync(FolderEntry entry)
        {
            Reads[entry.Name] = Reads.GetValueOrDefault(entry.Name) + 1;
            return Files.TryGetValue(entry.Name, out var f) ? System.Threading.Tasks.Task.FromResult(f.Bytes) : throw new SyncException(SyncErrorKind.Gone, "gone");
        }

        public Task<FolderEntry> WriteAsync(string name, byte[] content)
        {
            Files[name] = ($"t{++_n}", content);
            return System.Threading.Tasks.Task.FromResult(new FolderEntry(name, Files[name].Tag));
        }
    }

    /// <summary>
    /// The folder as a version without spaces sees it: every space field is dropped from what it
    /// reads (it never knew them) and from what it writes.
    /// </summary>
    private sealed class OlderTackFolder(ISyncFolder inner) : ISyncFolder
    {
        public Task<IReadOnlyList<FolderEntry>> ListAsync() => inner.ListAsync();

        public async Task<byte[]> ReadAsync(FolderEntry entry) => Strip(await inner.ReadAsync(entry));

        public Task<FolderEntry> WriteAsync(string name, byte[] content) => inner.WriteAsync(name, Strip(content));

        private static byte[] Strip(byte[] bytes)
        {
            var root = JsonNode.Parse(bytes)!.AsObject();
            root.Remove("spaces");
            foreach (string list in new[] { "tasks", "sheets" })
            {
                foreach (var r in root[list]!.AsArray())
                {
                    r!.AsObject().Remove("space");
                    r["clk"]!.AsObject().Remove("space");
                }
            }
            root["deleted"]!.AsObject().Remove("spaces");
            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("tack-spaces-").FullName;

        public string File => System.IO.Path.Combine(Path, "tack.json");

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
