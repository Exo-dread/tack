using System.Text;
using System.Text.Json;
using Tack.Model;
using Tack.Services;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Spaces on screen: the model operations the screens add (a task or sheet added into a space,
/// spaces reordered) and what the panel shows of a space (<see cref="SpaceView"/>): filtering,
/// the space on show and its fallback, counts, moves within a space, Done, labels and notes.
/// The same cases as Tack Focus's own tests; tests/vectors check the two apps stamp alike.
/// </summary>
internal static class SpaceViewTests
{
    private static readonly long Now = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("spaces on screen, add: a task added into a live space takes it with the task's own stamp; anywhere else, Main", AddTaskInSpace);
        yield return ("spaces on screen, add: a sheet likewise, with its first text", AddSheetInSpace);
        yield return ("spaces on screen, reorder: below a Main with no record, Main stays first and only the space is stamped", MoveBelowMain);
        yield return ("spaces on screen, reorder: above a Main with no record, Main takes the top key first", MoveAboveMain);
        yield return ("spaces on screen, reorder: Main itself gets a nameless record, still shown as Main", MoveMain);
        yield return ("spaces on screen, reorder: past either end, a deleted or unknown space, the same place", MoveEdges);
        yield return ("spaces on screen, reorder: between tied keys the tied ones below make room", MoveBetweenTies);
        yield return ("spaces on screen, reorder: tasks, sheets and the focus stay as they are", MoveTouchesNothingElse);
        yield return ("spaces on screen, reorder: renaming Main never moves it, here or when the name reaches another PC", RenameNeverMovesMain);
        yield return ("spaces on screen, reorder: a renamed Main keeps its name when it, or a space above it, gives it a place", PlaceRenamedMain);
        yield return ("spaces on screen: with only Main the panel shows the workspace's own lists, as before", NoSpacesUnchanged);
        yield return ("spaces on screen: the space on show is kept per device; a space that's gone shows Main until it's back", ShownFallback);
        yield return ("spaces on screen: listed Main first while it has no record, then by order; names as shown", TabsAndNames);
        yield return ("spaces on screen: a list draws a thousand spaces at a time, and always the one on show; names are found at once", ListedInPages);
        yield return ("spaces on screen: a record with All's id is never a space (only a hand-made file has one); what's in it is in Main", ReservedAllId);
        yield return ("spaces on screen: names are one line, control characters made spaces", NamesWithoutControls);
        yield return ("spaces on screen: a toast cuts a space's name at 24 characters, as Tack Focus does", ToastNames);
        yield return ("spaces on screen: a space shows its own tasks, finished tasks and sheets; Main what's in no live space", Filtering);
        yield return ("spaces on screen: lists follow moves, merges and new spaces as they happen", LiveUpdates);
        yield return ("spaces on screen: counts, progress and the focus task are the space's; All counts everything", Counts);
        yield return ("spaces on screen: the task in focus shows in its own space and All, a line elsewhere", NowElsewhere);
        yield return ("spaces on screen: a move within a space goes past the tasks you see, never re-keying other spaces", MovesWithinSpace);
        yield return ("spaces on screen: where keys tie, a move within a space still never re-keys another space's task", MovesWithinSpaceTied);
        yield return ("spaces on screen: Clear clears only the finished tasks on show, and Undo brings them back", ClearDoneOnShow);
        yield return ("spaces on screen: All labels each task with its space, only while there are spaces", Labels);
        yield return ("spaces on screen: new tasks and sheets go in the space on show (All: Main)", NewItems);
        yield return ("spaces on screen: a space with no sheets shows blank paper; its first sheet comes with the first keystroke", BlankPaper);
        yield return ("spaces on screen: the space on show is saved in tack.json's settings only once chosen", SavedInSettings);
    }

    // ---------------------------------------------------------------- model ops

    private static void AddTaskInSpace()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001", "gone00000001", "t1", "t2", "t3", "t4", "t5", "t6", "t7"]));
        var work = ws.AddSpace("Work");
        var gone = ws.AddSpace("Gone");
        ws.DeleteSpace(gone.Id);

        TaskItem task = null!;
        Check.Equal(1, Stamps(ws, () => task = ws.AddTask("Draft the report", space: work.Id)), "one stamp, as without a space");
        var t = task.ToData();
        Check.Equal(work.Id, t.Space, "in the space");
        Check.Equal(t.Clk.Text, t.Clk.Space, "stamped with the task's own clock");
        Check.Equal(TaskClocks.All(t.Clk.Text) with { Space = t.Clk.Text }, t.Clk, "every clock the same stamp");

        foreach (string? where in new[] { null, "", SpaceField.Main, gone.Id, "nosuchspace1", "bad id!" })
        {
            var d = ws.AddTask($"in {where}", space: where).ToData();
            Check.Equal("", d.Space, $"{JsonSerializer.Serialize(where)}: in Main");
            Check.Equal(Stamp.Zero, d.Clk.Space, $"{JsonSerializer.Serialize(where)}: with no clock for it");
        }

        var now = ws.AddTask("Fix the build", asNow: true, space: work.Id);
        Check.True(ws.Now == now, "straight into focus");
        Check.Equal(work.Id, now.Space, "in the space too");
    }

    private static void AddSheetInSpace()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001", "s1", "s2", "s3"]));
        var work = ws.AddSpace("Work");
        NoteSheet sheet = null!;
        Check.Equal(1, Stamps(ws, () => sheet = ws.AddSheet(work.Id, "Site visit\r\n- rebar")), "one stamp");
        var s = sheet.ToData();
        Check.Equal(work.Id, s.Space, "in the space");
        Check.Equal(s.Clk.Text, s.Clk.Space, "stamped with the sheet's own clock");
        Check.Equal("Site visit\n- rebar", s.Text, "with its first text, kept as notes are");
        Check.True(ws.ActiveSheet == sheet, "the one being written on");
        Check.Equal(OrderKey.After(ws.Sheets[0].Order), s.Order, "after the last sheet, whatever its space");

        var main = ws.AddSheet(SpaceField.Main).ToData();
        Check.Equal(("", Stamp.Zero), (main.Space, main.Clk.Space), "Main: no space, no clock");
        Check.Equal("", main.Text, "and blank");
    }

    private static void MoveBelowMain()
    {
        var (ws, work, dev, home) = ThreeSpaces();
        Check.Sequence(new[] { SpaceField.Main, work.Id, dev.Id, home.Id }, Order(ws), "Main first");
        Check.Equal(1, Stamps(ws, () => ws.MoveSpace(home.Id, 1)), "one stamp");
        Check.Sequence(new[] { SpaceField.Main, home.Id, work.Id, dev.Id }, Order(ws), "below Main, above Work");
        Check.True(ws.SpaceById(SpaceField.Main) == null, "Main still has no record");
        var moved = ws.SpaceById(home.Id)!;
        Check.Equal(OrderKey.Between(null, work.Order), moved.Order, "a key before Work's");
        Check.Equal(home with { Order = moved.Order, Clk = home.Clk with { Order = moved.Clk.Order } }, moved, "only its order and that clock");
        Check.Equal(work, ws.SpaceById(work.Id), "the others untouched");
    }

    private static void MoveAboveMain()
    {
        var (ws, work, dev, home) = ThreeSpaces();
        Check.Equal(2, Stamps(ws, () => ws.MoveSpace(home.Id, 0)), "two stamps: Main's place, then the space's");
        Check.Sequence(new[] { home.Id, SpaceField.Main, work.Id, dev.Id }, Order(ws), "above Main");
        var main = ws.SpaceById(SpaceField.Main)!;
        Check.Equal(OrderKey.Between(null, work.Order), main.Order, "Main at the top key");
        Check.Equal(("", false, Stamp.Zero, Stamp.Zero), (main.Name, main.Deleted, main.Clk.Name, main.Clk.Deleted), "nameless, nothing else stamped");
        Check.True(Stamp.IsLater(ws.SpaceById(home.Id)!.Clk.Order, main.Clk.Order), "Main first, then the space");
        Check.Equal(OrderKey.Before(main.Order), ws.SpaceById(home.Id)!.Order, "the space just above it");
        Check.Equal("Main", new SpaceView(ws).NameOf(SpaceField.Main), "still called Main");

        // Once Main has its record, it moves like any space.
        Check.Equal(1, Stamps(ws, () => ws.MoveSpace(home.Id, 3)), "one stamp");
        Check.Sequence(new[] { SpaceField.Main, work.Id, dev.Id, home.Id }, Order(ws), "to the end");
    }

    private static void MoveMain()
    {
        var (ws, work, dev, home) = ThreeSpaces();
        Check.Equal(0, Stamps(ws, () => ws.MoveSpace(SpaceField.Main, 0)), "already first: nothing");
        Check.True(ws.SpaceById(SpaceField.Main) == null, "and no record");
        Check.Equal(1, Stamps(ws, () => ws.MoveSpace(SpaceField.Main, 2)), "one stamp");
        Check.Sequence(new[] { work.Id, dev.Id, SpaceField.Main, home.Id }, Order(ws), "between Dev and Home");
        var main = ws.SpaceById(SpaceField.Main)!;
        Check.Equal(new SpaceData(SpaceField.Main, "", OrderKey.Between(dev.Order, home.Order), false, new SpaceClocks(Stamp.Zero, main.Clk.Order, Stamp.Zero)), main,
            "a nameless record: only the order stamped");
        Check.Equal("Main", new SpaceView(ws).NameOf(SpaceField.Main), "shown as Main");
        ws.MoveSpace(SpaceField.Main, 9);
        Check.Sequence(new[] { work.Id, dev.Id, home.Id, SpaceField.Main }, Order(ws), "past the end: last");
    }

    private static void MoveEdges()
    {
        var (ws, work, dev, home) = ThreeSpaces();
        ws.MoveSpace(work.Id, 99);
        Check.Sequence(new[] { SpaceField.Main, dev.Id, home.Id, work.Id }, Order(ws), "past the end: last");
        ws.MoveSpace(work.Id, -4);
        Check.Sequence(new[] { work.Id, SpaceField.Main, dev.Id, home.Id }, Order(ws), "before the start: first");
        Check.Equal(0, Stamps(ws, () => ws.MoveSpace(work.Id, 0)), "where it is: nothing");
        Check.Equal(0, Stamps(ws, () => ws.MoveSpace("nosuchspace1", 0)), "an unknown space: nothing");
        ws.DeleteSpace(dev.Id);
        Check.Equal(0, Stamps(ws, () => ws.MoveSpace(dev.Id, 0)), "a deleted space: nothing");
    }

    private static void MoveBetweenTies()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001"]));
        var work = ws.AddSpace("Work");
        string w = Stamp.Encode(Now, 0, "phone");
        SpaceData Tie(string id) => new(id, $"Tied {id[0]}", OrderKey.Mid, false, SpaceClocks.All(w));
        ws.MergeFrom(new WorkspaceData([], [], FocusData.None, [Tie("aaaaaaaaaaaa"), Tie("bbbbbbbbbbbb"), Tie("cccccccccccc")]));
        // Work's key is Mid too (the first space added), so four ties, by id.
        Check.Sequence(new[] { SpaceField.Main, "aaaaaaaaaaaa", "bbbbbbbbbbbb", "cccccccccccc", work.Id }, Order(ws), "ties by id");
        ws.MoveSpace("cccccccccccc", 2);
        Check.Sequence(new[] { SpaceField.Main, "aaaaaaaaaaaa", "cccccccccccc", "bbbbbbbbbbbb", work.Id }, Order(ws), "it lands where it was put");
        Check.True(ws.SpaceById("bbbbbbbbbbbb")!.Order != OrderKey.Mid, "the tied one below made room");
        Check.Equal(Tie("aaaaaaaaaaaa"), ws.SpaceById("aaaaaaaaaaaa"), "the one above untouched");
    }

    private static void RenameNeverMovesMain()
    {
        // The phone renames Main while it has no spaces; this PC meanwhile made spaces, the first keyed
        // Mid like Main's record. Ids sort below "main", so a key would put Main second.
        var phone = NewWs("phone001");
        phone.RenameSpace(SpaceField.Main, "Inbox");
        Check.Equal((OrderKey.Mid, Stamp.Zero), (phone.SpaceById(SpaceField.Main)!.Order, phone.SpaceById(SpaceField.Main)!.Clk.Order), "a name, no place");
        var pc = NewWs(ids: new(["sheet0000001", "aaaa00000001", "bbbb00000001"]));
        var work = pc.AddSpace("Work");
        var dev = pc.AddSpace("Dev");
        Check.Equal(OrderKey.Mid, work.Order, "the PC's first space keyed Mid");
        var fromPhone = phone.ToData();
        phone.MergeFrom(pc.ToData());
        pc.MergeFrom(fromPhone);
        Check.Sequence(new[] { SpaceField.Main, work.Id, dev.Id }, Order(pc), "the PC: Main still first");
        Check.Sequence(new[] { SpaceField.Main, work.Id, dev.Id }, Order(phone), "the phone: likewise");
        Check.Equal("Inbox", new SpaceView(pc).NameOf(SpaceField.Main), "with its name");
        Check.Equal(SpaceField.Main, new SpaceView(pc).SpaceForKey(1), "Alt+1 is still Main");

        // Renamed here, after the spaces: still first.
        pc.RenameSpace(SpaceField.Main, "Today");
        Check.Sequence(new[] { SpaceField.Main, work.Id, dev.Id }, Order(pc), "renamed again: where it was");
    }

    private static void PlaceRenamedMain()
    {
        var (ws, work, dev, home) = ThreeSpaces();
        ws.RenameSpace(SpaceField.Main, "Inbox");
        var named = ws.SpaceById(SpaceField.Main)!;
        Check.Sequence(new[] { SpaceField.Main, work.Id, dev.Id, home.Id }, Order(ws), "renamed: Main first");
        Check.Equal(2, Stamps(ws, () => ws.MoveSpace(home.Id, 0)), "two stamps: Main's place, then the space's");
        Check.Sequence(new[] { home.Id, SpaceField.Main, work.Id, dev.Id }, Order(ws), "above Main");
        var main = ws.SpaceById(SpaceField.Main)!;
        Check.Equal(named with { Order = OrderKey.Between(null, work.Order), Clk = named.Clk with { Order = main.Clk.Order } }, main, "its name kept, its order stamped");

        var (other, work2, dev2, home2) = ThreeSpaces();
        other.RenameSpace(SpaceField.Main, "Inbox");
        Check.Equal(1, Stamps(other, () => other.MoveSpace(SpaceField.Main, 2)), "Main itself: one stamp");
        Check.Sequence(new[] { work2.Id, dev2.Id, SpaceField.Main, home2.Id }, Order(other), "between Dev and Home");
        Check.Equal("Inbox", other.SpaceById(SpaceField.Main)!.Name, "its name kept");
    }

    private static void MoveTouchesNothingElse()
    {
        var (ws, work, dev, _) = ThreeSpaces();
        var t = ws.AddTask("in work", asNow: true, space: work.Id);
        ws.AddTask("in dev", space: dev.Id);
        ws.AddSheet(dev.Id, "dev notes");
        var before = ws.ToData();
        ws.MoveSpace(dev.Id, 0);
        ws.MoveSpace(SpaceField.Main, 3);
        var after = ws.ToData();
        Check.Sequence(before.Tasks, after.Tasks, "tasks");
        Check.Sequence(before.Sheets, after.Sheets, "sheets");
        Check.Equal(before.Focus, after.Focus, "focus");
        Check.True(ws.Now == t, "still in focus");
    }

    // ---------------------------------------------------------------- the view

    private static void NoSpacesUnchanged()
    {
        var ws = NewWs(wall: () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var a = ws.AddTask("a");
        ws.AddTask("b");
        ws.AddTask("now", asNow: true);
        ws.Complete(a);
        var view = new SpaceView(ws);
        Check.True(ReferenceEquals(ws.Open, view.Open) && ReferenceEquals(ws.Done, view.Done) && ReferenceEquals(ws.Sheets, view.Sheets),
            "the workspace's own collections");
        Check.True(view.ActiveSheet == ws.ActiveSheet, "its sheet");
        Check.Equal(false, view.HasSpaces, "no strip, no keys, no labels");
        Check.Equal(("Add a task", "Add a task"), (view.AddPlaceholder, view.AddName), "the placeholder and its name as before");
        Check.True(view.Now == ws.Now && !view.HasNowElsewhere, "the focus card as before");
        Check.Equal((ws.OpenCount, ws.DoneToday, ws.Progress, ws.HasDone), (view.OpenCount, view.DoneToday, view.Progress, view.HasDone), "the same counts");
        Check.True(view.FocusTask == ws.FocusTask, "the same focus task");
        Check.True(ws.Open.All(t => t.SpaceLabel == null), "no labels");

        // A choice left from when there were spaces changes nothing either.
        ws.Settings.SpaceOnShow = "dead00000001";
        Check.True(ReferenceEquals(ws.Open, view.Open), "a space that's gone: the same");
        ws.Settings.SpaceOnShow = SpaceView.All;
        Check.True(ReferenceEquals(ws.Open, view.Open) && ws.Open.All(t => t.SpaceLabel == null), "All: the same, and no labels");
    }

    private static void ShownFallback()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001"]));
        var work = ws.AddSpace("Work");
        var view = new SpaceView(ws);
        Check.Equal(SpaceField.Main, view.Shown, "Main to begin with");
        view.Show(work.Id);
        Check.Equal((work.Id, work.Id), (view.Shown, ws.Settings.SpaceOnShow), "shown and kept");
        var undo = ws.DeleteSpace(work.Id);
        Check.Equal(SpaceField.Main, view.Shown, "deleted: Main");
        Check.Equal(work.Id, ws.Settings.SpaceOnShow, "the choice is kept");
        undo();
        Check.Equal(work.Id, view.Shown, "back with Undo");
        ws.Settings.SpaceOnShow = "nosuchspace1";
        Check.Equal(SpaceField.Main, view.Shown, "never heard of: Main");
        view.Show(SpaceView.All);
        Check.Equal((SpaceView.All, SpaceView.All, "All", true), (view.Shown, ws.Settings.SpaceOnShow, view.ShownName, view.IsAll), "All");
        view.Show(SpaceField.Main);
        Check.Equal((SpaceField.Main, (string?)null), (view.Shown, ws.Settings.SpaceOnShow), "Main is kept as nothing at all");
        ws.Settings.SpaceOnShow = "main";
        Check.Equal(null, ws.Settings.SpaceOnShow, "even when it's named");
    }

    private static void TabsAndNames()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001", "dev000000001"]));
        var view = new SpaceView(ws);
        Check.Sequence(new[] { new SpaceTab(SpaceField.Main, "Main") }, view.Tabs, "only Main");
        var work = ws.AddSpace("Work");
        var dev = ws.AddSpace("Dev");
        Check.Sequence(new[] { new SpaceTab(SpaceField.Main, "Main"), new SpaceTab(work.Id, "Work"), new SpaceTab(dev.Id, "Dev") }, view.Tabs, "Main first, then by order");
        Check.Equal(true, view.HasSpaces, "a second space: the strip shows");
        ws.RenameSpace(SpaceField.Main, "Inbox");
        ws.RenameSpace(dev.Id, "  ");
        Check.Sequence(new[] { new SpaceTab(SpaceField.Main, "Inbox"), new SpaceTab(work.Id, "Work"), new SpaceTab(dev.Id, "Untitled") }, view.Tabs, "names as shown");
        ws.RenameSpace(SpaceField.Main, "");
        Check.Equal("Main", view.NameOf(SpaceField.Main), "Main without a name of its own");
        Check.Equal(SpaceView.All, view.SpaceForKey(0), "Alt+0: All");
        Check.Equal(SpaceField.Main, view.SpaceForKey(1), "Alt+1: the first");
        Check.Equal(dev.Id, view.SpaceForKey(3), "Alt+3: the third");
        Check.Equal(null, view.SpaceForKey(4), "Alt+4: nothing there");
    }

    private static void ReservedAllId()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001"]));
        var work = ws.AddSpace("Work");
        string c = Stamp.Encode(Now, 5, "phone");
        TaskData InAll(string id) => new(id, $"in all {id}", "2026-10-21T09:00:00.000+00:00", null, "A", false, SpaceView.All, TaskClocks.All(c) with { Space = c });
        ws.MergeFrom(new WorkspaceData([InAll("inall0000001"), InAll("inall0000002"), InAll("inall0000003")], [], FocusData.None,
            [new SpaceData(SpaceView.All, "Everything", "W", false, SpaceClocks.All(c))]));
        var mine = ws.AddTask("mine");
        var view = new SpaceView(ws);
        Check.True(ws.SpaceById(SpaceView.All) != null, "the record is kept (and synced) as it came");
        Check.Sequence(new[] { new SpaceTab(SpaceField.Main, "Main"), new SpaceTab(work.Id, "Work") }, view.Tabs, "not listed");
        Check.Sequence(new[] { SpaceField.Main, work.Id }, Order(ws), "nor among the spaces as listed");
        Check.Equal(SpaceField.Main, view.SpaceOf(ws.TaskById("inall0000001")!), "what's in it is in Main");
        Check.Equal(SpaceField.Main, Spaces.Effective(SpaceView.All, ws.ToData().Spaces), "everywhere");
        view.Show(SpaceView.All);
        Check.True(view.IsAll && ReferenceEquals(view.Open, ws.Open), "All is still All");
        Check.Equal(4, view.OpenCount, "each task counted once");
        view.Show(SpaceField.Main);
        Check.Sequence(new[] { mine.Id, "inall0000001", "inall0000002", "inall0000003" }, view.Open.Select(t => t.Id).ToList(), "Main shows them");
        Check.Equal("", ws.AddTask("new", space: SpaceView.All).Space, "nothing new goes in it");
        Check.Equal("", ws.AddSheet(SpaceView.All, "note").Space, "nor a sheet");
    }

    private static void NamesWithoutControls()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001"]));
        var work = ws.AddSpace("a\u0000b\u001bc\u007fd\u0085e\u009f");
        Check.Equal("a b c d e", work.Name, "each a space, then tidied");
        ws.RenameSpace(work.Id, "\u0085Work\u0085\u0085");
        Check.Equal("Work", ws.SpaceById(work.Id)!.Name, "none at either end");
        Check.Equal("Work", SpaceField.CleanName(ws.SpaceById(work.Id)!.Name), "clean already: no change");
    }

    private static void ToastNames()
    {
        Check.Equal("Work", SpaceView.ToastName("Work"), "a short name as it is");
        Check.Equal("Structural coordination!", SpaceView.ToastName("Structural coordination!"), "24 characters: as it is");
        // Tack Focus: shorten(name, 24) = name.slice(0, 23).trimEnd() + '…'
        Check.Equal("Coordination with the M…", SpaceView.ToastName("Coordination with the MEP and structural teams north tower L"), "cut, with an ellipsis");
        Check.Equal("Coordination with the…", SpaceView.ToastName("Coordination with the   MEP team"), "no space before the ellipsis");
        Check.Equal("Clash detection, block…", SpaceView.ToastName("Clash detection, block\U0001F3D7 north"), "never half a character");
        Check.True(SpaceView.ToastName(new string('W', 100)).Length == 24, "24 at most");
    }

    private static void Filtering()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001", "dev000000001", "gone00000001", "m1", "w1", "d1", "x1", "g1", "u1", "wd", "s0", "ws", "ds"]));
        var work = ws.AddSpace("Work");
        var dev = ws.AddSpace("Dev");
        var gone = ws.AddSpace("Gone");
        var m1 = ws.AddTask("main");
        var w1 = ws.AddTask("work", space: work.Id);
        var d1 = ws.AddTask("dev", space: dev.Id);
        var x1 = ws.AddTask("moved back to main", space: work.Id);
        ws.MoveTaskToSpace(x1.Id, SpaceField.Main);
        var g1 = ws.AddTask("in a space deleted since", space: gone.Id);
        ws.DeleteSpace(gone.Id);
        var u1 = ws.AddTask("unknown");
        ws.MoveTaskToSpace(u1.Id, "nosuchspace1"); // put there on a device whose space hasn't arrived here yet
        var wd = ws.AddTask("work, done", space: work.Id);
        ws.Complete(wd);
        var ws0 = ws.Sheets[0];
        var wsheet = ws.AddSheet(work.Id, "work notes");
        var view = new SpaceView(ws);

        view.Show(work.Id);
        Check.Sequence(new[] { w1 }, view.Open.ToList(), "Work: its open tasks");
        Check.Sequence(new[] { wd }, view.Done.ToList(), "Work: its finished tasks");
        Check.Sequence(new[] { wsheet }, view.Sheets.ToList(), "Work: its sheets");
        view.Show(dev.Id);
        Check.Sequence(new[] { d1 }, view.Open.ToList(), "Dev");
        Check.Equal(0, view.Done.Count + view.Sheets.Count, "Dev: nothing else");
        view.Show(SpaceField.Main);
        Check.Sequence(new[] { u1, g1, x1, m1 }, view.Open.ToList(), "Main: no space, moved back, deleted space, unknown space; in the one order");
        Check.Sequence(new[] { ws0 }, view.Sheets.ToList(), "Main's sheet");
        view.Show(SpaceView.All);
        Check.True(ReferenceEquals(ws.Open, view.Open), "All: everything, as the workspace has it");
    }

    private static void LiveUpdates()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001", "a", "b", "c"]));
        var work = ws.AddSpace("Work");
        var a = ws.AddTask("a");
        var b = ws.AddTask("b", space: work.Id);
        var c = ws.AddTask("c");
        var view = new SpaceView(ws);
        view.Show(work.Id);
        int summaries = 0;
        view.SummaryChanged += (_, _) => summaries++;

        ws.MoveTaskToSpace(a.Id, work.Id);
        Check.Sequence(new[] { b, a }, view.Open.ToList(), "moved in: in its place in the one order");
        Check.True(summaries > 0, "the counts are told");
        ws.MoveTaskToSpace(b.Id, SpaceField.Main);
        Check.Sequence(new[] { a }, view.Open.ToList(), "moved out");

        // Another device puts c in a space this one hasn't seen yet, then the space arrives.
        string later = Stamp.Encode(Now + 60_000, 0, "phone");
        var cData = ws.TaskById(c.Id)!.ToData();
        var home = new SpaceData("home00000001", "Home", OrderKey.After(work.Order), false, SpaceClocks.All(later));
        ws.MergeFrom(new WorkspaceData([cData with { Space = home.Id, Clk = cData.Clk with { Space = later } }], [], FocusData.None, []));
        view.Show(home.Id);
        Check.Equal(SpaceField.Main, view.Shown, "not here yet: Main");
        Check.True(view.Open.Contains(c), "and c shows in Main meanwhile");
        ws.MergeFrom(new WorkspaceData([], [], FocusData.None, [home]));
        Check.Equal(home.Id, view.Shown, "the space arrives: it's shown, as chosen");
        Check.Sequence(new[] { c }, view.Open.ToList(), "with c in it");
        Check.True(view.Tabs.Any(t => t.Id == home.Id && t.Name == "Home"), "and listed");
    }

    private static void Counts()
    {
        var ws = NewWs(wall: () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var work = ws.AddSpace("Work");
        var dev = ws.AddSpace("Dev");
        ws.AddTask("w1", space: work.Id);
        var w2 = ws.AddTask("w2", space: work.Id);
        var done = ws.AddTask("w3", space: work.Id);
        ws.Complete(done);
        var d1 = ws.AddTask("d1", space: dev.Id);
        ws.AddTask("m1");
        var now = ws.AddTask("focus", asNow: true, space: work.Id);
        var view = new SpaceView(ws);

        view.Show(work.Id);
        Check.Equal((3, 1, 0.25, true, true), (view.OpenCount, view.DoneToday, view.Progress, view.HasOpen, view.HasDone), "Work: 2 open + the focus, 1 done today");
        Check.True(view.FocusTask == now, "Work: the focus task");
        view.Show(dev.Id);
        Check.Equal((1, 0, 0.0, false), (view.OpenCount, view.DoneToday, view.Progress, view.HasDone), "Dev: its own, without the focus elsewhere");
        Check.True(view.FocusTask == d1, "Dev: the top of its list");
        view.Show(SpaceView.All);
        Check.Equal((ws.OpenCount, ws.DoneToday, ws.Progress), (view.OpenCount, view.DoneToday, view.Progress), "All: everything");
        Check.Equal(5, view.OpenCount, "4 open + the focus");

        view.Show(work.Id);
        ws.Complete(w2);
        Check.Equal((2, 2), (view.OpenCount, view.DoneToday), "a tick counts at once");
    }

    private static void NowElsewhere()
    {
        var ws = NewWs();
        var work = ws.AddSpace("Work");
        var dev = ws.AddSpace("Dev");
        var now = ws.AddTask("Review the clashes", asNow: true, space: work.Id);
        var view = new SpaceView(ws);
        view.Show(work.Id);
        Check.True(view.Now == now && view.HasNow && !view.HasNowElsewhere, "its own space: the card");
        view.Show(SpaceView.All);
        Check.True(view.Now == now && !view.HasNowElsewhere, "All: the card");
        Check.Equal("Work", view.NowLabel, "with its space on it");
        view.Show(dev.Id);
        Check.True(view.Now == null && !view.HasNow && view.NowElsewhere == now, "elsewhere: a line, no card");
        Check.Equal("Now in Work: ", view.NowElsewhereLabel, "the line");
        Check.Equal("Now in Work: Review the clashes", view.NowLineName, "what screen readers hear");
        Check.Equal(null, view.NowLabel, "no label on a card that isn't there");
        ws.MoveTaskToSpace(now.Id, dev.Id);
        Check.True(view.Now == now && !view.HasNowElsewhere, "moved here: the card");
    }

    private static void MovesWithinSpace()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001", "dev000000001", "w3", "d2", "w2", "d1", "w1"]));
        var work = ws.AddSpace("Work");
        var dev = ws.AddSpace("Dev");
        // Added bottom first: the one order is w1, d1, w2, d2, w3.
        var w3 = ws.AddTask("w3", space: work.Id);
        var d2 = ws.AddTask("d2", space: dev.Id);
        var w2 = ws.AddTask("w2", space: work.Id);
        var d1 = ws.AddTask("d1", space: dev.Id);
        var w1 = ws.AddTask("w1", space: work.Id);
        var view = new SpaceView(ws);
        view.Show(work.Id);
        var devBefore = new[] { d1.ToData(), d2.ToData() };

        Check.Equal(1, Stamps(ws, () => view.MoveOpen(0, 2)), "down to the bottom: one stamp");
        Check.Sequence(new[] { w2, w3, w1 }, view.Open.ToList(), "Work, as you see it");
        Check.Sequence(new[] { d1, w2, d2, w3, w1 }, ws.Open.ToList(), "just below the last task it passed");

        Check.Equal(1, Stamps(ws, () => view.MoveOpen(2, 0)), "back to the top: one stamp");
        Check.Sequence(new[] { w1, w2, w3 }, view.Open.ToList(), "Work again");
        Check.Sequence(new[] { d1, w1, w2, d2, w3 }, ws.Open.ToList(), "just above the task it passed");

        view.MoveOpen(1, 2);
        Check.Sequence(new[] { w1, w3, w2 }, view.Open.ToList(), "one down");
        Check.Sequence(new[] { d1, w1, d2, w3, w2 }, ws.Open.ToList(), "past d2, which it never saw");
        Check.Sequence(devBefore, new[] { d1.ToData(), d2.ToData() }, "Dev's tasks never re-keyed");
        Check.Equal(0, Stamps(ws, () => view.MoveOpen(1, 1)), "nowhere: nothing");
        Check.Equal(0, Stamps(ws, () => view.MoveOpen(0, 5)), "out of range: nothing");
    }

    private static void MovesWithinSpaceTied()
    {
        // Tasks from another device, two of them (or all) sharing a key: what two devices adding at the top at once leave.
        (Workspace Ws, SpaceView View, string Work) Tied(params (string Id, string Order, bool InWork)[] specs)
        {
            var ws = NewWs(ids: new(["sheet0000001", "work00000001", "s0000000000s"]));
            var work = ws.AddSpace("Work");
            string w = Stamp.Encode(Now - 5000, 0, "x");
            var tasks = specs.Select(s => new TaskData(s.Id, s.Id, "2026-10-21T08:00:00.000+00:00", null, s.Order, false, s.InWork ? work.Id : "",
                TaskClocks.All(w) with { Space = s.InWork ? w : Stamp.Zero })).ToList();
            ws.MergeFrom(new WorkspaceData(tasks, [], FocusData.None, []));
            var view = new SpaceView(ws);
            view.Show(work.Id);
            return (ws, view, work.Id);
        }
        static List<string> Ids(IEnumerable<TaskItem> list) => list.Select(t => t.Id).ToList();
        static Dictionary<string, TaskData> Records(Workspace ws) => ws.Open.ToDictionary(t => t.Id, t => t.ToData());
        static List<string> Changed(Workspace ws, Dictionary<string, TaskData> before) =>
            ws.Open.Where(t => !before.TryGetValue(t.Id, out var b) || b != t.ToData()).Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

        // Going down: just below P would re-key Q (Main), tied with it. Below Q looks the same in Work.
        var (ws, view, work) = Tied(("pppppppppppp", "T", true), ("qqqqqqqqqqqq", "T", false), ("rrrrrrrrrrrr", "V", true));
        var s = ws.AddTask("S", space: work);
        var before = Records(ws);
        Check.Equal(1, Stamps(ws, () => view.MoveOpen(0, 1)), "down: one stamp");
        Check.Sequence(new[] { "pppppppppppp", s.Id, "rrrrrrrrrrrr" }, Ids(view.Open), "Work, as you see it");
        Check.Sequence(new[] { s.Id }, Changed(ws, before), "only the task moved");

        // Going up: just above N would re-key N and the Main task tied with it below.
        (ws, view, work) = Tied(("hhhhhhhhhhhh", "T", false), ("nnnnnnnnnnnn", "T", true), ("oooooooooooo", "T", false), ("sssssssssss1", "X", true));
        before = Records(ws);
        Check.Equal(1, Stamps(ws, () => view.MoveOpen(1, 0)), "up: one stamp");
        Check.Sequence(new[] { "sssssssssss1", "nnnnnnnnnnnn" }, Ids(view.Open), "Work, as you see it");
        Check.Sequence(new[] { "sssssssssss1" }, Changed(ws, before), "only the task moved");

        // Every place between its neighbours ties: only Work's tasks make room.
        (ws, view, work) = Tied(("pppppppppppp", "T", true), ("qqqqqqqqqqqq", "T", false), ("rrrrrrrrrrrr", "T", true));
        s = ws.AddTask("S", space: work);
        before = Records(ws);
        Check.Equal(2, Stamps(ws, () => view.MoveOpen(0, 1)), "the task, then the one making room");
        Check.Sequence(new[] { "pppppppppppp", s.Id, "rrrrrrrrrrrr" }, Ids(view.Open), "Work, as you see it");
        Check.Sequence(new[] { "rrrrrrrrrrrr", s.Id }.OrderBy(id => id, StringComparer.Ordinal).ToList(), Changed(ws, before), "Work's tasks only");
        Check.Sequence(ws.Open.OrderBy(t => t.Order, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal).ToList(), ws.Open.ToList(), "the list in key order");
    }

    private static void ClearDoneOnShow()
    {
        var ws = NewWs();
        var work = ws.AddSpace("Work");
        var w = ws.AddTask("w", space: work.Id);
        var m = ws.AddTask("m");
        ws.Complete(w);
        ws.Complete(m);
        var view = new SpaceView(ws);
        view.Show(work.Id);
        var undo = view.ClearDone();
        Check.True(w.IsDeleted && !m.IsDeleted, "only Work's");
        Check.Sequence(new[] { m }, ws.Done.ToList(), "Main's stays");
        Check.Equal(0, view.Done.Count, "Work's Done is empty");
        undo();
        Check.True(!w.IsDeleted && view.Done.Contains(w), "Undo brings it back");
        view.Show(SpaceView.All);
        view.ClearDone();
        Check.Equal(0, ws.Done.Count, "All clears all");
    }

    private static void Labels()
    {
        var ws = NewWs();
        var a = ws.AddTask("a");
        var view = new SpaceView(ws);
        view.Show(SpaceView.All);
        Check.Equal(null, a.SpaceLabel, "no spaces: no label");
        var work = ws.AddSpace("Work");
        var b = ws.AddTask("b", space: work.Id);
        var now = ws.AddTask("now", asNow: true, space: work.Id);
        ws.Complete(b);
        Check.Equal(("Main", "Work", "Work"), (a.SpaceLabel, b.SpaceLabel, now.SpaceLabel), "All: each one's space");
        ws.RenameSpace(work.Id, "Office");
        Check.Equal("Office", b.SpaceLabel, "renamed: relabelled");
        view.Show(work.Id);
        Check.Equal(((string?)null, (string?)null, (string?)null), (a.SpaceLabel, b.SpaceLabel, now.SpaceLabel), "in a space: no labels");
    }

    private static void NewItems()
    {
        var ws = NewWs();
        var work = ws.AddSpace("Work");
        var view = new SpaceView(ws);
        view.Show(work.Id);
        Check.Equal((work.Id, "Add to Work", "Add a task to Work"), (view.NewItemSpace, view.AddPlaceholder, view.AddName), "into Work");
        var t = ws.AddTask("x", space: view.NewItemSpace);
        Check.True(view.Open.Contains(t) && t.Space == work.Id, "added where it's seen");
        view.Show(SpaceField.Main);
        Check.Equal("Add to Main", view.AddPlaceholder, "Main, by its name");
        Check.Equal("", ws.AddTask("y", space: view.NewItemSpace).Space, "Main: no space");
        view.Show(SpaceView.All);
        Check.Equal((SpaceField.Main, "Add a task", "Add a task"), (view.NewItemSpace, view.AddPlaceholder, view.AddName), "All: into Main, the placeholder as before");
    }

    private static void BlankPaper()
    {
        var ws = NewWs();
        var work = ws.AddSpace("Work");
        var dev = ws.AddSpace("Dev");
        var mainSheet = ws.Sheets[0];
        var workSheet = ws.AddSheet(work.Id, "work");
        var view = new SpaceView(ws);
        int sheets = ws.Sheets.Count;

        view.Show(dev.Id);
        Check.True(view.ActiveSheet == null && view.Sheets.Count == 0, "Dev: blank paper");
        Check.Equal(sheets, ws.Sheets.Count, "no sheet made by looking");
        view.Show(work.Id);
        Check.True(view.ActiveSheet == workSheet && ws.ActiveSheet == workSheet, "Work: its sheet, which Tack opens on next time");
        view.Show(dev.Id);
        var first = ws.AddSheet(view.NewItemSpace, "f"); // what the first keystroke does
        Check.True(view.ActiveSheet == first && view.Sheets.Contains(first) && first.Space == dev.Id, "the first keystroke made Dev's sheet");
        Check.Equal("f", first.Text, "with it");
        view.Show(SpaceField.Main);
        Check.True(view.ActiveSheet == mainSheet, "Main: its sheet");
        view.Show(dev.Id);
        Check.True(view.ActiveSheet == first, "each space remembers its sheet");
        ws.MoveSheetToSpace(first.Id, work.Id);
        Check.True(view.ActiveSheet == null, "moved away: blank paper again");
        view.Show(SpaceView.All);
        Check.True(view.ActiveSheet != null && ReferenceEquals(view.Sheets, ws.Sheets), "All: every sheet");
    }

    private static void SavedInSettings()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001"]));
        var view = new SpaceView(ws);
        Check.True(!SettingsJson(ws).Contains("spaceOnShow"), "never chosen: not in the file (as before)");
        var work = ws.AddSpace("Work");
        view.Show(work.Id);
        Check.True(SettingsJson(ws).Contains("\"spaceOnShow\": \"work00000001\""), "chosen: in the settings");
        view.Show(SpaceField.Main);
        Check.True(!SettingsJson(ws).Contains("spaceOnShow"), "back to Main: gone again");

        // Read back as a PC would, it's what was chosen.
        view.Show(SpaceView.All);
        var doc = JsonSerializer.Deserialize(Encoding.UTF8.GetBytes(SettingsJson(ws)), StateJsonContext.Default.StateDocument)!;
        Check.Equal(SpaceView.All, doc.Settings.SpaceOnShow, "read back");
        Check.True(!TackFileJson(ws).Contains("spaceOnShow"), "never in what other devices get");
    }

    private static void ListedInPages()
    {
        var tabs = Enumerable.Range(0, SpaceView.MaxListed + 5).Select(i => new SpaceTab($"s{i}", $"space {i}")).ToList();
        var (few, none) = SpaceView.Listed(tabs.Take(3).ToList(), SpaceView.MaxListed);
        Check.Equal(3, few.Count, "a few: all of them");
        Check.Equal(0, none, "and none left out");
        var (first, more) = SpaceView.Listed(tabs, SpaceView.MaxListed);
        Check.Equal(SpaceView.MaxListed, first.Count, "a thousand at a time");
        Check.Equal(5, more, "the rest left out");
        Check.True(first.Select((l, i) => l.Index == i && l.Tab == tabs[i]).All(b => b), "in order, with their places");
        var (kept, fewer) = SpaceView.Listed(tabs, SpaceView.MaxListed, $"s{SpaceView.MaxListed + 3}");
        Check.Equal((tabs[SpaceView.MaxListed + 3], SpaceView.MaxListed + 3), kept[^1], "the space on show is always drawn");
        Check.Equal(4, fewer, "one fewer left out");
        Check.Equal(0, SpaceView.Listed(tabs, 2 * SpaceView.MaxListed).More, "More spaces: as many again");

        var ws = new Workspace(new Settings(), new Hlc("desk0001", () => Now), WorkspaceData.Empty);
        for (int i = 0; i < SpaceView.MaxListed + 5; i++) ws.AddSpace($"space {i}");
        var view = new SpaceView(ws);
        string last = ws.SpaceList[^1].Id;
        Check.Equal($"space {SpaceView.MaxListed + 4}", view.NameOf(last), "a name past the first thousand");
        Check.Equal("Main", view.NameOf(SpaceField.Main), "Main");
        Check.Equal("Untitled", view.NameOf("gone"), "a space that isn't there");
    }

    // ---------------------------------------------------------------- helpers

    private static (Workspace Ws, SpaceData Work, SpaceData Dev, SpaceData Home) ThreeSpaces()
    {
        var ws = NewWs(ids: new(["sheet0000001", "work00000001", "dev000000001", "home00000001"]));
        return (ws, ws.AddSpace("Work"), ws.AddSpace("Dev"), ws.AddSpace("Home"));
    }

    private static List<string> Order(Workspace ws) => ws.ShownSpaces.Select(s => s.Id).ToList();

    private static string SettingsJson(Workspace ws) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(StateFile.ToDocument(ws), StateJsonContext.Default.StateDocument));

    private static string TackFileJson(Workspace ws)
    {
        using var buffer = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(buffer)) TackFile.WriteSection(w, ws.ToData(), "", null);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

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
}
