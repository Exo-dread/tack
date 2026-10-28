using System.IO;
using System.Text;
using System.Text.Json;
using Tack.Model;
using Tack.Services;
using Tack.Snippets;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Files in the sync folder that a broken or hostile writer made (the same cases as
/// web/tests/unit/synchostile.test.ts). Anyone who can write that folder is trusted to edit, by
/// design; but a bad file must never stop sync, swell everyone's files for good, or hide another
/// device's changes after it's gone.
/// </summary>
internal static class HostileFileTests
{
    private static readonly long Now = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private const long Day = 86_400_000;

    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("hostile files, M1: half a character (\\ud800) or bad UTF-8 makes a file unreadable, never an error", HalfCharacters);
        yield return ("hostile files, M1: such a file is set aside and the rest still syncs", HalfCharactersDontStopSync);
        yield return ("hostile files, M1: an import with such text says so", HalfCharactersInImport);
        yield return ("hostile files, M1: such text in this PC's own tack.json doesn't stop Tack opening", HalfCharactersAtStartup);
        yield return ("hostile files, M2: ids like \"constructor\" sync like any other", BuiltInNames);
        yield return ("hostile files, M3: one file vouching for another can't hide that device's changes", Vouching);
        yield return ("hostile files, M4: limits on records and text; more devices seen are marked", Limits);
        yield return ("hostile files, M4: only real dates, sane clocks and order keys", Fields);
        yield return ("hostile files, M4: won't write an own file every device would refuse", TooLargeToWrite);
        yield return ("hostile files, L1: never reads a file the listing shows to be too big", NeverReadsTooBig);
        yield return ("hostile files, L1: a file that fails to read costs only that file", OneFailedRead);
        yield return ("hostile files, L7: an empty \"completed\" means not done", EmptyCompleted);
        yield return ("hostile files, L8: clocks don't adopt stamps more than a year and a day ahead", ClockWindow);
        yield return ("second audit, M1: a file claiming a thousand devices seen can't stop honest devices syncing", SeenFlood);
        yield return ("second audit, M1: trimming seen keeps the devices in the folder, then the latest", TrimsSeen);
        yield return ("second audit, M2: a file that would fill this PC is set aside, its own file stays readable", RecordsBudget);
        yield return ("second audit, M2: files are taken in while there is room, together", RecordsBudgetTogether);
        yield return ("second audit, M2: an own file every device would refuse is never written, and said every cycle", TooLargeEveryCycle);
        yield return ("second audit, M2: never reads more of a file than a device file may hold", BoundedRead);
        yield return ("regression review: a file passing on too many claims still counts those about devices in the folder", SeenOverClaims);
        yield return ("regression review: a peer's everyday edits still arrive after a flood filled this PC", FloodTrickle);
        yield return ("regression review: a bulk file is held to what this PC takes in at once, every file to the most one carries", BulkHeld);
        yield return ("regression review: only spaces that show count against the spaces budget", SpacesLiveOnly);
        yield return ("final pass, M1: spaces are records: however many show, new ones or deleted ones brought back, they're taken in", SpacesTakenAsRecords);
        yield return ("final pass, M1: at a thousand spaces, an honest PC that adds a space still reaches every other device", () => SpacesAtTheCap(ws => ws.AddSpace("Groceries")));
        yield return ("final pass, M1: at a thousand spaces, an honest PC that renames Main still reaches every other device", () => SpacesAtTheCap(ws => ws.RenameSpace(SpaceField.Main, "Inbox")));
        yield return ("final pass, M1: at a thousand spaces, an honest PC that moves Main still reaches every other device", () => SpacesAtTheCap(ws => ws.MoveSpace(SpaceField.Main, 1)));
        yield return ("final pass, M1: at a thousand spaces, an honest PC that undoes a space's deletion still reaches every other device", SpacesAtTheCapUndo);
        yield return ("final pass, M1: at a thousand spaces, an honest PC that deletes one space and adds another still reaches every other device", () => SpacesAtTheCap(ws =>
        {
            ws.DeleteSpace(Work(ws));
            ws.AddSpace("Garden");
        }));
        yield return ("final pass, M1: a file landing while one PC has a space the other hasn't seen yet splits neither way", SpacesSplitNeitherWay);
        yield return ("final pass, M1: an import with more than a thousand spaces is taken, here and by every peer", SpacesImported);
        yield return ("final pass, L1: a file sized between two PCs' totals can be taken in anyway, which heals the split", TakeInAnyway);
        yield return ("final pass, L1: a request to take a file in is for the version set aside, and never past what a file may carry", TakeInBounded);
        yield return ("final pass, L2: a PC taking back its own file knows again what it had seen: an old device's older note text isn't kept as a copy", AdoptKnowsSeen);
        yield return ("second audit, L1: imports no more than a device can pass on", ImportLimits);
        yield return ("second audit, L1: imports skip text longer than any device reads", ImportLongText);
        yield return ("second audit, L2: imported dates are kept only as every device reads them", ImportDates);
        yield return ("snippets, hostile paste: a 20000x20000 PNG (a few KB of black) is turned down from its header, nothing decoded", PngBomb);
        yield return ("snippets, hostile paste: a DIB claiming 20000x20000 is turned down before a byte of it is copied", DibBomb);
        yield return ("snippets, hostile paste: a 20000x20000 bitmap is turned down without drawing it", BitmapBomb);
        yield return ("snippets, hostile paste: damaged PNGs are refused or kept whole, never an error or half a snippet", CorruptPngs);
        yield return ("snippets, hostile paste: damaged DIB headers are refused", CorruptDibs);
        yield return ("snippets, hostile paste: copied files on shares, device paths, cloud placeholders, oversized or misnamed are never read", HostileFiles);
        yield return ("snippets, hostile snippets.json: entries naming other files or out of range are dropped; only Tack's own files are cleared", HostileIndex);
        yield return ("snippets, hostile snippets.json: one that can't be read (nor its .bak) clears no file, is kept aside and never written over", UnreadableSnippetIndex);
        yield return ("snippets, hostile snippets.json: entries past the caps aren't loaded, and their files aren't cleared away", SnippetsPastTheCaps);
        yield return ("snippets, hostile snippet file: one replaced by a vast, damaged or oversized image shows an empty frame, no error", HostileStoredFile);
        // Dropping onto the orb: hostile drags (huge lists, javascript: links, device paths, endless streams) are in DropTests.
        yield return ("attachments.json: a damaged or hand-edited file can't point a paperclip anywhere a drop couldn't; the good entries still load", AttachmentsEntries);
        yield return ("attachments.json: too large, too deep or too many is bounded, and the backup takes over", AttachmentsBounds);
        yield return ("attachments.json: one that can't be read (nor its .bak) sweeps no saved email, is kept aside and never written over", UnreadableAttachments);
        yield return ("markdown, hostile notes: runs of 100,000 delimiters, brackets or backticks, deep quotes and indents, 1 MB and lone surrogates read in linear time, no error", HostileMarkdown);
    }

    /// <summary>
    /// A note is synced text: anyone who can write the sync folder chooses it. Reading it as Markdown (docs/NOTES-MARKDOWN.md)
    /// must cost about what its length does, never overflow the stack, and keep every character.
    /// </summary>
    private static void HostileMarkdown()
    {
        const int N = 100_000;
        static string Repeat(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();
        static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        // The best of three. With noGc, inside a region without collections: what the reading itself costs. (A
        // collection's pause grows with what's alive, the half-read note included, so it isn't linear in the text:
        // timed with it, twice the text can cost three or four times as much. Without it, it costs twice.)
        static long Time(string text, bool noGc = false)
        {
            long best = long.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                bool region = noGc && GC.TryStartNoGCRegion(200_000_000);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Markdown.Parse(text);
                best = Math.Min(best, clock.ElapsedTicks);
                if (region && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
            }
            return best;
        }

        // What 100,000 letters cost: the yardstick (the best of three, after a warm-up).
        string letters = new('a', N);
        Markdown.Parse(letters);
        double baseline = Ms(Time(letters, noGc: true));

        var cases = new (string Name, string Text)[]
        {
            ("100,000 *", new string('*', N)),
            ("100,000 _", new string('_', N)),
            ("100,000 ~", new string('~', N)),
            ("100,000 [", new string('[', N)),
            ("100,000 ]", new string(']', N)),
            ("100,000 `", new string('`', N)),
            ("*a 50,000 times", Repeat("*a", N / 2)),
            ("**a* 25,000 times", Repeat("**a*", N / 4)),
            ("_a* 33,000 times", Repeat("_a*", N / 3)),
            ("~~a 33,000 times", Repeat("~~a", N / 3)),
            ("*_ nested 25,000 deep", Repeat("*_", N / 4) + "x" + Repeat("_*", N / 4)),
            ("* a _ nested 16,000 deep", Repeat("*a _b ", N / 6) + "x" + Repeat(" b_ a*", N / 6)),
            ("[a]( 25,000 times", Repeat("[a](", N / 4)),
            ("[ ]( nested 33,000 deep", new string('[', N / 3) + new string(']', N / 3) + "(x)"),
            ("[*a]( and )* 12,000 times", Repeat("[*a](", N / 10) + Repeat("b*)", N / 10)),
            ("backtick runs 1, 2, 3, ...", string.Concat(Enumerable.Range(1, 440).Select(k => new string('`', k) + "a"))),
            ("\\\\* 33,000 times", Repeat("\\\\*", N / 3)),
            ("100,000 > on a line", new string('>', N) + " a"),
            ("> nested 1,000 deep, 100 lines", Repeat(string.Join(" ", Enumerable.Repeat(">", 1000)) + " a\n", 100)),
            ("indents 2 deeper each line, 2,000 lines", string.Concat(Enumerable.Range(0, 2000).Select(k => new string(' ', 2 * k) + "- a\n"))),
            ("indents back and forth", Repeat("- a\n" + new string(' ', 16) + "- b\n  c\n", N / 30)),
            ("100,000 spaces then an item", new string(' ', N) + "- a"),
            ("100,000 tabs then a heading", new string('\t', N) + "# a"),
            ("50,000 lines of #", Repeat("#\n", N / 2)),
            ("25,000 one-line fences", Repeat("```\n", N / 4)),
            ("an unclosed fence, then 50,000 lines", "```\n" + Repeat("x\n", N / 2)),
            ("12,000 tasks", Repeat("- [ ] a\n", N / 8)),
            ("100,000 CRs", new string('\r', N)),
        };
        foreach (var (name, text) in cases)
        {
            // Linear: twice the text costs about twice as much as the first half of it; and, collections and all, never
            // more than a few hundred ms per 100,000 characters, however many delimiters it has.
            double took = Ms(Time(text, noGc: true)), half = Ms(Time(text[..(text.Length / 2)], noGc: true));
            Check.True(took <= 3 * half + 2, $"{name}: not linear: {took:F1} ms for all of it, {half:F1} ms for half");
            double all = Ms(Time(text));
            double per = all / Math.Max(1, text.Length) * N;
            Check.True(per <= 250, $"{name}: {all:F1} ms for {text.Length:N0} characters ({per:F1} ms per 100,000; letters {baseline:F1})");
        }
        // A run of 100,000 * (or [ or `) costs no more than 100,000 letters, give or take the noise of a fraction of a ms.
        foreach (char c in new[] { '*', '[', '`', '_', '~' })
        {
            double run = Ms(Time(new string(c, N), noGc: true));
            Check.True(run <= 3 * baseline + 2, $"100,000 {c}: {run:F2} ms, letters {baseline:F2}");
        }

        // A whole megabyte of everything at once: the longest note any device takes in.
        var mixed = new StringBuilder();
        var random = new Random(11);
        string[] pieces = ["# h ", "- [x] ", "> ", "*", "**", "_", "~~", "`", "``", "[", "](", ")", "\\", "word ", "\n", "\r\n", "  ", "1. ", "---", "```", "\t"];
        while (mixed.Length < DeviceFile.MaxNoteText) mixed.Append(pieces[random.Next(pieces.Length)]);
        mixed.Length = DeviceFile.MaxNoteText;
        var whole = System.Diagnostics.Stopwatch.StartNew();
        var blocks = Markdown.Parse(mixed.ToString());
        Check.True(whole.ElapsedMilliseconds < 3000 && blocks.Count > 0, $"1 MB of everything: {whole.ElapsedMilliseconds} ms");

        // Lone surrogates stay as they are, wherever they are.
        string halves = "\ud800 *\udc00* `\ud800` [\udfff](\udbff)\n- [ ] \udbfe";
        string shown = string.Concat(MarkdownTexts(Markdown.Parse(halves)));
        Check.Equal(halves.Count(char.IsSurrogate), shown.Count(char.IsSurrogate), "every half kept: " + string.Join(" ", shown.Select(c => ((int)c).ToString("X4"))));
    }

    /// <summary>Every piece of text a tree shows (and its links' urls), in order.</summary>
    internal static IEnumerable<string> MarkdownTexts(IEnumerable<MdBlock> blocks)
    {
        static IEnumerable<string> Inline(IEnumerable<MdInline> inlines)
        {
            foreach (var i in inlines)
            {
                switch (i)
                {
                    case MdText t:
                        yield return t.Text;
                        break;
                    case MdCode c:
                        yield return c.Text;
                        break;
                    case MdStyle s:
                        foreach (var x in Inline(s.Content)) yield return x;
                        break;
                    case MdLink l:
                        foreach (var x in Inline(l.Content)) yield return x;
                        yield return l.Url;
                        break;
                }
            }
        }
        foreach (var b in blocks)
        {
            var parts = b switch
            {
                MdHeading h => Inline(h.Content),
                MdParagraph p => Inline(p.Content),
                MdQuote q => MarkdownTexts(q.Blocks),
                MdCodeBlock c => [c.Text],
                MdList l => l.Items.SelectMany(it => Inline(it.Content).Concat(MarkdownTexts(it.Sub))),
                _ => [],
            };
            foreach (var x in parts) yield return x;
        }
    }

    // ---------------------------------------------------------------- attachments.json (local, never synced)

    private static void AttachmentsEntries()
    {
        string folder = Path.Combine(Path.GetTempPath(), "tack-drop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new Tack.Drop.Core.AttachmentStore(folder, Tack.Drop.DesktopDrop.IsProgram);
            string longLabel = "<a href=\"javascript:x\">" + new string('L', 10_000) + "\n second line";
            string json = $$"""
            { "version": 99, "future": { "anything": [1, 2, 3] }, "attachments": [
              { "task": "aaaaaaaaaaaa", "kind": "link", "target": "javascript:alert(1)", "label": "x" },
              { "task": "aaaaaaaaaaab", "kind": "link", "target": "file://\\\\evil\\share\\x.exe", "label": "x" },
              { "task": "aaaaaaaaaaac", "kind": "link", "target": "https://user:pw@acc.autodesk.com/", "label": "x" },
              { "task": "bbbbbbbbbbbb", "kind": "file", "target": "..\\..\\Windows\\notepad.exe", "label": "x", "saved": true },
              { "task": "bbbbbbbbbbbc", "kind": "file", "target": "C:\\Users\\me\\Desktop\\x.pdf", "label": "x", "saved": true },
              { "task": "cccccccccccc", "kind": "file", "target": "0123456789ab\\run.exe", "label": "x", "saved": true },
              { "task": "cccccccccccd", "kind": "file", "target": "0123456789ab\\Mail.eml:payload.exe", "label": "x", "saved": true },
              { "task": "dddddddddddd", "kind": "program", "target": "C:\\x.exe", "label": "x" },
              { "task": "../../escape", "kind": "file", "target": "C:\\x.pdf", "label": "x" },
              { "task": "eeeeeeeeeeee", "kind": "file", "target": "\\\\?\\C:\\x.pdf", "label": "x" },
              { "task": "eeeeeeeeeeef", "kind": "folder", "target": "Projects\\relative", "label": "x" },
              { "task": "ffffffffffff", "kind": "folder", "target": "C:\\Projects", "label": {{System.Text.Json.JsonSerializer.Serialize(longLabel)}} },
              { "task": "ffffffffffff", "kind": "link", "target": "https://second.example/", "label": "the same task again" },
              { "task": "111111111111", "kind": "link", "target": "https://acc.autodesk.com/docs", "label": "ACC", "opened": 3 },
              { "task": "222222222222", "kind": "file", "target": "0123456789ab\\Mail.eml", "label": "Mail.eml", "saved": true },
              { "task": "333333333333", "kind": "link", "target": 123, "label": "x" },
              { "task": "444444444444", "kind": "link", "target": "https://x.example/\ud800", "label": "half a character" },
              { "task": "555555555555", "kind": "file", "target": "\\\\fileserver\\Projects\\x.pdf", "label": "x.pdf", "saved": "yes" },
              "a string", null, 7, [ ]
            ]}
            """;
            store.Parse(Encoding.UTF8.GetBytes(json));
            var kept = store.All.ToDictionary(e => e.TaskId, e => e.Attachment);
            Check.Sequence(["ffffffffffff", "111111111111", "222222222222", "555555555555"], kept.Keys.ToList(), "only what a drop could have made, each task once");
            Check.Equal(Tack.Drop.Core.AttachmentKind.Folder, kept["ffffffffffff"].Kind, "the first entry for a task wins");
            Check.True(kept["ffffffffffff"].Label.Length <= Tack.Drop.Core.DropLimits.MaxLabel && !kept["ffffffffffff"].Label.Contains('\n'), "a label is one line, bounded");
            Check.Equal("https://acc.autodesk.com/docs", kept["111111111111"].Target, "an http(s) page");
            Check.True(kept["222222222222"].Saved, "a saved copy inside the folder");
            Check.True(!kept["555555555555"].Saved, "\"saved\" means true, nothing else");
            Check.Equal(@"\\fileserver\Projects\x.pdf", kept["555555555555"].Target, "a network file, as dropped");

            // Written back, only what was kept.
            store.Save();
            string written = File.ReadAllText(store.FilePath);
            Check.True(!written.Contains("javascript:alert", StringComparison.Ordinal) && !written.Contains("notepad", StringComparison.Ordinal) && !written.Contains("future", StringComparison.Ordinal), "nothing refused is written back");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private static void AttachmentsBounds()
    {
        string folder = Path.Combine(Path.GetTempPath(), "tack-drop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, Tack.Drop.Core.AttachmentStore.FileName);
            string Entry(int i) => $$"""{ "task": "t{{i:x10}}", "kind": "link", "target": "https://x.example/{{i}}", "label": "x" }""";
            File.WriteAllText(path + ".bak", $$"""{ "attachments": [ {{Entry(1)}} ] }""");

            // Too large: never read; the backup is.
            using (var f = new FileStream(path, FileMode.Create)) f.SetLength(Tack.Drop.Core.DropLimits.MaxFileBytes + 1);
            var store = new Tack.Drop.Core.AttachmentStore(folder);
            var errors = new List<string>();
            store.Load((m, _) => errors.Add(m));
            Check.Equal(1, store.Count, "too large: the backup instead");
            Check.True(errors.Count == 1, "and the log says so");

            // Too deep: unreadable as a whole; the backup again.
            File.WriteAllText(path, "{\"attachments\": [" + new string('[', 200) + new string(']', 200) + "]}");
            store.Load();
            Check.Equal(1, store.Count, "too deep: the backup");

            // Not an attachments file at all.
            File.WriteAllText(path, "[1, 2, 3]");
            store.Load();
            Check.Equal(1, store.Count, "not one: the backup");

            // Too many: the first ones, up to the cap.
            File.WriteAllText(path, "{ \"attachments\": [" + string.Join(",", Enumerable.Range(0, Tack.Drop.Core.DropLimits.MaxAttachments + 1000).Select(Entry)) + "] }");
            store.Load();
            Check.Equal(Tack.Drop.Core.DropLimits.MaxAttachments, store.Count, "never more than the cap");
            Check.True(store.For("t0000000000") != null && store.For($"t{Tack.Drop.Core.DropLimits.MaxAttachments:x10}") == null, "the first ones");
            Check.True(!store.Add("tffffffffff", new Tack.Drop.Core.Attachment(Tack.Drop.Core.AttachmentKind.Link, "https://y.example/", "y")), "and no more added");

            // Neither file: nothing, and nothing created.
            File.Delete(path);
            File.Delete(path + ".bak");
            store.Load();
            Check.True(store.Count == 0 && !File.Exists(path) && !Directory.Exists(store.Folder), "no attachments, no file made");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private static void UnreadableAttachments()
    {
        string folder = Path.Combine(Path.GetTempPath(), "tack-drop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new Tack.Drop.Core.AttachmentStore(folder);
            string id = "abcdefabcdef", mail = Path.Combine(store.Folder, id, "Site visit.msg");
            Directory.CreateDirectory(Path.GetDirectoryName(mail)!);
            File.WriteAllText(mail, "pretend");
            Check.True(store.Add("bbbbbbbbbbbb", Tack.Drop.Core.Attachment.Clean(Tack.Drop.Core.AttachmentKind.File, id + "\\Site visit.msg", "Site visit.msg", true, _ => false)!), "kept");
            store.Save();
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(mail)!, DateTime.UtcNow.AddDays(-3)); // dropped days ago
            string path = store.FilePath;
            foreach (var (name, text, bak) in new[]
            {
                ("cut short, its .bak too", "{ \"attachments\": ", "{ \"attachments\": "),
                ("a top-level array (another shape)", "[{\"task\":\"bbbbbbbbbbbb\"}]", "[]"),
                ("too large, no .bak", "{\"attachments\":[]}" + new string(' ', (int)Tack.Drop.Core.DropLimits.MaxFileBytes), null),
            })
            {
                File.WriteAllText(path, text);
                if (bak != null) File.WriteAllText(path + ".bak", bak);
                else File.Delete(path + ".bak");
                var again = new Tack.Drop.Core.AttachmentStore(folder);
                again.Load();
                Check.True(again.LeftAlone && again.Count == 0, $"{name}: unreadable, so left alone");
                Check.Equal(0, again.SweepOrphans(DateTime.UtcNow.AddMinutes(1)), $"{name}: nothing swept");
                Check.True(File.Exists(mail), $"{name}: the saved email stays");
                Check.True(!again.Add("cccccccccccc", new Tack.Drop.Core.Attachment(Tack.Drop.Core.AttachmentKind.Link, "https://y.example/", "y")), $"{name}: nothing added");
                again.Save();
                Check.True(File.ReadAllText(path) == text, $"{name}: never written over");
                var copies = Directory.GetFiles(folder, Tack.Drop.Core.AttachmentStore.FileName + ".unreadable-*");
                Check.True(copies.Count(c => File.ReadAllText(c) == text) == (text.Length > 20 * 1024 * 1024 ? 0 : 1), $"{name}: kept aside, once");
                new Tack.Drop.Core.AttachmentStore(folder).Load();
                Check.Equal(copies.Length, Directory.GetFiles(folder, Tack.Drop.Core.AttachmentStore.FileName + ".unreadable-*").Length, $"{name}: no second copy at the next start");
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- Snippets (Tack for Windows only)
    // Images pasted into Notes and kept on this PC: the clipboard, files copied in Explorer, snippets.json
    // and the snippets folder are all input. Nothing here syncs, but a bad image must never take Tack down,
    // eat its memory, or reach anything but Tack's own files.

    private static void PngBomb()
    {
        Ui.Run(_ =>
        {
            byte[] bomb = SnippetTests.CraftPng(20000, 20000); // 1.6 GB once drawn
            Check.True(bomb.Length < 1 << 20, $"small on the clipboard ({bomb.Length:N0} bytes)");
            foreach (var (name, data) in new[]
            {
                ("on the clipboard", SnippetTests.Clip(("PNG", SnippetTests.Stream(bomb)))),
                ("from this process", (System.Windows.IDataObject)SnippetTests.Plain(("PNG", SnippetTests.Stream(bomb)))),
            })
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var found = SnippetImage.FromPaste(data);
                Check.Equal(PasteProblem.TooBig, found.Problem, $"{name}: turned down as too big");
                Check.Equal(0, found.Images.Count, $"{name}: nothing kept");
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check.True(allocated < 2 << 20, $"{name}: from its header, nothing decoded ({allocated:N0} bytes)");
                Check.True(clock.ElapsedMilliseconds < 2000, $"{name}: at once ({clock.ElapsedMilliseconds} ms)");
            }

            // The same as a file copied in Explorer, and as a kept snippet's file replaced by it.
            using var dir = new SnippetTests.Temp();
            string file = Path.Combine(dir.Path, "bomb.png");
            File.WriteAllBytes(file, bomb);
            Check.Equal(PasteProblem.TooBig, SnippetImage.FromPaste(SnippetTests.Clip((System.Windows.DataFormats.FileDrop, new[] { file }))).Problem, "copied in Explorer");
            Check.True(SnippetImage.Load(file) == null && SnippetImage.Load(file, (2000, 1000)) == null, "never shown");
        });
    }

    private static void DibBomb()
    {
        Ui.Run(_ =>
        {
            // A header claiming 20000x20000 at 32 bits (1.6 GB), with a kilobyte behind it.
            var dib = SnippetDib(width: 20000, height: 20000, bits: 32, extra: 1024);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var found = SnippetImage.FromPaste(SnippetTests.Clip((System.Windows.DataFormats.Dib, SnippetTests.Stream(dib))));
            Check.Equal(PasteProblem.TooBig, found.Problem, "turned down as too big");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check.True(allocated < 1 << 20, $"before it's copied ({allocated:N0} bytes)");

            // Within the limit, but cut short: what's there is less than the header says.
            var shortOne = SnippetDib(width: 1000, height: 1000, bits: 24, extra: 1024);
            Check.Equal(PasteProblem.Unreadable, SnippetImage.FromPaste(SnippetTests.Clip((System.Windows.DataFormats.Dib, SnippetTests.Stream(shortOne)))).Problem, "cut short: unreadable");
        });
    }

    private static void BitmapBomb()
    {
        Ui.Run(_ =>
        {
            // 20000x20000 that's never been drawn: drawing it would take 1.6 GB.
            var bomb = new System.Windows.Media.Imaging.TransformedBitmap(SnippetTests.Picture(20, 20), new System.Windows.Media.ScaleTransform(1000, 1000));
            bomb.Freeze();
            Check.Equal(20000, bomb.PixelWidth, "20000 wide");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var found = SnippetImage.FromPaste(SnippetTests.Plain((System.Windows.DataFormats.Bitmap, bomb)));
            Check.Equal(PasteProblem.TooBig, found.Problem, "turned down as too big");
            Check.True(clock.ElapsedMilliseconds < 2000, $"without drawing it ({clock.ElapsedMilliseconds} ms)");
        });
    }

    private static void CorruptPngs()
    {
        Ui.Run(errors =>
        {
            byte[] good = SnippetTests.Png(SnippetTests.Picture(200, 150));
            var random = new Random(7);
            byte[] Noise(int n)
            {
                var b = new byte[n];
                random.NextBytes(b);
                return b;
            }
            byte[] badCrc = (byte[])good.Clone();
            badCrc[29] ^= 0xFF; // in the IHDR chunk's CRC
            var cases = new (string Name, byte[] Bytes, bool MayKeep)[]
            {
                ("garbage after the signature", [.. good.Take(8), .. Noise(400)], false),
                ("a header and nothing else", good.Take(33).ToArray(), false),
                ("image data that isn't compressed data", SnippetTests.CraftPng(300, 200, idat: Noise(600)), true), // Windows' decoder makes something of it
                ("no size", SnippetTests.CraftPng(0, 0), false),
                ("a negative size", SnippetTests.CraftPng(-5, 10), false),
                ("cut short", good.Take(good.Length / 2).ToArray(), true),
                ("a damaged checksum", badCrc, true),
                ("a JPEG on the PNG format", SnippetJpeg(), false),
                ("empty", [], false),
            };
            using var dir = new SnippetTests.Temp();
            var store = new SnippetStore(dir.Path);
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            var host = new SnippetTests.Host();
            var notes = SnippetTests.Notes(ws, store, host);
            foreach (var (name, bytes, mayKeep) in cases)
            {
                var found = SnippetImage.FromPaste(SnippetTests.Clip(("PNG", SnippetTests.Stream(bytes))));
                Check.True(found.IsImage, $"{name}: taken as an image (not pasted as anything else)");
                if (found.Images.Count == 0)
                {
                    Check.True(found.Problem is PasteProblem.Unreadable or PasteProblem.TooBig, $"{name}: refused ({found.Problem})");
                }
                else
                {
                    Check.True(mayKeep, $"{name}: refused");
                    // Kept only whole: it decodes all the way, and has its thumbnail.
                    var kept = found.Images.Single();
                    Check.True(kept.Thumb != null, $"{name}: kept with a thumbnail");
                    var decoded = SnippetTests.Decode(kept.Bytes);
                    decoded.CopyPixels(new byte[decoded.PixelWidth * 4 * decoded.PixelHeight], decoded.PixelWidth * 4, 0);
                }
                int count = store.Count;
                Check.True(notes.Paste(SnippetTests.Clip(("PNG", SnippetTests.Stream(bytes)))), $"{name}: Notes takes the paste");
                Check.Equal(count + found.Images.Count, store.Count, $"{name}: nothing half-kept");
                if (found.Images.Count == 0) Check.Equal("Couldn't read that image", host.Toasts.LastOrDefault(), $"{name}: and says so");
            }
            Check.True(!Directory.Exists(store.Folder) || Directory.GetFiles(store.Folder).All(f => f.EndsWith(".png", StringComparison.Ordinal)), "no temp file left behind");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void CorruptDibs()
    {
        Ui.Run(_ =>
        {
            var cases = new (string Name, byte[] Dib)[]
            {
                ("an OS/2 header", SnippetDib(12, 12, 24, size: 12)),
                ("no width", SnippetDib(0, 10, 24)),
                ("no height", SnippetDib(10, 0, 24)),
                ("the most negative height", SnippetDib(10, int.MinValue, 24)),
                ("two planes", SnippetDib(10, 10, 24, planes: 2)),
                ("7 bits a pixel", SnippetDib(10, 10, 7)),
                ("a JPEG inside", SnippetDib(10, 10, 24, compression: 4)),
                ("a PNG inside", SnippetDib(10, 10, 24, compression: 5)),
                ("a billion colours in its table", SnippetDib(10, 10, 8, colors: 1_000_000_000)),
                ("run-length data of 4 GB", SnippetDib(10, 10, 8, compression: 1, imageSize: uint.MaxValue)),
                ("bit fields on 24 bits", SnippetDib(10, 10, 24, compression: 3)),
                ("half a header", SnippetDib(10, 10, 24).Take(20).ToArray()),
            };
            foreach (var (name, dib) in cases)
            {
                var found = SnippetImage.FromPaste(SnippetTests.Clip((System.Windows.DataFormats.Dib, SnippetTests.Stream(dib))));
                Check.True(found.Images.Count == 0 && found.Problem is PasteProblem.Unreadable or PasteProblem.TooBig, $"{name}: refused ({found.Problem})");
            }
        });
    }

    private static void HostileFiles()
    {
        Ui.Run(_ =>
        {
            using var dir = new SnippetTests.Temp();
            PasteProblem Problem(params string[] files) => SnippetImage.FromPaste(SnippetTests.Clip((System.Windows.DataFormats.FileDrop, files))).Problem;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (string path in new[] { @"\\fileserver\bim\clash.png", @"\\?\C:\Windows\clash.png", @"\\.\PhysicalDrive0\x.png", @"C:clash.png", "clash.png", @"//fileserver/bim/clash.png" })
                Check.Equal(PasteProblem.NotOnThisPc, Problem(path), $"{path}: never read");
            Check.True(clock.ElapsedMilliseconds < 1000, $"without trying the network ({clock.ElapsedMilliseconds} ms)");
            Check.True(!SnippetImage.IsOnThisPc(@"\\fileserver\bim\clash.png") && SnippetImage.IsOnThisPc(Path.Combine(dir.Path, "x.png")), "this PC's own drive, yes; a share, no");

            string big = Path.Combine(dir.Path, "big.png");
            using (var fs = File.Create(big)) fs.SetLength(SnippetImage.MaxEncodedBytes + 1);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Check.Equal(PasteProblem.TooBig, Problem(big), "a file over 50 MB: too big");
            Check.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20, "without reading it");

            string cloud = Path.Combine(dir.Path, "cloud.png");
            File.WriteAllBytes(cloud, SnippetTests.Png(SnippetTests.Picture(20, 20)));
            File.SetAttributes(cloud, FileAttributes.Offline);
            Check.Equal(PasteProblem.NotOnThisPc, Problem(cloud), "a file that isn't downloaded: never fetched");
            File.SetAttributes(cloud, FileAttributes.Normal);

            string misnamed = Path.Combine(dir.Path, "photo.png");
            File.WriteAllBytes(misnamed, SnippetJpeg());
            Check.Equal(PasteProblem.Unreadable, Problem(misnamed), "a JPEG called .png: only the PNG decoder is tried, and it says no");
            Directory.CreateDirectory(Path.Combine(dir.Path, "folder.png"));
            Check.Equal(PasteProblem.Unreadable, Problem(Path.Combine(dir.Path, "folder.png")), "a folder called .png");
            Check.Equal(PasteProblem.Unreadable, Problem(Path.Combine(dir.Path, "gone.png")), "a file that's gone");

            clock.Restart();
            var many = Enumerable.Range(0, 50_000).Select(i => Path.Combine(dir.Path, $"{i}.txt")).ToArray();
            Check.True(!SnippetImage.FromPaste(SnippetTests.Clip((System.Windows.DataFormats.FileDrop, many))).IsImage, "50,000 text files: nothing for Notes");
            Check.True(clock.ElapsedMilliseconds < 1000, $"and quickly: only the first thousand names are read ({clock.ElapsedMilliseconds} ms)");
            var lastOfMany = many.Take(999).Append(Path.Combine(dir.Path, "x.png")).Append(Path.Combine(dir.Path, "y.png")).ToArray();
            Check.Equal(PasteProblem.Unreadable, Problem(lastOfMany), "the thousandth name is read (and that file is gone)");
        });
    }

    private static void HostileIndex()
    {
        using var dir = new SnippetTests.Temp();
        string folder = Path.Combine(dir.Path, SnippetStore.FolderName), index = Path.Combine(dir.Path, SnippetStore.IndexName);
        Directory.CreateDirectory(folder);
        static string Id(int n) => n.ToString("x32");
        byte[] png = SnippetTests.Png(SnippetTests.Picture(20, 20));
        for (int n = 1; n <= 12; n++) File.WriteAllBytes(Path.Combine(folder, Id(n) + ".png"), png);
        string tack = Path.Combine(dir.Path, "tack.json"), mine = Path.Combine(folder, "keep.txt");
        File.WriteAllText(tack, "{\"version\":1}");
        File.WriteAllText(mine, "mine");

        static Dictionary<string, object?> Entry(string id, string sheet = "sheet0000001", string? file = null, object? created = null, object? width = null) => new()
        {
            ["id"] = id,
            ["sheetId"] = sheet,
            ["file"] = file ?? id + ".png",
            ["created"] = created ?? "2026-10-24T09:05:00.000+01:00",
            ["size"] = 1,
            ["width"] = width ?? 20,
            ["height"] = 20,
        };
        var extra = Entry(Id(8));
        extra["extra"] = new { deep = new[] { 1, 2 } };
        var entries = new List<object?>
        {
            Entry(Id(1)),
            Entry("../../tack", file: "../../tack.json"),
            Entry(Id(2), file: @"..\..\tack.json"),
            Entry(Id(3), sheet: "bad id!"),
            Entry(Id(4), created: "yesterday"),
            Entry(Id(5), width: 0),
            Entry(Id(6), width: "20"),
            Entry(Id(0xabcd).ToUpperInvariant()),
            Entry(Id(1)), // twice
            Entry(Id(99)), // no file
            "just a string",
            42,
            null,
            extra,
        };
        File.WriteAllText(index, JsonSerializer.Serialize(new { version = 1, snippets = entries }));

        var store = new SnippetStore(dir.Path);
        Check.Equal(2, store.Count, "only the two good entries (unknown fields are fine)");
        Check.True(store.Of("sheet0000001").Select(s => s.Id).SequenceEqual([Id(1), Id(8)]), "those two");
        Check.Equal((long)png.Length, store.Of("sheet0000001")[0].Size, "sized by the file itself, not the entry");
        Check.True(File.Exists(tack) && File.ReadAllText(tack) == "{\"version\":1}", "tack.json beside it untouched");
        Check.True(File.Exists(mine), "someone's own file in the folder untouched");
        Check.True(!File.Exists(Path.Combine(folder, Id(2) + ".png")) && File.Exists(Path.Combine(folder, Id(1) + ".png")), "Tack's own files nothing names are cleared away");

        // A thousand entries: fifty read.
        for (int n = 1; n <= 1000; n++) File.WriteAllBytes(Path.Combine(folder, Id(n) + ".png"), png);
        File.WriteAllText(index, JsonSerializer.Serialize(new { version = 1, snippets = Enumerable.Range(1, 1000).Select(n => Entry(Id(n))) }));
        Check.Equal(SnippetStore.MaxCount, new SnippetStore(dir.Path).Count, "a thousand entries: fifty");

        // Too big, too deep, or not JSON: nothing read, no error, and someone's own files stay.
        foreach (var (name, text) in new[]
        {
            ("over 256 KB", "{\"version\":1,\"snippets\":[]," + new string(' ', 300_000) + "}"),
            ("nested 10,000 deep", "{\"version\":1,\"x\":" + new string('[', 10_000) + new string(']', 10_000) + "}"),
            ("not JSON", "snippets!"),
        })
        {
            File.Delete(index + ".bak");
            File.WriteAllText(index, text);
            Check.Equal(0, new SnippetStore(dir.Path).Count, $"{name}: nothing read");
            Check.True(File.Exists(mine) && File.Exists(tack), $"{name}: nothing else touched");
        }

        // A later version's index is left exactly as it is, files and all.
        File.WriteAllBytes(Path.Combine(folder, Id(1) + ".png"), png);
        string later = JsonSerializer.Serialize(new { version = 2, snippets = new[] { Entry(Id(1)) } });
        File.WriteAllText(index, later);
        var newer = new SnippetStore(dir.Path);
        Check.Equal(0, newer.Count, "a later version: not read");
        Check.Equal(SnippetRefusal.CantSave, newer.Add("sheet0000001", new SnippetPng(png, 20, 20, null), SnippetTests.At(9, 0)).Refusal, "nor written over");
        newer.Prune(_ => false);
        Check.True(File.ReadAllText(index) == later && File.Exists(Path.Combine(folder, Id(1) + ".png")), "left exactly as it was");
    }

    private static void UnreadableSnippetIndex()
    {
        Ui.Run(_ =>
        {
            using var dir = new SnippetTests.Temp();
            var store = new SnippetStore(dir.Path);
            var png = new SnippetPng(SnippetTests.Png(SnippetTests.Picture(20, 20)), 20, 20, null);
            var a = store.Add("sheet0000001", png, SnippetTests.At(9, 0)).Snippet!;
            var b = store.Add("sheet0000001", png, SnippetTests.At(9, 1)).Snippet!;
            foreach (var (name, text, bak) in new[]
            {
                ("a top-level array (a later shape, or damage), its .bak too", "[]", "[]"),
                ("cut short, its .bak zeroed", "{ \"version\": 1, \"snippets\": [", "\0\0\0"),
                ("over 256 KB, no .bak", "{\"version\":1,\"snippets\":[]," + new string(' ', 300_000) + "}", null),
            })
            {
                File.WriteAllText(store.IndexPath, text);
                if (bak != null) File.WriteAllText(store.IndexPath + ".bak", bak);
                else File.Delete(store.IndexPath + ".bak");
                var again = new SnippetStore(dir.Path);
                Check.True(again.LeftAlone && again.Count == 0, $"{name}: unreadable, so left alone");
                Check.True(File.Exists(store.PathOf(a)) && File.Exists(store.PathOf(b)), $"{name}: every PNG stays");
                Check.Equal(SnippetRefusal.CantSave, again.Add("sheet0000001", png, SnippetTests.At(9, 2)).Refusal, $"{name}: nothing added over it");
                again.Prune(_ => false);
                Check.True(File.ReadAllText(store.IndexPath) == text && File.Exists(store.PathOf(a)), $"{name}: never written over, nothing cleared");
                var copies = Directory.GetFiles(dir.Path, SnippetStore.IndexName + ".unreadable-*");
                Check.Equal(1, copies.Count(c => File.ReadAllText(c) == text), $"{name}: kept aside");
                Check.True(new SnippetStore(dir.Path).LeftAlone, $"{name}: still left alone at the next start");
                Check.Equal(copies.Length, Directory.GetFiles(dir.Path, SnippetStore.IndexName + ".unreadable-*").Length, $"{name}: once, not at every start");
            }
        });
    }

    private static void SnippetsPastTheCaps()
    {
        Ui.Run(_ =>
        {
            using var dir = new SnippetTests.Temp();
            string folder = Path.Combine(dir.Path, SnippetStore.FolderName), index = Path.Combine(dir.Path, SnippetStore.IndexName);
            Directory.CreateDirectory(folder);
            byte[] png = SnippetTests.Png(SnippetTests.Picture(20, 20));
            static string Id(int n) => n.ToString("x32");
            int over = SnippetStore.MaxCount + 5;
            for (int n = 1; n <= over; n++) File.WriteAllBytes(Path.Combine(folder, Id(n) + ".png"), png);
            File.WriteAllText(index, JsonSerializer.Serialize(new
            {
                version = 1,
                snippets = Enumerable.Range(1, over).Select(n => new { id = Id(n), sheetId = "sheet0000001", file = Id(n) + ".png", created = "2026-10-24T09:05:00.000+01:00", size = 1, width = 20, height = 20 }),
            }));
            var store = new SnippetStore(dir.Path);
            Check.Equal(SnippetStore.MaxCount, store.Count, "fifty loaded");
            Check.Equal(over, Directory.GetFiles(folder, "*.png").Length, "the five past the cap keep their files");
        });
    }

    private static void HostileStoredFile()
    {
        Ui.Run(errors =>
        {
            using var dir = new SnippetTests.Temp();
            var store = new SnippetStore(dir.Path);
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            var png = new SnippetPng(SnippetTests.Png(SnippetTests.Picture(60, 40)), 60, 40, null);
            var a = store.Add(ws.ActiveSheet.Id, png, SnippetTests.At(9, 0)).Snippet!;
            var b = store.Add(ws.ActiveSheet.Id, png, SnippetTests.At(9, 1)).Snippet!;
            var c = store.Add(ws.ActiveSheet.Id, png, SnippetTests.At(9, 2)).Snippet!;
            File.WriteAllBytes(store.PathOf(a), SnippetTests.CraftPng(20000, 20000));
            File.WriteAllBytes(store.PathOf(b), [1, 2, 3]);
            using (var fs = File.Create(store.PathOf(c))) fs.SetLength(SnippetImage.MaxEncodedBytes + 1);

            long before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var s in new[] { a, b, c }) Check.True(SnippetImage.Load(store.PathOf(s)) == null, $"{s.Id}: not shown");
            Check.True(GC.GetAllocatedBytesForCurrentThread() - before < 4 << 20, "without reading the big one or drawing the vast one");

            var host = new SnippetTests.Host();
            var notes = SnippetTests.Notes(ws, store, host);
            using var window = Ui.Host(notes);
            var thumbs = Ui.Descendants<System.Windows.Controls.Button>(notes).Where(t => System.Windows.Automation.AutomationProperties.GetName(t).StartsWith("Snippet ", StringComparison.Ordinal)).ToList();
            Check.Equal(3, thumbs.Count, "each shows as an empty frame");
            Check.True(thumbs.All(t => t.Background is not System.Windows.Media.ImageBrush), "with no image");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    /// <summary>A DIB header (and <paramref name="extra"/> bytes behind it) saying whatever it's told to.</summary>
    private static byte[] SnippetDib(int width, int height, int bits, int extra = 64, int size = 40, short planes = 1, int compression = 0, int colors = 0, uint imageSize = 0)
    {
        var dib = new byte[Math.Max(size, 40) + extra];
        using var w = new BinaryWriter(new MemoryStream(dib));
        w.Write(size);
        w.Write(width);
        w.Write(height);
        w.Write(planes);
        w.Write((short)bits);
        w.Write(compression);
        w.Write(imageSize);
        w.Write(0);
        w.Write(0);
        w.Write(colors);
        w.Write(0);
        return dib;
    }

    private static byte[] SnippetJpeg()
    {
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(SnippetTests.Picture(40, 30)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- M1

    private static void HalfCharacters()
    {
        string lone = Craft("mmmm0001", Now, text: "half \\ud83d an emoji");
        Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(() => DeviceFile.Parse(Encoding.UTF8.GetBytes(lone), "mmmm0001", Now)), "lone surrogate");

        byte[] bad = Encoding.UTF8.GetBytes(Craft("mmmm0001", Now, text: "XX"));
        int at = Encoding.UTF8.GetString(bad).IndexOf("XX", StringComparison.Ordinal);
        bad[at] = 0xC3; // a two-byte sequence missing its second byte
        bad[at + 1] = 0x28;
        Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(() => DeviceFile.Parse(bad, "mmmm0001", Now)), "invalid UTF-8");
    }

    private static void HalfCharactersDontStopSync()
    {
        var folder = new Folder();
        folder.Put("aaaa0001.json", Craft("aaaa0001", Now, text: "fine"));
        folder.Put("mmmm0001.json", Craft("mmmm0001", Now, text: "half \\ud800 a character"));
        var (ws, meta) = Device("desk0001");
        var result = Cycle(ws, folder, meta);
        Check.Equal("fine", string.Join(",", ws.Open.Select(t => t.Text)), "the good file merged");
        Check.True(result.Problems.Contains(("mmmm0001", DeviceFileProblem.Unreadable)), "the bad one set aside");
        Check.True(result.Wrote && folder.Files.ContainsKey("desk0001.json"), "and the PC wrote its own file");
        int reads = folder.Reads.GetValueOrDefault("mmmm0001.json");
        Cycle(ws, folder, meta);
        Check.Equal(reads, folder.Reads.GetValueOrDefault("mmmm0001.json"), "not read again until it changes");
    }

    private static void HalfCharactersInImport()
    {
        var (ws, _) = Device("desk0001");
        string file = "{\"version\":1,\"tasks\":[],\"sheets\":[],\"tackFocus\":{\"format\":1,\"tasks\":[{\"id\":\"t1\",\"text\":\"a\\ud800b\",\"created\":\"2026-10-21T09:00:00Z\",\"completed\":null,\"order\":\"V\",\"deleted\":false,"
            + $"\"clk\":{{\"text\":\"{Stamp.Encode(Now, 0, "x")}\",\"completed\":\"{Stamp.Zero}\",\"order\":\"{Stamp.Zero}\",\"deleted\":\"{Stamp.Zero}\"}}}}],\"sheets\":[]}}}}";
        var (message, undo) = FileImport.Run(ws, Encoding.UTF8.GetBytes(file), null);
        Check.Equal("This file has text Tack can't read.", message, "the import says so");
        Check.True(undo == null && ws.Open.Count == 0, "and changes nothing");
    }

    private static void HalfCharactersAtStartup()
    {
        string dir = Directory.CreateTempSubdirectory("tack-hostile-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "tack.json"),
                "{\"version\":1,\"tasks\":[{\"id\":\"a1\",\"text\":\"kept\",\"created\":\"2026-10-21T09:00:00+03:00\"}],\"sheets\":[],"
                + "\"tackFocus\":{\"format\":1,\"tasks\":[{\"id\":\"a1\",\"text\":\"x\\ud800\",\"created\":\"2026-10-21T09:00:00+03:00\",\"completed\":null,\"order\":\"V\",\"deleted\":false,"
                + $"\"clk\":{{\"text\":\"{Stamp.Encode(Now, 0, "desk")}\",\"completed\":\"{Stamp.Zero}\",\"order\":\"{Stamp.Zero}\",\"deleted\":\"{Stamp.Zero}\"}}}}],\"sheets\":[]}}}}");
            var doc = new StateStore(dir).Load();
            var data = StateFile.ToData(doc, new Hlc("desk"), Now, Now);
            Check.Equal("kept", string.Join(",", data.Tasks.Where(t => !t.Deleted).Select(t => t.Text)), "the top level is used");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------- M2

    private static void BuiltInNames()
    {
        var folder = new Folder();
        string seen = $"\"seen\":{{\"toString\":\"{Stamp.Encode(Now, 0, "toString")}\"}}";
        folder.Put("constructor.json", Craft("constructor", Now, text: "from constructor", extra: seen));
        var (ws, meta) = Device("desk0001");
        Cycle(ws, folder, meta);
        Check.Equal("from constructor", string.Join(",", ws.Open.Select(t => t.Text)), "merged");
        var written = DeviceFile.Parse(folder.Files["desk0001.json"].Bytes, "desk0001", Now);
        Check.True(written.Seen.ContainsKey("constructor") && written.Seen.ContainsKey("toString"), "and passed on as ordinary ids");
        Check.True(!StampSeen(Stamp.Encode(Now, 0, "constructor"), "other", new Dictionary<string, string>()), "unseen until seen");
    }

    private static bool StampSeen(string stamp, string device, Dictionary<string, string> seen) => SyncEngine.SeenBy(stamp, device, seen);

    // ---------------------------------------------------------------- M3

    private static void Vouching()
    {
        var folder = new Folder();
        long now = Now;
        folder.Put("phon0001.json", Craft("phon0001", now, text: "first"));
        // Mallory claims to have seen the phone's writes ~300 days from now, and to have written then.
        long later = now + 300 * Day;
        folder.Put("mall0001.json", Craft("mall0001", later, text: "from mallory", extra: $"\"seen\":{{\"phon0001\":\"{Stamp.Encode(later, 5, "phon0001")}\"}}"));
        var (ws, meta) = Device("desk0001", () => now);
        Cycle(ws, folder, meta, now);

        now += 1000;
        folder.Put("phon0001.json", Craft("phon0001", now, text: "second"));
        Cycle(ws, folder, meta, now);
        Check.Equal("second", ws.Open.Select(t => t.Text).Single(t => t == "second"), "the phone's next change still arrives");

        folder.Files.Remove("mall0001.json");
        now += 1000;
        folder.Put("phon0001.json", Craft("phon0001", now, text: "third"));
        Cycle(ws, folder, meta, now);
        Check.True(ws.Open.Any(t => t.Text == "third"), "and after Mallory's file is gone");

        // A claim is believed no further than the claimant's own clock.
        var folder2 = new Folder();
        folder2.Put("phon0001.json", Craft("phon0001", Now, text: "x"));
        folder2.Put("mall0001.json", Craft("mall0001", Now, text: "y", extra: $"\"seen\":{{\"phon0001\":\"{Stamp.Encode(Now + 300 * Day, 0, "phon0001")}\"}}"));
        var (ws2, meta2) = Device("desk0002");
        Cycle(ws2, folder2, meta2);
        Check.True(Stamp.Time(meta2.Seen["phon0001"]) <= Now, "the claim is cut back to Mallory's own clock");
    }

    // ---------------------------------------------------------------- M4

    private static void Limits()
    {
        string tooMany = Craft("mmmm0001", Now, text: "x", tasksJson: "[" + string.Join(",", Enumerable.Repeat("{}", DeviceFile.MaxRecords + 1)) + "]");
        Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(() => DeviceFile.Parse(Encoding.UTF8.GetBytes(tooMany), "mmmm0001", Now)), "too many records");
        string atLimit = Craft("mmmm0001", Now, text: "x", tasksJson: "[" + string.Join(",", Enumerable.Repeat("{}", DeviceFile.MaxRecords)) + "]");
        DeviceFile.Parse(Encoding.UTF8.GetBytes(atLimit), "mmmm0001", Now); // fine at the limit

        // More "seen" claims than any device writes (a file passing on a crafted one's, before that
        // was trimmed): the file is still read, marked so the engine counts only the claims that matter.
        string seen = "\"seen\":{" + string.Join(",", Enumerable.Range(0, DeviceFile.MaxSeen + 1).Select(i => $"\"dev{i:D5}\":\"{Stamp.Encode(Now, 0, "x")}\"")) + "}";
        var relayed = DeviceFile.Parse(Encoding.UTF8.GetBytes(Craft("mmmm0001", Now, text: "x", extra: seen)), "mmmm0001", Now);
        Check.True(relayed.SeenOver, "too many devices seen: marked");
        Check.Equal(DeviceFile.MaxSeen + 1, relayed.Seen.Count, "with its claims, for the engine to sort out");
        Check.True(!DeviceFile.Parse(Encoding.UTF8.GetBytes(Craft("mmmm0001", Now, text: "x")), "mmmm0001", Now).SeenOver, "a usual file isn't marked");
        Check.Equal("x", string.Join(",", relayed.Tasks.Select(t => t.Text)), "and the records still read");

        string longText = Craft("mmmm0001", Now, text: new string('x', DeviceFile.MaxTaskText + 1));
        Check.Equal(0, DeviceFile.Parse(Encoding.UTF8.GetBytes(longText), "mmmm0001", Now).Tasks.Count, "an over-long task is skipped");
    }

    private static void Fields()
    {
        Check.True(Stamp.IsStamp(Stamp.Encode(Now, 0, new string('x', 32))), "a 32-character device part");
        Check.True(!Stamp.IsStamp(Stamp.Encode(Now, 0, new string('x', 33))), "no 33-character one");
        Check.True(OrderKey.IsOrderKey(new string('V', OrderKey.MaxLength)) && !OrderKey.IsOrderKey(new string('V', OrderKey.MaxLength + 1)), "order keys of a sane length");

        var odd = Record(created: "!", completed: null);
        Check.True(odd.Created != "!" && TackFile.IsFileDate(odd.Created), "an odd created date counts as missing");
        Check.True(Record(created: new string('x', 100_000), completed: null).Created.Length < 40, "and a huge one");
        Check.Equal("2026-10-21T09:00:00.1234567+03:00", Record(created: "2026-10-21T09:00:00.1234567+03:00", completed: null).Created, "real dates are kept, .NET's seven digits too");
        Check.True(!TackFile.IsFileDate("2026-10-21T09:00:00.000+03:00 "), "nothing after the date");
    }

    private static void TooLargeToWrite()
    {
        var folder = new Folder();
        var (ws, meta) = Device("desk0001");
        ws.AddSheet().Text = new string('a', DeviceFile.MaxBytes + 1000);
        SyncErrorKind? kind = null;
        try
        {
            Cycle(ws, folder, meta);
        }
        catch (SyncException ex)
        {
            kind = ex.Kind;
        }
        Check.Equal<SyncErrorKind?>(SyncErrorKind.TooLarge, kind, "refused as too large");
        Check.True(!folder.Files.ContainsKey("desk0001.json") && meta.Own.Pending == null, "nothing written or pending");
    }

    // ---------------------------------------------------------------- L1

    private static void NeverReadsTooBig()
    {
        var folder = new Folder();
        folder.Put("bigbig01.json", "x", size: DeviceFile.MaxBytes + 1L);
        var (ws, meta) = Device("desk0001");
        var result = Cycle(ws, folder, meta);
        Check.True(result.Problems.Contains(("bigbig01", DeviceFileProblem.Unreadable)), "set aside");
        Check.Equal(0, folder.Reads.GetValueOrDefault("bigbig01.json"), "without being read");
    }

    private static void OneFailedRead()
    {
        var folder = new Folder();
        folder.Put("aaaa0001.json", Craft("aaaa0001", Now, text: "from ann"));
        folder.Put("bbbb0001.json", Craft("bbbb0001", Now, text: "from bob"));
        folder.FailNextRead = SyncErrorKind.Offline; // the OneDrive app holds the first file
        var (ws, meta) = Device("desk0001");
        var first = Cycle(ws, folder, meta);
        Check.True(first.Merged.Count == 1 && first.Wrote, "the other file merged and the PC wrote its own");
        var second = Cycle(ws, folder, meta);
        Check.Equal("from ann,from bob", string.Join(",", ws.Open.Select(t => t.Text).Order()), "the failed one comes next time");
        Check.Equal(2, first.Merged.Count + second.Merged.Count, "each merged once");
    }

    // ---------------------------------------------------------------- L7, L8

    private static void EmptyCompleted()
    {
        Check.Equal<string?>(null, Record(created: "2026-10-21T09:00:00Z", completed: "").Completed, "empty");
        Check.Equal<string?>(null, Record(created: "2026-10-21T09:00:00Z", completed: "not a date").Completed, "not a date");
        Check.Equal<string?>("2026-10-21T10:00:00Z", Record(created: "2026-10-21T09:00:00Z", completed: "2026-10-21T10:00:00Z").Completed, "a date");
    }

    private static void ClockWindow()
    {
        var h = new Hlc("aaaa1111", () => Now);
        h.Observe(Stamp.Encode(Now + Stamp.MaxAheadMs + 1, 0, "bbbb2222"));
        Check.Equal((double)Now, Stamp.Time(h.Now()), "not adopted");
        h.Observe(Stamp.Encode(Now + 30 * Day, 0, "bbbb2222"));
        Check.Equal((double)(Now + 30 * Day), Stamp.Time(h.Now()), "nearer stamps are");
    }

    // ---------------------------------------------------------------- second audit (24 October 2026)
    // Limits on what a device writes and takes in, so that no file within every reader's limits, alone
    // or with others, can make an honest device write a file its peers refuse (as synchostile.test.ts).

    private static void SeenFlood()
    {
        var folder = new Folder();
        folder.Put("phon0001.json", Craft("phon0001", Now, text: "before"));
        var (desk, deskMeta) = Device("desk0001");
        var (pc, pcMeta) = Device("pcpc0001");
        Cycle(desk, folder, deskMeta);
        Cycle(pc, folder, pcMeta);
        // Within every limit: exactly MaxSeen made-up devices.
        string claims = "\"seen\":{" + string.Join(",", Enumerable.Range(0, DeviceFile.MaxSeen).Select(i => $"\"zz{i:D6}\":\"{Stamp.Encode(Now - 5000, 0, "q")}\"")) + "}";
        folder.Put("evil0001.json", Craft("evil0001", Now, text: "from evil", extra: claims));
        Cycle(desk, folder, deskMeta);
        Cycle(pc, folder, pcMeta);
        foreach (string id in new[] { "desk0001", "pcpc0001" })
        {
            var own = DeviceFile.Parse(folder.Files[$"{id}.json"].Bytes, id, Now); // every device takes it
            Check.True(own.Seen.Count <= DeviceFile.MaxSeen, $"{id} passes on no more than any reader takes");
        }

        folder.Files.Remove("evil0001.json");
        folder.Put("phon0001.json", Craft("phon0001", Now + 1000, text: "after"));
        Cycle(desk, folder, deskMeta, Now + 1000);
        var result = Cycle(pc, folder, pcMeta, Now + 1000);
        Cycle(desk, folder, deskMeta, Now + 2000);
        Check.True(pc.Open.Any(t => t.Text == "after"), "the phone's next change still arrives");
        Check.Equal(0, result.Problems.Count, "and no device's file is set aside");
        Check.Equal(0, Cycle(desk, folder, deskMeta, Now + 2000).Problems.Count, "on either side");
    }

    private static void TrimsSeen()
    {
        var seen = Enumerable.Range(0, DeviceFile.MaxSeen + 5).ToDictionary(i => $"zz{i:D6}", i => Stamp.Encode(Now + i, 0, "q"));
        seen["real0001"] = Stamp.Encode(Now - 1000, 0, "real0001"); // older than every made-up claim, but its file is there
        DeviceFile.TrimSeen(seen, "mine0001", new HashSet<string> { DeviceFile.FileName("real0001") });
        Check.Equal(DeviceFile.MaxSeen, seen.Count, "trimmed to what every reader takes");
        Check.True(seen.ContainsKey("real0001"), "a device in the folder stays");
        Check.True(Enumerable.Range(0, 6).All(i => !seen.ContainsKey($"zz{i:D6}")), "the oldest made-up claims went");
        var few = new Dictionary<string, string> { ["aaaa0001"] = Stamp.Encode(Now, 0, "aaaa0001") };
        DeviceFile.TrimSeen(few, "mine0001", new HashSet<string>());
        Check.Equal(1, few.Count, "under the limit nothing changes");
    }

    /// <summary>A crafted file within every limit: one task and <paramref name="deletions"/> made-up deletions.</summary>
    private static string Deletions(string device, int deletions, string prefix = "d")
    {
        string at = Stamp.Encode(Now - 5000, 0, device);
        string map = "{" + string.Join(",", Enumerable.Range(0, deletions).Select(i => $"\"{prefix}{i}\":\"{at}\"")) + "}";
        return Craft(device, Now, text: $"from {device}").Replace("\"deleted\":{\"tasks\":{}", $"\"deleted\":{{\"tasks\":{map}", StringComparison.Ordinal);
    }

    private static void RecordsBudget()
    {
        var folder = new Folder();
        folder.Put("phon0001.json", Craft("phon0001", Now, text: "before"));
        string evil = Deletions("evil0002", DeviceFile.MaxRecords - 1);
        DeviceFile.Parse(Encoding.UTF8.GetBytes(evil), "evil0002", Now); // within every limit
        folder.Put("evil0002.json", evil);
        var (ws, meta) = Device("desk0001");
        var result = Cycle(ws, folder, meta);
        Check.True(result.Problems.Contains(("evil0002", DeviceFileProblem.Full)), "set aside");
        Check.True(ws.RecordCount < 10, "and not taken in");
        DeviceFile.Parse(folder.Files["desk0001.json"].Bytes, "desk0001", Now); // every device takes this PC's file
        Check.Equal("before", string.Join(",", ws.Open.Select(t => t.Text)), "the rest still syncs");
    }

    private static void RecordsBudgetTogether()
    {
        var folder = new Folder();
        int half = DeviceFile.MaxTaken / 2;
        folder.Put("aaaa0001.json", Deletions("aaaa0001", half, "a"));
        folder.Put("bbbb0001.json", Deletions("bbbb0001", half, "b"));
        var (ws, meta) = Device("desk0001");
        var result = Cycle(ws, folder, meta);
        Check.Equal("aaaa0001", string.Join(",", result.Merged), "the first fits");
        Check.True(result.Problems.Contains(("bbbb0001", DeviceFileProblem.Full)), "the second would take this PC past what it takes in");
        DeviceFile.Parse(folder.Files["desk0001.json"].Bytes, "desk0001", Now);
        var again = Cycle(ws, folder, meta);
        Check.True(again.Problems.Contains(("bbbb0001", DeviceFileProblem.Full)), "still set aside");
        Check.Equal(1, folder.Reads.GetValueOrDefault("bbbb0001.json"), "and not read again until it changes");
    }

    private static void TooLargeEveryCycle()
    {
        var folder = new Folder();
        var (ws, meta) = Device("desk0001");
        // Holding more than any file may carry (merged before this was checked, say).
        string at = Stamp.Encode(Now - 5000, 0, "evil0003");
        ws.MergeFrom(new WorkspaceData(Enumerable.Range(0, DeviceFile.MaxRecords + 1).Select(i => DeviceFile.DeadTask($"d{i}", at)).ToList(), [], FocusData.None, []));
        SyncErrorKind? Kind()
        {
            try
            {
                Cycle(ws, folder, meta);
                return null;
            }
            catch (SyncException ex)
            {
                return ex.Kind;
            }
        }
        Check.Equal<SyncErrorKind?>(SyncErrorKind.TooLarge, Kind(), "refused as too large");
        Check.True(!folder.Files.ContainsKey("desk0001.json"), "nothing written");
        Check.Equal<SyncErrorKind?>(SyncErrorKind.TooLarge, Kind(), "and said again on the next cycle, which finds nothing new");
        Check.True(meta.Own.Pending == null, "nothing pending");
    }

    private static void BoundedRead()
    {
        string dir = Directory.CreateTempSubdirectory("tack-hostile-").FullName;
        try
        {
            var local = new LocalSyncFolder(dir);
            local.Prepare();
            string path = Path.Combine(local.Devices, "bigbig01.json");
            File.WriteAllText(path, "x");
            var entry = local.ListAsync().GetAwaiter().GetResult().Single(); // listed small...
            using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(DeviceFile.MaxBytes + 1L); // ...grown since
            Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(() => local.ReadAsync(entry).GetAwaiter().GetResult()), "refused unread");
            File.WriteAllText(path, "{}");
            Check.Equal("{}", Encoding.UTF8.GetString(local.ReadAsync(entry).GetAwaiter().GetResult()), "an ordinary file reads as before");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string DesktopFile(IEnumerable<string> tasks) => "{\"version\":1,\"tasks\":[" + string.Join(",", tasks) + "],\"sheets\":[]}";

    private static string DesktopTask(string id, string text, string created = "2026-10-20T09:00:00Z", string? completed = null) =>
        $"{{\"id\":\"{id}\",\"text\":{JsonSerializer.Serialize(text)},\"created\":{JsonSerializer.Serialize(created)}"
        + (completed != null ? $",\"completed\":{JsonSerializer.Serialize(completed)}" : "") + ",\"now\":false}";

    private static void ImportLimits()
    {
        var (ws, _) = Device("desk0001");
        // Holding nearly all a device takes in (deletions, say: Undo after a huge import leaves those).
        string at = Stamp.Encode(Now - 5000, 0, "desk0001");
        ws.MergeFrom(new WorkspaceData(Enumerable.Range(0, DeviceFile.MaxTaken - 2 - ws.RecordCount).Select(i => DeviceFile.DeadTask($"d{i}", at)).ToList(), [], FocusData.None, []));
        byte[] fits = Encoding.UTF8.GetBytes(DesktopFile([DesktopTask("i0", "one"), DesktopTask("i1", "two")]));
        var (message, _) = FileImport.Run(ws, fits, null);
        Check.Equal("Imported 2 tasks", message, "up to the limit is fine");
        Check.Equal(DeviceFile.MaxTaken, ws.RecordCount, "and taken in");
        Check.Equal("Nothing new in that file", FileImport.Run(ws, fits, null).Message, "the same again adds nothing, so it's fine too");
        var (refused, undo) = FileImport.Run(ws, Encoding.UTF8.GetBytes(DesktopFile([DesktopTask("i2", "one more")])), null);
        Check.Equal("This file holds more tasks and notes than Tack can keep in sync.", refused, "one more is refused");
        Check.True(undo == null && ws.RecordCount == DeviceFile.MaxTaken, "and nothing changes");
        var (huge, _) = FileImport.Run(Device("desk0002").Ws, Encoding.UTF8.GetBytes(DesktopFile(Enumerable.Repeat("{}", DeviceFile.MaxRecords + 1))), null);
        Check.Equal("This file holds more tasks and notes than Tack can keep in sync.", huge, "a file with more records than any device may hold");
    }

    private static void ImportLongText()
    {
        var (ws, _) = Device("desk0001");
        string file = "{\"version\":1,\"tasks\":[" + DesktopTask("a1", new string('x', DeviceFile.MaxTaskText + 1)) + "," + DesktopTask("a2", "kept") + "],"
            + $"\"sheets\":[{{\"id\":\"s1\",\"text\":\"{new string('n', DeviceFile.MaxNoteText + 1)}\",\"updated\":\"2026-10-20T09:00:00Z\"}}]}}";
        FileImport.Run(ws, Encoding.UTF8.GetBytes(file), null);
        Check.Equal("kept", string.Join(",", ws.Open.Select(t => t.Text)), "an over-long task is skipped");
        Check.True(ws.Sheets.All(s => s.Id != "s1"), "and an over-long note");

        string at = Stamp.Encode(Now, 0, "bbbb2222");
        string Record(string id, string text) => $"{{\"id\":\"{id}\",\"text\":\"{text}\",\"created\":\"2026-10-20T09:00:00Z\",\"completed\":null,\"order\":\"V\",\"deleted\":false,"
            + $"\"clk\":{{\"text\":\"{at}\",\"completed\":\"{at}\",\"order\":\"{at}\",\"deleted\":\"{Stamp.Zero}\"}}}}";
        var (ws2, _) = Device("desk0002");
        string withClocks = "{\"version\":1,\"tasks\":[],\"sheets\":[],\"tackFocus\":{\"format\":1,\"focus\":{\"id\":null,\"clk\":\"" + Stamp.Zero + "\"},\"tasks\":["
            + Record("b1", new string('x', DeviceFile.MaxTaskText + 1)) + "," + Record("b2", "kept") + "],\"sheets\":[]}}";
        FileImport.Run(ws2, Encoding.UTF8.GetBytes(withClocks), null);
        Check.Equal("kept", string.Join(",", ws2.Open.Select(t => t.Text)), "in files with clocks too");
    }

    private static void ImportDates()
    {
        var folder = new Folder();
        var (desk, deskMeta) = Device("desk0001");
        var (pc, pcMeta) = Device("pcpc0001");
        string file = "{\"version\":1,\"tasks\":[" + DesktopTask("a1", "done one", created: " 2026-10-20T09:00:00Z", completed: "2026-10-21") + "],"
            + "\"sheets\":[{\"id\":\"s1\",\"text\":\"note\",\"updated\":\"2026-10-21 10:00\"}]}";
        FileImport.Run(desk, Encoding.UTF8.GetBytes(file), null);
        var t = desk.ToData().Tasks.Single(x => x.Id == "a1");
        Check.True(TackFile.IsFileDate(t.Created) && TackFile.IsFileDate(t.Completed), $"dates every device reads ({t.Created}, {t.Completed})");
        Check.True(TackFile.IsFileDate(desk.ToData().Sheets.Single(s => s.Id == "s1").Updated), "the note's too");
        desk.AddTask("nudge"); // an import of a file without clocks is no news on its own
        Cycle(desk, folder, deskMeta);
        Cycle(pc, folder, pcMeta);
        Cycle(desk, folder, deskMeta);
        Check.Equal("done one", string.Join(",", desk.Done.Select(x => x.Text)), "still done here");
        Check.Equal("done one", string.Join(",", pc.Done.Select(x => x.Text)), "and done on the other device");
        Check.Equal(t.Created, pc.ToData().Tasks.Single(x => x.Id == "a1").Created, "created the same everywhere");

        var (ws, _) = Device("desk0002");
        FileImport.Run(ws, Encoding.UTF8.GetBytes(DesktopFile([DesktopTask("a1", "x", created: "2026-10-20T09:00:00.1234567+01:00", completed: "2026-10-21T10:00:00+01:00")])), null);
        var kept = ws.ToData().Tasks.Single();
        Check.Equal("2026-10-20T09:00:00.1234567+01:00", kept.Created, "dates already as every device reads them are kept as written");
        Check.Equal<string?>("2026-10-21T10:00:00+01:00", kept.Completed, "completed too");
    }

    // ---------------------------------------------------------------- regression review (24 October 2026)
    // What the second audit's limits cost honest devices (as synchostile.test.ts).

    /// <summary>Ignoring every claim of such a file made a device passing them on look as if it had seen nothing.</summary>
    private static void SeenOverClaims()
    {
        var folder = new Folder();
        var (phone, phoneMeta) = Device("phon0001");
        var (desk, deskMeta) = Device("desk0001");
        var note = phone.AddSheet(text: "Plan: v1");
        Cycle(phone, folder, phoneMeta);
        Cycle(desk, folder, deskMeta);
        phone.SheetById(note.Id)!.Text = "Plan: v2 (phone)";
        Cycle(phone, folder, phoneMeta);
        Cycle(desk, folder, deskMeta);
        desk.SheetById(note.Id)!.Text = "Plan: v3 (desk, after seeing v2)";
        Cycle(desk, folder, deskMeta);
        // The desk passes on a crafted file's thousand claims, as versions before TrimSeen do.
        string claims = string.Concat(Enumerable.Range(0, DeviceFile.MaxSeen).Select(i => $"\"zz{i:D6}\":\"{Stamp.Encode(Now - 5000, 0, "q")}\","));
        string text = Encoding.UTF8.GetString(folder.Files["desk0001.json"].Bytes);
        folder.Put("desk0001.json", text.Replace("\"seen\":{", "\"seen\":{" + claims, StringComparison.Ordinal).Replace(",}", "}", StringComparison.Ordinal));
        Cycle(phone, folder, phoneMeta);
        Check.Equal("Plan: v3 (desk, after seeing v2)", string.Join(" | ", phone.Sheets.Select(s => s.Text).Where(t => t.StartsWith("Plan", StringComparison.Ordinal))),
            "a note edited in turn is kept once");
        Check.Equal("desk0001", string.Join(",", DeviceFile.Parse(folder.Files["phon0001.json"].Bytes, "phon0001", Now).Seen.Keys), "and the made-up claims go no further");
    }

    /// <summary>A budget that honest devices fill with each other's edits stops them.</summary>
    private static void FloodTrickle()
    {
        var folder = new Folder();
        var (desk, deskMeta) = Device("desk0001");
        var (pc, pcMeta) = Device("pcpc0001");
        for (int i = 0; i < 20; i++) desk.AddTask($"shared {i}");
        var devices = new[] { (Name: "desk", Ws: desk, Meta: deskMeta), (Name: "pc", Ws: pc, Meta: pcMeta) };
        var problems = new List<(string, DeviceFileProblem)>();
        void Settle()
        {
            for (int i = 0; i < 2; i++)
                foreach (var d in devices) problems.AddRange(Cycle(d.Ws, folder, d.Meta).Problems);
        }
        Settle();
        // Within every limit, and within what a device takes in at once: MaxTaken less what's held, less 3.
        folder.Put("evil0004.json", Deletions("evil0004", DeviceFile.MaxTaken - desk.RecordCount - 4));
        foreach (var d in devices) problems.AddRange(Cycle(d.Ws, folder, d.Meta).Problems);
        Check.Equal(DeviceFile.MaxTaken - 3, desk.RecordCount, "the flood is taken in");
        folder.Files.Remove("evil0004.json");
        for (int round = 0; round < 2; round++)
        {
            foreach (var d in devices)
            {
                d.Ws.AddTask($"{d.Name} {round}a");
                d.Ws.Complete(d.Ws.AddTask($"{d.Name} {round}b"));
            }
            Settle();
        }
        Check.Equal(0, problems.Count, "no file is set aside");
        Check.Equal("desk 0a,desk 1a,from evil0004,pc 0a,pc 1a",
            string.Join(",", pc.Open.Select(t => t.Text).Where(t => !t.StartsWith("shared", StringComparison.Ordinal)).Order(StringComparer.Ordinal)), "every new task arrives");
        Check.Equal("desk 0b,desk 1b,pc 0b,pc 1b", string.Join(",", pc.Done.Select(t => t.Text).Order(StringComparer.Ordinal)), "and every completion");
        Check.Equal(desk.RecordCount, pc.RecordCount, "both hold the same");
        foreach (var d in devices) DeviceFile.Parse(folder.Files[$"{d.Ws.Clock.Node}.json"].Bytes, d.Ws.Clock.Node, Now); // every device takes both files
    }

    private static void BulkHeld()
    {
        var folder = new Folder();
        var (ws, meta) = Device("desk0001");
        folder.Put("aaaa0001.json", Deletions("aaaa0001", DeviceFile.MaxTaken - DeviceFile.MaxTrickle - 10, "a"));
        Check.Equal(0, Cycle(ws, folder, meta).Problems.Count, "taken in");
        // More than a few at once past MaxTaken: held.
        folder.Put("bbbb0001.json", Deletions("bbbb0001", DeviceFile.MaxTrickle + 20, "b"));
        Check.True(Cycle(ws, folder, meta).Problems.SequenceEqual([("bbbb0001", DeviceFileProblem.Full)]), "a bulk file past what this PC takes in at once is set aside");
        // A few at a time go in, up to the most a file may carry, and no further.
        int room = DeviceFile.MaxRecords - ws.RecordCount;
        for (int i = 0; i * DeviceFile.MaxTrickle < room + DeviceFile.MaxTrickle; i++)
            folder.Put($"cccc{i:D4}.json", Deletions($"cccc{i:D4}", DeviceFile.MaxTrickle - 1, $"c{i}x"));
        var result = Cycle(ws, folder, meta);
        Check.True(result.Problems.Count > 0 && result.Problems.All(p => p.Problem == DeviceFileProblem.Full), "the rest are set aside as too much to take in");
        Check.True(ws.RecordCount <= DeviceFile.MaxRecords, "never more than a file may carry");
        DeviceFile.Parse(folder.Files["desk0001.json"].Bytes, "desk0001", Now); // every device takes this PC's file
    }

    /// <summary>A crafted file: one task, <paramref name="deleted"/> deleted spaces, and <paramref name="live"/> ones (JSON).</summary>
    private static string Spaces(string device, int deleted = 0, string? live = null)
    {
        string at = Stamp.Encode(Now - 5000, 0, device);
        string craft = Craft(device, Now, text: $"from {device}", extra: live == null ? null : $"\"seen\":{{}},\"spaces\":{live}");
        string map = "{" + string.Join(",", Enumerable.Range(0, deleted).Select(i => $"\"s{i}\":\"{at}\"")) + "}";
        return craft.Replace("\"sheets\":{}}}", $"\"sheets\":{{}},\"spaces\":{map}}}}}", StringComparison.Ordinal);
    }

    /// <summary><paramref name="n"/> live spaces as JSON, their deletion clock <paramref name="deleted"/> (later than a deletion: brought back).</summary>
    private static string LiveSpaces(int n, string deleted)
    {
        string at = Stamp.Encode(Now - 5000, 0, "evil0006");
        return "[" + string.Join(",", Enumerable.Range(0, n).Select(i =>
            $"{{\"id\":\"s{i}\",\"name\":\"junk {i}\",\"order\":\"V\",\"deleted\":false,\"clk\":{{\"name\":\"{at}\",\"order\":\"{at}\",\"deleted\":\"{deleted}\"}}}}")) + "]";
    }

    private static void SpacesLiveOnly()
    {
        var folder = new Folder();
        var (phone, phoneMeta) = Device("phon0001");
        var (pc, pcMeta) = Device("pcpc0001");
        phone.AddTask("before");
        Cycle(phone, folder, phoneMeta);
        Cycle(pc, folder, pcMeta);
        // Within every limit (31 KB): 995 deleted spaces. Every device takes them in.
        folder.Put("evil0005.json", Spaces("evil0005", deleted: 995));
        Cycle(phone, folder, phoneMeta);
        Cycle(pc, folder, pcMeta);
        Check.Equal(995, pc.ToData().Spaces.Count, "taken in");
        folder.Files.Remove("evil0005.json");
        for (int i = 0; i < 6; i++) phone.AddSpace($"space {i}");
        phone.AddTask("after");
        Cycle(phone, folder, phoneMeta);
        var result = Cycle(pc, folder, pcMeta);
        Check.Equal(0, result.Problems.Count, "the phone's file isn't set aside");
        Check.Equal("after,before,from evil0005", string.Join(",", pc.Open.Select(t => t.Text).Order(StringComparer.Ordinal)), "its changes arrive");
        Check.Equal(6, pc.ToData().Spaces.Count(s => !s.Deleted), "and its spaces");
    }

    private static void SpacesTakenAsRecords()
    {
        var folder = new Folder();
        var (ws, meta) = Device("desk0001");
        int n = SpaceView.MaxListed + 1;
        folder.Put("evil0006.json", Spaces("evil0006", deleted: n));
        Check.Equal(0, Cycle(ws, folder, meta).Problems.Count, "deleted ones are taken in");
        Check.Equal(n, ws.ToData().Spaces.Count, "all of them");
        folder.Put("evil0007.json", Spaces("evil0007", live: LiveSpaces(n, Stamp.Encode(Now - 1000, 0, "evil0007"))));
        Check.Equal(0, Cycle(ws, folder, meta).Problems.Count, "and brought back");
        Check.Equal(n, ws.SpaceList.Count, "every one shows");
        DeviceFile.Parse(folder.Files["desk0001.json"].Bytes, "desk0001", Now); // every device takes this PC's file
    }

    // ---------------------------------------------------------------- final pass

    // A limit on how many spaces show, applied as files come in, let one file of junk spaces bring
    // honest devices to the limit, after which the next honest change that showed one more space had
    // the acting device's whole file set aside by every peer, for good.

    private static string Work(Workspace ws) => ws.SpaceList.First(s => s.Name == "Work").Id;

    /// <summary>A folder writer's file of live spaces bringing <paramref name="ws"/> to exactly a thousand.</summary>
    private static string JunkSpaces(Workspace ws)
    {
        string at = Stamp.Encode(Now - 5000, 0, "evil0001");
        int n = 1000 - ws.SpaceList.Count;
        string live = "[" + string.Join(",", Enumerable.Range(0, n).Select(i =>
            $"{{\"id\":\"junk{i}\",\"name\":\"junk {i}\",\"order\":\"W\",\"deleted\":false,\"clk\":{{\"name\":\"{at}\",\"order\":\"{at}\",\"deleted\":\"{Stamp.Zero}\"}}}}")) + "]";
        return Craft("evil0001", Now, text: "from evil0001", extra: $"\"seen\":{{}},\"spaces\":{live}");
    }

    /// <summary>Two PCs sharing two spaces; <paramref name="before"/> (on the second) synced everywhere; the file; then <paramref name="act"/>.</summary>
    private static void SpacesAtTheCap(Action<Workspace> act, Action<Workspace>? before = null)
    {
        var folder = new Folder();
        var (phone, phoneMeta) = Device("phon0001");
        var (pc, pcMeta) = Device("pcpc0001");
        var problems = new List<(string, DeviceFileProblem)>();
        void Settle(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                problems.AddRange(Cycle(phone, folder, phoneMeta).Problems);
                problems.AddRange(Cycle(pc, folder, pcMeta).Problems);
            }
        }
        phone.AddTask("base");
        phone.AddSpace("Home");
        phone.AddSpace("Work");
        Settle(2);
        if (before != null)
        {
            before(pc);
            Settle(2);
        }
        folder.Put("evil0001.json", JunkSpaces(phone));
        Settle(1);
        Check.Equal(1000, phone.SpaceList.Count, "a thousand spaces show");
        act(pc);
        pc.AddTask("call mom");
        Settle(2);
        Check.Equal(0, problems.Count, "no file is set aside");
        Check.True(phone.Open.Any(t => t.Text == "call mom"), "the PC's changes arrive");
        Check.Data(Sorted(pc.ToData()), Sorted(phone.ToData()), "both hold the same");
    }

    private static void SpacesAtTheCapUndo()
    {
        Action undo = () => { };
        SpacesAtTheCap(_ => undo(), ws => undo = ws.DeleteSpace(Work(ws)));
    }

    private static void SpacesSplitNeitherWay()
    {
        var folder = new Folder();
        var (phone, phoneMeta) = Device("phon0001");
        var (pc, pcMeta) = Device("pcpc0001");
        var problems = new List<(string, DeviceFileProblem)>();
        void Round()
        {
            problems.AddRange(Cycle(phone, folder, phoneMeta).Problems);
            problems.AddRange(Cycle(pc, folder, pcMeta).Problems);
        }
        phone.AddTask("base");
        phone.AddSpace("Home");
        Round();
        Round();
        phone.AddTask("milk", space: phone.AddSpace("Groceries").Id);
        Cycle(phone, folder, phoneMeta);
        // Sized for the PC, which hasn't seen Groceries yet: one too many for the phone.
        folder.Put("evil0001.json", JunkSpaces(pc));
        problems.AddRange(Cycle(pc, folder, pcMeta).Problems);
        problems.AddRange(Cycle(phone, folder, phoneMeta).Problems);
        for (int i = 0; i < 3; i++)
        {
            phone.AddTask($"phone {i}");
            pc.AddTask($"pc {i}");
            Round();
        }
        Round();
        Check.Equal(0, problems.Count, "no file is set aside");
        Check.True(phone.Open.Any(t => t.Text == "pc 2"), "the PC's changes reach the phone");
        Check.True(pc.Open.Any(t => t.Text == "phone 2") && pc.Open.Any(t => t.Text == "milk"), "and the phone's the PC");
        Check.Data(Sorted(pc.ToData()), Sorted(phone.ToData()), "both hold the same");
    }

    private static void SpacesImported()
    {
        var folder = new Folder();
        var (phone, phoneMeta) = Device("phon0001");
        var (pc, pcMeta) = Device("pcpc0001");
        phone.AddTask("base");
        for (int i = 0; i < 2; i++)
        {
            Cycle(phone, folder, phoneMeta);
            Cycle(pc, folder, pcMeta);
        }
        string at = Stamp.Encode(Now - 5000, 0, "aaaa1111");
        string spaces = "[" + string.Join(",", Enumerable.Range(0, 1001).Select(i =>
            $"{{\"id\":\"imp{i}\",\"name\":\"imported {i}\",\"order\":\"W\",\"deleted\":false,\"clk\":{{\"name\":\"{at}\",\"order\":\"{at}\",\"deleted\":\"{Stamp.Zero}\"}}}}")) + "]";
        var (message, _) = FileImport.Run(pc, Encoding.UTF8.GetBytes($"{{\"version\":1,\"tasks\":[],\"sheets\":[],\"tackFocus\":{{\"tasks\":[],\"sheets\":[],\"spaces\":{spaces}}}}}"), null);
        Check.True(!message.Contains("can't", StringComparison.Ordinal) && !message.Contains("more tasks", StringComparison.Ordinal), $"imported ({message})");
        Check.Equal(1001, pc.SpaceList.Count, "every space");
        pc.AddTask("after the import");
        var problems = new List<(string, DeviceFileProblem)>();
        for (int i = 0; i < 2; i++)
        {
            problems.AddRange(Cycle(pc, folder, pcMeta).Problems);
            problems.AddRange(Cycle(phone, folder, phoneMeta).Problems);
        }
        Check.Equal(0, problems.Count, "no file is set aside");
        Check.Equal(1001, phone.SpaceList.Count, "the phone takes them");
        Check.True(phone.Open.Any(t => t.Text == "after the import"), "and what came after");
        DeviceFile.Parse(folder.Files["pcpc0001.json"].Bytes, "pcpc0001", Now); // every device takes the PC's file
    }

    // Whether a device takes a file in at once depends on what it already holds, and honest devices
    // differ by their unsynced edits: one file sized between two devices' totals is taken by one and
    // set aside by the other, which then sets aside the first one's whole file too.

    private static void TakeInAnyway()
    {
        var folder = new Folder();
        var (h1, h1Meta) = Device("hone0001");
        var (h2, h2Meta) = Device("htwo0001");
        h1.AddTask("base");
        for (int i = 0; i < 2; i++)
        {
            Cycle(h1, folder, h1Meta);
            Cycle(h2, folder, h2Meta);
        }
        for (int i = 0; i < 5; i++) h1.AddTask($"unsynced {i}");
        folder.Put("evil0001.json", Deletions("evil0001", DeviceFile.MaxTaken - h2.RecordCount - 1));
        Check.Equal(0, Cycle(h2, folder, h2Meta).Problems.Count, "one PC takes it");
        Check.Equal(DeviceFile.MaxTaken, h2.RecordCount, "up to what it takes in at once");
        h2.AddTask("h2 work");
        Cycle(h2, folder, h2Meta);
        // h1 holds a few more (its unsynced tasks): the file is too much at once for it, and so is
        // h2's, which now carries it. Without a way out, it would stay so.
        for (int i = 0; i < 2; i++)
            Check.True(Cycle(h1, folder, h1Meta).Problems.SequenceEqual([("evil0001", DeviceFileProblem.Full), ("htwo0001", DeviceFileProblem.Full)]), "the other sets both aside");
        Check.True(!h1.Open.Any(t => t.Text == "h2 work"), "and h2's changes don't arrive");
        // Settings → Take it in anyway.
        Check.True(SyncEngine.TakeIn(h1Meta, "htwo0001"), "asked");
        Check.True(!SyncEngine.TakeIn(h1Meta, "hone0001"), "only a file set aside can be asked for");
        Check.True(Cycle(h1, folder, h1Meta).Problems.SequenceEqual([("evil0001", DeviceFileProblem.Full)]), "taken in");
        Check.True(h1.Open.Any(t => t.Text == "h2 work"), "h2's changes arrive");
        Check.True(h1Meta.Take == null, "the request is spent");
        // From then on, both ways, and with the folder writer's file gone too.
        folder.Files.Remove("evil0001.json");
        var problems = new List<(string, DeviceFileProblem)>();
        for (int i = 0; i < 3; i++)
        {
            if (i < 2)
            {
                h1.AddTask($"h1 {i}");
                h2.AddTask($"h2 {i}");
            }
            problems.AddRange(Cycle(h1, folder, h1Meta).Problems);
            problems.AddRange(Cycle(h2, folder, h2Meta).Problems);
        }
        Check.Equal(0, problems.Count, "nothing is set aside");
        Check.True(h1.Open.Any(t => t.Text == "h2 1") && h2.Open.Any(t => t.Text == "h1 1"), "changes arrive both ways");
        DeviceFile.Parse(folder.Files["hone0001.json"].Bytes, "hone0001", Now); // every device takes h1's file
    }

    private static void TakeInBounded()
    {
        var folder = new Folder();
        var (ws, meta) = Device("desk0001");
        folder.Put("aaaa0001.json", Deletions("aaaa0001", DeviceFile.MaxTaken - 10, "a"));
        folder.Put("bbbb0001.json", Deletions("bbbb0001", DeviceFile.MaxRecords - DeviceFile.MaxTaken + 100, "b"));
        Check.True(Cycle(ws, folder, meta).Problems.SequenceEqual([("bbbb0001", DeviceFileProblem.Full)]), "set aside");
        Check.True(SyncEngine.TakeIn(meta, "bbbb0001"), "asked");
        Check.True(Cycle(ws, folder, meta).Problems.SequenceEqual([("bbbb0001", DeviceFileProblem.Full)]), "past what this PC's own file may carry: still set aside");
        Check.True(ws.RecordCount <= DeviceFile.MaxRecords, "never more than a file may carry");
        DeviceFile.Parse(folder.Files["desk0001.json"].Bytes, "desk0001", Now);
        Check.True(meta.Take == null, "the request is spent");
        Check.Equal(2, folder.Reads.GetValueOrDefault("bbbb0001.json"), "read once for it");
        Cycle(ws, folder, meta);
        Check.Equal(2, folder.Reads.GetValueOrDefault("bbbb0001.json"), "and not again");
        // A new version is judged afresh: the request was for the version you saw.
        folder.Put("bbbb0001.json", Deletions("bbbb0001", DeviceFile.MaxTrickle + 5, "b"));
        Check.True(SyncEngine.TakeIn(meta, "bbbb0001"), "asked about the old version");
        Check.True(Cycle(ws, folder, meta).Problems.SequenceEqual([("bbbb0001", DeviceFileProblem.Full)]), "the new one is judged afresh");
        Check.True(meta.Take == null, "that request is gone");
        Check.True(SyncEngine.TakeIn(meta, "bbbb0001"), "asked about this one");
        Check.Equal(0, Cycle(ws, folder, meta).Problems.Count, "taken in");
        Check.True(meta.Take == null, "and spent");
    }

    // With the user's own files: a device that lost its sync state takes back its own file, but went
    // on to judge an out-of-date device's older text of a note as written at the same time as its own,
    // and kept a copy of it that then went to every device.

    private static void AdoptKnowsSeen()
    {
        var folder = new Folder();
        long wall = Now;
        // Merged in this order: the old device's file before the phone's.
        var (old, oldMeta) = Device("AAAAold1", () => wall);
        var (pc, pcMeta) = Device("mmmmpc01", () => wall);
        var (phone, phoneMeta) = Device("zzzzphon", () => wall);
        var note = phone.AddSheet(text: "Notes v1");
        for (int i = 0; i < 2; i++)
        {
            Cycle(old, folder, oldMeta, wall);
            Cycle(pc, folder, pcMeta, wall);
            Cycle(phone, folder, phoneMeta, wall);
        }
        // The old device is left behind; the phone edits the note, and the PC takes it in.
        wall += 1000;
        phone.SheetById(note.Id)!.Text = "Notes v2";
        for (int i = 0; i < 2; i++)
        {
            Cycle(phone, folder, phoneMeta, wall);
            Cycle(pc, folder, pcMeta, wall);
        }
        Check.Equal("Notes v2", string.Join("|", pc.Sheets.Select(s => s.Text).Where(t => t.Length > 0)), "the PC has the phone's edit");
        // The PC loses its sync state (not its data or its id), and takes its own file back.
        wall += 1000;
        pcMeta = SyncMeta.Empty("folder");
        var result = Cycle(pc, folder, pcMeta, wall);
        Check.Equal("AAAAold1,zzzzphon", string.Join(",", result.Merged), "both other files merged, the old one first");
        Check.Equal("Notes v2", string.Join("|", pc.Sheets.Select(s => s.Text).Where(t => t.Length > 0)), "and no copy of the older text");
        Check.True(pcMeta.Seen.ContainsKey("zzzzphon"), "what it had seen is known again");
    }

    private static WorkspaceData Sorted(WorkspaceData d) => d with
    {
        Tasks = d.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal).ToList(),
        Sheets = d.Sheets.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(),
        Spaces = d.Spaces.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(),
    };

    // ---------------------------------------------------------------- helpers

    /// <summary>A hand-made device file with one live task (its text as JSON-escaped source).</summary>
    private static string Craft(string device, long now, string text, string? extra = null, string? tasksJson = null)
    {
        string at = Stamp.Encode(now, 0, device);
        string tasks = tasksJson ?? $"[{{\"id\":\"t{device}\",\"text\":\"{text}\",\"created\":\"2026-10-21T09:00:00.000Z\",\"completed\":null,\"order\":\"V\",\"deleted\":false,"
            + $"\"clk\":{{\"text\":\"{at}\",\"completed\":\"{Stamp.Zero}\",\"order\":\"{at}\",\"deleted\":\"{Stamp.Zero}\"}}}}]";
        return $"{{\"format\":\"tack-sync-device\",\"version\":1,\"device\":\"{device}\",\"name\":\"crafted\",\"app\":\"test\",\"written\":\"{Stamp.Encode(now, 1, device)}\","
            + (extra ?? "\"seen\":{}") + $",\"focus\":{{\"id\":null,\"clk\":\"{Stamp.Zero}\"}},\"tasks\":{tasks},\"sheets\":[],\"deleted\":{{\"tasks\":{{}},\"sheets\":{{}}}}}}";
    }

    private static TaskData Record(string created, string? completed)
    {
        string at = Stamp.Encode(Now, 0, "aaaa1111");
        string json = $"{{\"id\":\"t1\",\"text\":\"x\",\"created\":{JsonSerializer.Serialize(created)},\"completed\":{JsonSerializer.Serialize(completed)},\"order\":\"V\",\"deleted\":false,"
            + $"\"clk\":{{\"text\":\"{at}\",\"completed\":\"{at}\",\"order\":\"{at}\",\"deleted\":\"{Stamp.Zero}\"}}}}";
        using var doc = JsonDocument.Parse(json);
        return TackFile.ReadTaskRecord(doc.RootElement, Now, clean: true)!;
    }

    private static DeviceFileProblem ProblemOf(Func<object> parse) => ProblemOf(() => { parse(); });

    private static DeviceFileProblem ProblemOf(Action parse)
    {
        try
        {
            parse();
        }
        catch (DeviceFileException ex)
        {
            return ex.Problem;
        }
        throw new InvalidOperationException("expected the file to be refused");
    }

    private static (Workspace Ws, SyncMeta Meta) Device(string id, Func<long>? wall = null)
    {
        var clock = wall ?? (() => Now);
        return (new Workspace(new Settings(), new Hlc(id, clock), null, () => DateTimeOffset.FromUnixTimeMilliseconds(clock())), SyncMeta.Empty("folder"));
    }

    private static CycleResult Cycle(Workspace ws, Folder folder, SyncMeta meta, long? now = null)
    {
        var engine = new SyncEngine(ws, folder, new SyncHooks
        {
            Label = () => (ws.Clock.Node, "test"),
            FlushAsync = () => Task.CompletedTask,
            SaveMetaAsync = _ => Task.CompletedTask,
        });
        return engine.CycleAsync(meta, now ?? Now).GetAwaiter().GetResult();
    }

    /// <summary>A folder in memory that reports sizes, counts reads and can fail one.</summary>
    private sealed class Folder : ISyncFolder
    {
        private int _n;
        public Dictionary<string, (string Tag, byte[] Bytes, long Size)> Files { get; } = new();
        public Dictionary<string, int> Reads { get; } = new();
        public SyncErrorKind? FailNextRead { get; set; }

        public void Put(string name, string text, long? size = null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Files[name] = ($"t{++_n}", bytes, size ?? bytes.Length);
        }

        public Task<IReadOnlyList<FolderEntry>> ListAsync() =>
            Task.FromResult<IReadOnlyList<FolderEntry>>(Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new FolderEntry(f.Key, f.Value.Tag, f.Value.Size)).ToList());

        public Task<byte[]> ReadAsync(FolderEntry entry)
        {
            if (FailNextRead is { } kind)
            {
                FailNextRead = null;
                throw new SyncException(kind, "can't read it right now");
            }
            Reads[entry.Name] = Reads.GetValueOrDefault(entry.Name) + 1;
            return Files.TryGetValue(entry.Name, out var f) ? Task.FromResult(f.Bytes) : throw new SyncException(SyncErrorKind.Gone, "gone");
        }

        public Task<FolderEntry> WriteAsync(string name, byte[] content)
        {
            Files[name] = ($"t{++_n}", content, content.Length);
            return Task.FromResult(new FolderEntry(name, Files[name].Tag, content.Length));
        }
    }
}
