using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tack.Model;
using Tack.Services;
using Tack.Snippets;
using Tack.Sync;
using Tack.Views;
using Tack.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace Tack.Tests;

/// <summary>
/// Snippets: images pasted into Notes, kept beside tack.json on this PC only. Pastes come as the clipboard
/// hands them over (through COM, in global memory), never through this PC's own clipboard. The hostile cases
/// are in <see cref="HostileFileTests"/>.
/// </summary>
internal static class SnippetTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("snippets: nothing on disk until the first paste; then a PNG and snippets.json, read back the same", StoreRoundTrip);
        yield return ("snippets: a snip (PNG with a DIB beside it) is kept exactly as it came, with a thumbnail", SnipKeptAsItCame);
        yield return ("snippets: a bitmap alone (DIB: 24-bit, 32-bit with no alpha, top-down, bit fields) is kept the right way up and opaque", DibKept);
        yield return ("snippets: image files copied in Explorer are kept; other files aren't Notes' business", FilesKept);
        yield return ("snippets: anything with text pastes as text, an image under it or not", TextWins);
        yield return ("snippets: a huge image is scaled down to fit before it's kept", HugeScaledDown);
        yield return ("snippets: up to 50 snippets and 50 MB, then a toast says why", Caps);
        yield return ("snippets: never in tack.json, so never synced, imported or exported", NeverInTackJson);
        yield return ("snippets: at start, snippets of deleted sheets and files nothing names are cleared away", PrunedAtStart);
        yield return ("snippets: a tack.json that can't be read (nor its .bak) clears none of them, all session; restored, each sheet has its own", KeptWhileTackJsonUnreadable);
        yield return ("snippets: discarded ones an Undo could put back keep their room, so Undo never takes Notes past its caps", UndoWithinCaps);
        yield return ("notes: a paste that needs the room an Undo holds ends that Undo, and the paste is kept", PasteEndsHeldUndo);
        yield return ("toast undo: Undo runs once and nothing lapses; another toast, the toast going or quitting makes it lapse once", UndoSlotLapses);
        yield return ("notes on screen: no snippets, the paper exactly as before; a paste shows a thumbnail strip above the text", StripOnScreen);
        yield return ("notes on screen: x discards at once with Undo, Undo puts it back, and the file goes once Undo lapses", DiscardWithUndo);
        yield return ("notes on screen: the snippet strip is one tab stop; Delete discards (with Undo) and the focus stays in the strip; a click leaves the focus in the text", SnippetKeys);
        yield return ("notes on screen: deleting a sheet takes its snippets; Undo brings both back; the last sheet is cleared of them", DeleteSheetTakesSnippets);
        yield return ("notes on screen: the menu's Paste with an image adds a snippet; on blank paper it makes the sheet", PasteCommandAndBlankPaper);
        yield return ("notes on screen: an image pasted on Tasks lands in Notes with a toast and Undo", FromTasks);
        yield return ("snippet viewer on screen: one pixel to a pixel when it fits, scaled to nine tenths of the display when not", ViewerFits);
    }

    // ================= Store =================

    private static void StoreRoundTrip()
    {
        using var dir = new Temp();
        var store = new SnippetStore(dir.Path);
        Check.True(!File.Exists(store.IndexPath) && !Directory.Exists(store.Folder), "nothing on disk before the first paste");

        var png = One(Clip(("PNG", Stream(Png(Picture(300, 200))))));
        var (snippet, refusal) = store.Add("sheet0000001", png, At(9, 5));
        Check.Equal(SnippetRefusal.None, refusal, "kept");
        Check.True(snippet != null && File.Exists(store.PathOf(snippet)), "its PNG");
        Check.True(File.Exists(store.IndexPath), "and snippets.json");
        Check.Equal(Path.Combine(dir.Path, "snippets"), store.Folder, "in the snippets folder beside tack.json");

        var again = new SnippetStore(dir.Path);
        var back = again.Of("sheet0000001").Single();
        Check.Equal(snippet, back, "read back the same");
        Check.Equal(300, back.Width, "its width");
        Check.Equal(png.Bytes.Length, (int)back.Size, "its size");

        using var doc = JsonDocument.Parse(File.ReadAllBytes(store.IndexPath));
        var e = doc.RootElement.GetProperty("snippets")[0];
        Check.Equal("sheet0000001", e.GetProperty("sheetId").GetString(), "sheetId");
        Check.Equal(snippet!.Id + ".png", e.GetProperty("file").GetString(), "file");
        Check.True(IsoTime.Parse(e.GetProperty("created").GetString()) == back.Created, "created");
        Check.Equal(png.Bytes.Length, e.GetProperty("size").GetInt32(), "size");
    }

    private static void SnipKeptAsItCame()
    {
        Ui.Run(_ =>
        {
            var picture = Picture(640, 360);
            byte[] png = Png(picture);
            // Win+Shift+S puts a PNG and a bitmap on the clipboard; the PNG is the one taken.
            var found = SnippetImage.FromPaste(Clip(("PNG", Stream(png)), (DataFormats.Dib, Stream(Dib(picture, 24)))));
            Check.Equal(PasteProblem.None, found.Problem, "no problem");
            var kept = found.Images.Single();
            Check.True(kept.Bytes.AsSpan().SequenceEqual(png), "the PNG as it came (the clipboard's rounding trimmed off)");
            Check.Equal((640, 360), (kept.Width, kept.Height), "its size");
            Check.True(kept.Thumb != null && kept.Thumb.PixelHeight == SnippetImage.ThumbPixels, $"a thumbnail {SnippetImage.ThumbPixels} pixels tall ({kept.Thumb?.PixelHeight})");
            Check.True(kept.Thumb!.IsFrozen, "frozen, so it can be shown anywhere");

            // "image/png", as some apps name it.
            Check.Equal(1, SnippetImage.FromPaste(Clip(("image/png", Stream(png)))).Images.Count, "image/png too");
            // Straight from an object in this process (not through COM), as a stream or bytes.
            Check.Equal(1, SnippetImage.FromPaste(Plain(("PNG", Stream(png)))).Images.Count, "a stream in this process");
        });
    }

    private static void DibKept()
    {
        Ui.Run(_ =>
        {
            var picture = Picture(120, 80);
            foreach (var (name, dib) in new[]
            {
                ("24-bit", Dib(picture, 24)),
                ("32-bit, alpha all zero", Dib(picture, 32, zeroAlpha: true)),
                ("32-bit top-down", Dib(picture, 32, topDown: true)),
                ("32-bit bit fields", Dib(picture, 32, bitFields: true)),
            })
            {
                var found = SnippetImage.FromPaste(Clip((DataFormats.Dib, Stream(dib))));
                Check.Equal(PasteProblem.None, found.Problem, $"{name}: kept");
                var kept = found.Images.Single();
                Check.Equal((120, 80), (kept.Width, kept.Height), $"{name}: its size");
                var decoded = Decode(kept.Bytes);
                Check.Equal(Colors.Red, Pixel(decoded, 5, 5), $"{name}: red at the top left (the right way up)");
                Check.Equal(Colors.Blue, Pixel(decoded, 115, 75), $"{name}: blue at the bottom right");
            }
        });
    }

    private static void FilesKept()
    {
        Ui.Run(_ =>
        {
            using var dir = new Temp();
            string png = Path.Combine(dir.Path, "Clash report.png"), jpg = Path.Combine(dir.Path, "Site photo.JPG"), txt = Path.Combine(dir.Path, "notes.txt");
            File.WriteAllBytes(png, Png(Picture(200, 100)));
            File.WriteAllBytes(jpg, Jpeg(Picture(160, 120)));
            File.WriteAllText(txt, "not an image");

            var found = SnippetImage.FromPaste(Clip((DataFormats.FileDrop, new[] { png, txt, jpg })));
            Check.Equal(PasteProblem.None, found.Problem, "no problem");
            Check.Equal(2, found.Images.Count, "the two images, not the text file");
            Check.Equal((160, 120), (found.Images[1].Width, found.Images[1].Height), "the photo, drawn again as a PNG");
            Check.True(SnippetImage.PngSize(found.Images[1].Bytes) == (160, 120), "a PNG");

            Check.True(!SnippetImage.FromPaste(Clip((DataFormats.FileDrop, new[] { txt }))).IsImage, "a text file alone: left to the paste as before");
            var many = Enumerable.Range(0, 14).Select(i => png).ToArray();
            Check.Equal(SnippetImage.MaxFilesPerPaste, SnippetImage.FromPaste(Clip((DataFormats.FileDrop, many))).Images.Count, "ten at a time");
        });
    }

    private static void TextWins()
    {
        Ui.Run(_ =>
        {
            var picture = Dib(Picture(50, 20), 24);
            Check.True(!SnippetImage.FromPaste(Clip((DataFormats.UnicodeText, "Level 3\tDucts\r\n"), (DataFormats.Dib, Stream(picture)))).IsImage, "cells copied from Excel: the text");
            Check.True(!SnippetImage.FromPaste(Clip((DataFormats.Text, "ANSI text"), ("PNG", Stream(Png(Picture(10, 10)))))).IsImage, "ANSI text too");
            Check.True(!SnippetImage.FromPaste(Clip((DataFormats.UnicodeText, "plain text"))).IsImage, "plain text");
            Check.True(SnippetImage.FromPaste(Clip((DataFormats.UnicodeText, "  \r\n"), (DataFormats.Dib, Stream(picture)))).IsImage, "only blanks beside a picture: the picture");
            Check.True(!SnippetImage.FromPaste(Clip(("HTML Format", "<b>x</b>"))).IsImage, "nothing Notes takes");
            Check.True(!SnippetImage.FromPaste(null).IsImage, "an empty clipboard");

            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            using var dir = new Temp();
            var notes = Notes(ws, new SnippetStore(dir.Path), new Host());
            Check.True(!notes.Paste(Clip((DataFormats.UnicodeText, "Call the client"))), "Notes leaves text to the editor");
            Check.Equal(0, new SnippetStore(dir.Path).Count, "no snippet");
        });
    }

    private static void HugeScaledDown()
    {
        Ui.Run(_ =>
        {
            // Three 4K screens side by side, as one snip: wider than a snippet is kept.
            var found = SnippetImage.FromPaste(Clip(("PNG", Stream(Png(Picture(11520, 2160))))));
            Check.Equal(PasteProblem.None, found.Problem, "kept");
            var kept = found.Images.Single();
            Check.True(kept.Width <= SnippetImage.MaxStoredSide && (long)kept.Width * kept.Height <= SnippetImage.MaxStoredPixels, $"scaled to fit ({kept.Width}x{kept.Height})");
            Check.True(Math.Abs((double)kept.Width / kept.Height - 11520.0 / 2160) < 0.02, "the same shape");
            Check.True(SnippetImage.PngSize(kept.Bytes) == (kept.Width, kept.Height), "the PNG is that size");
            Check.True(kept.Bytes.Length <= SnippetImage.MaxSnippetBytes, "and within a snippet's bytes");
        });
    }

    private static void Caps()
    {
        Ui.Run(_ =>
        {
            using var dir = new Temp();
            var store = new SnippetStore(dir.Path);
            var small = One(Clip(("PNG", Stream(Png(Picture(40, 30))))));
            for (int i = 0; i < SnippetStore.MaxCount; i++) Check.Equal(SnippetRefusal.None, store.Add("sheet0000001", small, At(9, i % 60)).Refusal, $"#{i + 1}");
            Check.Equal(SnippetRefusal.Full, store.Add("sheet0000001", small, At(10, 0)).Refusal, "the 51st: full");

            var big = new SnippetPng(new byte[20 * 1024 * 1024], 10, 10, null);
            var roomy = new SnippetStore(new Temp().Path);
            Check.Equal(SnippetRefusal.None, roomy.Add("s1", big, At(9, 0)).Refusal, "20 MB");
            Check.Equal(SnippetRefusal.None, roomy.Add("s1", big, At(9, 1)).Refusal, "40 MB");
            Check.Equal(SnippetRefusal.NoRoom, roomy.Add("s1", big, At(9, 2)).Refusal, "60 MB: no room");
            Check.Equal(2, roomy.Count, "two kept");
            Directory.Delete(Path.GetDirectoryName(roomy.Folder)!, recursive: true);

            // In Notes, the refusal is a toast.
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            var host = new Host();
            var notes = Notes(ws, store, host);
            Check.True(notes.Paste(Clip(("PNG", Stream(Png(Picture(40, 30)))))), "the paste is taken");
            Check.Equal("Notes keeps up to 50 snippets: discard one to make room", host.Toasts.LastOrDefault(), "and the toast says why");
        });
    }

    private static void NeverInTackJson()
    {
        Ui.Run(_ =>
        {
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            ws.Sheets[0].Text = "Plan";
            using var dir = new Temp();
            var store = new SnippetStore(dir.Path);
            var notes = Notes(ws, store, new Host());
            byte[] Saved() => JsonSerializer.SerializeToUtf8Bytes(StateFile.ToDocument(ws), StateJsonContext.Default.StateDocument);
            var before = Saved();
            var records = ws.ToData();
            Check.True(notes.Paste(Clip(("PNG", Stream(Png(Picture(64, 64)))))), "pasted");
            Check.Equal(1, store.Count, "kept");
            Check.True(before.AsSpan().SequenceEqual(Saved()), "tack.json (and so every device file and export) exactly as it was");
            Check.Data(records, ws.ToData(), "the records a device file is written from, untouched");
        });
    }

    private static void PrunedAtStart()
    {
        using var dir = new Temp();
        var store = new SnippetStore(dir.Path);
        var png = new SnippetPng(Png(Picture(20, 20)), 20, 20, null);
        var live = store.Add("live00000001", png, At(9, 0)).Snippet!;
        var gone = store.Add("gone00000001", png, At(9, 1)).Snippet!;
        var discarded = store.Add("live00000001", png, At(9, 2)).Snippet!;
        store.Detach([discarded]); // Tack closed before its Undo lapsed

        // Someone's own files in the folder, and Tack's tack.json beside it, are never touched.
        string mine = Path.Combine(store.Folder, "my notes.txt"), upper = Path.Combine(store.Folder, new string('A', 32) + ".png"), tack = Path.Combine(dir.Path, "tack.json");
        File.WriteAllText(mine, "mine");
        File.WriteAllText(upper, "mine too");
        File.WriteAllText(tack, "{}");

        var next = new SnippetStore(dir.Path);
        Check.True(!File.Exists(next.PathOf(discarded)), "the discarded one's file is cleared away");
        next.Prune(id => id == "live00000001");
        Check.Equal(live, next.Of("live00000001").Single(), "the live sheet's snippet stays");
        Check.True(next.Of("gone00000001").Count == 0 && !File.Exists(next.PathOf(gone)), "the gone sheet's snippet goes, file and all");
        Check.True(File.Exists(mine) && File.Exists(upper) && File.Exists(tack), "nothing else is touched");
        Check.Equal(1, new SnippetStore(dir.Path).Count, "and that's saved");
    }

    private static void KeptWhileTackJsonUnreadable()
    {
        Ui.Run(_ =>
        {
            using var dir = new Temp();
            var state = new StateStore(dir.Path);
            var ws = state.Attach(state.Load());
            var sheet = ws.AddSheet(null, "Clash review L3");
            state.Flush();
            var snip = new SnippetStore(dir.Path).Add(sheet.Id, new SnippetPng(Png(Picture(20, 20)), 20, 20, null), At(9, 0)).Snippet!;
            sheet.Text = "Clash review L3\n- duct vs beam at C4";
            state.Flush(); // so tack.json.bak is there too
            byte[] good = File.ReadAllBytes(state.FilePath);
            Check.True(File.Exists(state.FilePath + ".bak"), "tack.json and its .bak");

            // Both unreadable (a bad sector, a sync tool, a half-copied folder): Tack starts empty.
            File.WriteAllText(state.FilePath, "{ \"version\": 1, \"tasks\": [");
            File.WriteAllText(state.FilePath + ".bak", "\0\0\0");
            var lost = new StateStore(dir.Path);
            var empty = lost.Attach(lost.Load());
            Check.True(lost.LoadFailed && empty.LoadFailed && empty.SheetById(sheet.Id) == null, "started empty, and knows why");
            var held = new SnippetStore(dir.Path);
            held.Prune(empty);
            Check.True(held.Count == 1 && File.Exists(held.PathOf(snip)), "the snippet and its file stay at start");
            empty.AddSheet(null, "Carrying on");
            lost.Flush();
            held.Prune(empty);
            Check.True(held.Count == 1 && File.Exists(held.PathOf(snip)), "and all session, whatever is written meanwhile");

            // tack.json restored (from a backup): the sheet has its snippet again.
            File.WriteAllBytes(state.FilePath, good);
            var restored = new StateStore(dir.Path);
            var back = restored.Attach(restored.Load());
            var again = new SnippetStore(dir.Path);
            again.Prune(back);
            Check.True(!back.LoadFailed && again.Of(sheet.Id).Count == 1, "restored: the sheet and its snippet");

            // A tack.json that reads, without the sheet (deleted for good): its snippets go, as ever.
            back.DeleteSheet(back.SheetById(sheet.Id)!);
            again.Prune(back);
            Check.True(again.Count == 0 && !File.Exists(again.PathOf(snip)), "a sheet that's really gone takes its snippets");
        });
    }

    private static void UndoWithinCaps()
    {
        Ui.Run(_ =>
        {
            using var dir = new Temp();
            var store = new SnippetStore(dir.Path);
            var png = new SnippetPng(Png(Picture(16, 16)), 16, 16, null);
            var onA = Enumerable.Range(0, 10).Select(i => store.Add("sheetaaaaaaa", png, At(9, i)).Snippet!).ToList();
            for (int i = 0; i < 35; i++) store.Add("sheetbbbbbbb", png, At(10, i));
            store.Detach(onA); // sheet A deleted: its Undo on offer
            int pasted = 0;
            SnippetRefusal last = SnippetRefusal.None;
            for (int i = 0; i < 10; i++)
            {
                var (s, refusal) = store.Add("sheetbbbbbbb", png, At(11, i));
                if (s != null) pasted++;
                else last = refusal;
            }
            Check.Equal(5, pasted, "pasted only into the room the Undo doesn't hold");
            Check.Equal(SnippetRefusal.Held, last, "and says why");
            store.Attach(onA); // Undo
            Check.Equal(SnippetStore.MaxCount, store.Count, "Undo puts them all back, and that's the cap");
            var restarted = new SnippetStore(dir.Path);
            Check.True(restarted.Count == SnippetStore.MaxCount && restarted.Of("sheetaaaaaaa").Count == 10, "a restart keeps every one, the ones Undo put back too");
            Check.Equal(SnippetStore.MaxCount, Directory.GetFiles(restarted.Folder, "*.png").Length, "and every file");

            // Once the Undo lapses, the room is free again.
            var one = restarted.Of("sheetaaaaaaa").Take(1).ToList();
            restarted.Detach(one);
            Check.Equal(SnippetRefusal.Held, restarted.Add("sheetbbbbbbb", png, At(12, 0)).Refusal, "held while its Undo is on offer");
            restarted.Forget(one);
            Check.Equal(SnippetRefusal.None, restarted.Add("sheetbbbbbbb", png, At(12, 1)).Refusal, "free once it lapses");

            // Bytes are held the same way.
            var big = new SnippetPng(new byte[20 * 1024 * 1024], 10, 10, null);
            using var roomy = new Temp();
            var bytes = new SnippetStore(roomy.Path);
            var first = bytes.Add("sheetaaaaaaa", big, At(9, 0)).Snippet!;
            bytes.Add("sheetaaaaaaa", big, At(9, 1));
            bytes.Detach([first]);
            Check.Equal(SnippetRefusal.Held, bytes.Add("sheetaaaaaaa", big, At(9, 2)).Refusal, "20 MB held by a discarded one");
            bytes.Attach([first]);
            Check.True(bytes.TotalBytes <= SnippetStore.MaxTotalBytes, "Undo stays within 50 MB");
        });
    }

    private static void PasteEndsHeldUndo()
    {
        Ui.Run(errors =>
        {
            using var dir = new Temp();
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            var store = new SnippetStore(dir.Path);
            var host = new Host();
            var notes = Notes(ws, store, host);
            using var window = Ui.Host(notes);
            var png = new SnippetPng(Png(Picture(16, 16)), 16, 16, null);
            for (int i = 0; i < SnippetStore.MaxCount; i++) store.Add(ws.ActiveSheet.Id, png, At(9, i % 60));
            var discarded = store.Of(ws.ActiveSheet.Id)[0];
            notes.Discard(discarded); // to make room: its Undo on offer
            Check.True(host.CanUndo, "Undo on offer");
            notes.Keep(new PastedImages([png], PasteProblem.None), fromTasks: false);
            Check.True(!host.CanUndo, "the paste ended that Undo");
            Check.Equal(SnippetStore.MaxCount, store.Count, "and took the room");
            Check.True(!File.Exists(store.PathOf(discarded)), "the discarded one's file went as the Undo lapsed");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void UndoSlotLapses()
    {
        var slot = new UndoSlot();
        int undone = 0, lapsed = 0;
        slot.Hold(() => undone++, () => lapsed++);
        Check.True(slot.CanUndo, "an Undo held");
        slot.Take()!();
        Check.Equal((1, 0), (undone, lapsed), "undone, nothing lapsed");
        slot.Lapse();
        Check.Equal(0, lapsed, "and it can't lapse later");

        slot.Hold(() => undone++, () => lapsed++);
        slot.Hold(null, null);
        Check.Equal((1, 1), (undone, lapsed), "another toast: the first lapses");
        Check.True(slot.Take() == null, "a toast without Undo has nothing to take");

        slot.Hold(() => undone++, () => lapsed++);
        slot.Lapse();
        slot.Lapse();
        Check.Equal(2, lapsed, "the toast going: lapses once");
        slot.Hold(null, () => throw new IOException("disk gone"));
        slot.Lapse();
        Check.True(!slot.CanUndo, "an error while lapsing is logged, not thrown");
    }

    // ================= On screen =================

    private static void StripOnScreen()
    {
        Ui.Run(errors =>
        {
            using var dir = new Temp();
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            ws.Sheets[0].Text = "Revit: export the NWC\r\n- File > Export > NWC\r\n- tick Convert linked files";
            var store = new SnippetStore(dir.Path);
            var host = new Host();
            var notes = Notes(ws, store, host);
            var editor = (TextBox)notes.FindName("Editor");
            var scroller = (FrameworkElement)notes.FindName("SnippetScroller");
            var paper = (FrameworkElement)VisualTreeHelper.GetParent(editor);
            var surface = Surface(notes);
            using var window = Ui.Host(surface);

            Check.Equal(Visibility.Collapsed, scroller.Visibility, "no snippets: no strip");
            double top = editor.TranslatePoint(new Point(0, 0), paper).Y;
            Check.True(Math.Abs(top) < 0.5, $"the text starts at the top of the paper, as before ({top})");
            Shots.Save(surface, "notes-none");

            Check.True(notes.Paste(Clip(("PNG", Stream(Png(Picture(900, 500)))))), "a snip");
            Check.True(notes.Paste(Clip((DataFormats.Dib, Stream(Dib(Picture(300, 600), 24))))), "a tall one");
            Ui.Pump();
            Check.Equal(Visibility.Visible, scroller.Visibility, "the strip shows");
            var thumbs = Ui.Descendants<Button>(scroller).Where(b => AutomationProperties.GetName(b).StartsWith("Snippet ", StringComparison.Ordinal)).ToList();
            Check.Equal(2, thumbs.Count, "a thumbnail each");
            Check.True(AutomationProperties.GetName(thumbs[0]).StartsWith("Snippet 1 of 2, pasted ", StringComparison.Ordinal), "named for screen readers");
            Check.True(Math.Abs(thumbs[0].ActualHeight - NotesView.ThumbHeight) < 0.5, "52 tall");
            Check.True(Math.Abs(thumbs[0].ActualWidth - 93.6) < 0.5, $"as wide as its shape ({thumbs[0].ActualWidth})");
            Check.True(Math.Abs(thumbs[1].ActualWidth - 40) < 0.5, $"a tall one at least 40 wide ({thumbs[1].ActualWidth})");
            Check.True(thumbs[0].Background is ImageBrush, "showing the image");
            double below = editor.TranslatePoint(new Point(0, 0), paper).Y;
            Check.True(below >= 10 + NotesView.ThumbHeight - 0.5, $"the text below the strip ({below})");
            var discards = Ui.Descendants<Button>(scroller).Where(b => AutomationProperties.GetName(b) == "Discard snippet").ToList();
            Check.True(discards.Count == 2 && discards.All(d => d.Opacity == 0 && !d.IsHitTestVisible), "x hidden until the pointer is over it");
            Check.Equal(0, host.Toasts.Count, "pasted in Notes: no toast");
            Check.True(host.Announced.Contains("Snippet added"), "but screen readers hear it");

            // Hovering a thumbnail shows its x; then a look in both themes.
            var cell = (FrameworkElement)VisualTreeHelper.GetParent(discards[0]);
            cell.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Ui.WaitUntil(() => discards[0].Opacity > 0.99, 1000);
            Check.True(discards[0].IsHitTestVisible && discards[0].Opacity > 0.99, "x shows on hover");
            Shots.Save(surface, "notes-snippets-light");
            Shots.Dark(() => Shots.Save(surface, "notes-snippets-dark"));

            // Another sheet has none: no strip; back again, there they are.
            ws.AddSheet();
            Ui.Pump();
            Check.Equal(Visibility.Collapsed, scroller.Visibility, "another sheet: no strip");
            ws.ActiveSheet = ws.Sheets[0];
            Ui.Pump();
            Check.Equal(Visibility.Visible, scroller.Visibility, "back: the strip");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void SnippetKeys()
    {
        Ui.Run(errors =>
        {
            using var dir = new Temp();
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            var store = new SnippetStore(dir.Path);
            var host = new Host();
            var notes = Notes(ws, store, host);
            var scroller = (FrameworkElement)notes.FindName("SnippetScroller");
            var strip = (Panel)notes.FindName("SnippetStrip");
            var editor = (TextBox)notes.FindName("Editor");
            using var window = Ui.Host(notes);
            for (int i = 0; i < 3; i++) notes.Paste(Clip(("PNG", Stream(Png(Picture(120 + i, 90))))));
            Ui.Pump();
            Button[] Thumbs() => Ui.Descendants<Button>(scroller).Where(b => AutomationProperties.GetName(b).StartsWith("Snippet ", StringComparison.Ordinal)).ToArray();
            Check.Equal(3, Thumbs().Length, "three thumbnails");
            Check.True(Thumbs().All(t => t.Focusable), "each can take the focus");
            Check.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(strip), "the strip is one tab stop");
            Check.Equal(KeyboardNavigationMode.Contained, KeyboardNavigation.GetDirectionalNavigation(strip), "arrow keys move between thumbnails");

            // A click views it and leaves the focus in the text.
            editor.Focus();
            var first = Thumbs()[0];
            first.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            Check.True(!first.Focusable, "while it's clicked, it doesn't take the focus");
            Ui.Pump();
            Check.True(first.Focusable && editor.IsKeyboardFocused, "and the text keeps it");

            // From the keyboard: Delete discards the one with the focus, with Undo; the focus moves to the next.
            var middle = Thumbs()[1];
            var gone = (Snippet)middle.Tag;
            Check.True(middle.Focus(), "a thumbnail takes the focus");
            middle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(middle), 0, Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent });
            Ui.Pump();
            Check.True(store.Count == 2 && !store.Of(ws.ActiveSheet.Id).Contains(gone), "Delete discards it");
            Check.Equal("Snippet discarded", host.Toasts.LastOrDefault(), "with Undo");
            Check.True(Thumbs()[1].IsKeyboardFocused, "the focus moves to the next one along");
            host.Undo();
            Ui.Pump();
            Check.Equal(3, store.Count, "Undo puts it back");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void DiscardWithUndo()
    {
        Ui.Run(errors =>
        {
            using var dir = new Temp();
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            var store = new SnippetStore(dir.Path);
            var host = new Host();
            var notes = Notes(ws, store, host);
            var scroller = (FrameworkElement)notes.FindName("SnippetScroller");
            using var window = Ui.Host(notes);

            notes.Paste(Clip(("PNG", Stream(Png(Picture(200, 120))))));
            Ui.Pump();
            var snippet = store.Of(ws.ActiveSheet.Id).Single();
            string file = store.PathOf(snippet);
            Click(Ui.Descendants<Button>(scroller).Single(b => AutomationProperties.GetName(b) == "Discard snippet"));
            Ui.Pump();
            Check.Equal(0, store.Count, "off the sheet at once");
            Check.Equal(Visibility.Collapsed, scroller.Visibility, "and off the paper");
            Check.True(File.Exists(file), "its file kept while Undo can bring it back");
            Check.Equal("Snippet discarded", host.Toasts.LastOrDefault(), "a toast with Undo");
            Check.True(host.CanUndo, "Undo offered");
            Check.True(!File.ReadAllText(store.IndexPath).Contains(snippet.Id, StringComparison.Ordinal), "saved off the sheet (quitting now clears its file at the next start)");

            host.Undo();
            Ui.Pump();
            Check.Equal(snippet, store.Of(ws.ActiveSheet.Id).Single(), "Undo puts it back");
            Check.Equal(Visibility.Visible, scroller.Visibility, "on the paper");

            Click(Ui.Descendants<Button>(scroller).Single(b => AutomationProperties.GetName(b) == "Discard snippet"));
            host.Lapse();
            Check.True(!File.Exists(file), "the file goes once Undo lapses");

            // A thumbnail's click opens the viewer for it; so does View in its menu.
            notes.Paste(Clip(("PNG", Stream(Png(Picture(200, 120))))));
            Ui.Pump();
            var thumb = Ui.Descendants<Button>(scroller).Single(b => AutomationProperties.GetName(b).StartsWith("Snippet,", StringComparison.Ordinal));
            Click(thumb);
            Check.True(host.Viewed.Count == 1 && host.Viewed[0].Path == store.PathOf(store.Of(ws.ActiveSheet.Id)[0]), "a click opens it");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void DeleteSheetTakesSnippets()
    {
        Ui.Run(errors =>
        {
            using var dir = new Temp();
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            ws.Sheets[0].Text = "Keep";
            var sheet = ws.AddSheet();
            var store = new SnippetStore(dir.Path);
            var host = new Host();
            var notes = Notes(ws, store, host);
            using var window = Ui.Host(notes);

            notes.Paste(Clip(("PNG", Stream(Png(Picture(80, 60))))));
            var snippet = store.Of(sheet.Id).Single();
            notes.DeleteSheet(sheet);
            Check.True(sheet.IsDeleted && store.Count == 0, "the sheet and its snippet go");
            Check.Equal("Sheet deleted", host.Toasts.LastOrDefault(), "a sheet with only a snippet isn't empty: Undo is offered");
            host.Undo();
            Ui.Pump();
            Check.True(!sheet.IsDeleted && store.Of(sheet.Id).SequenceEqual([snippet]), "Undo brings both back");

            notes.DeleteSheet(sheet);
            host.Lapse();
            Check.True(!File.Exists(store.PathOf(snippet)), "once Undo lapses, the file goes");

            // The last sheet is cleared instead, and of its snippets too.
            var only = ws.Sheets.Single();
            notes.Paste(Clip(("PNG", Stream(Png(Picture(80, 60))))));
            notes.DeleteSheet(only);
            Check.True(!only.IsDeleted && only.Text == "" && store.Count == 0, "cleared, snippets and all");
            host.Undo();
            Check.True(only.Text == "Keep" && store.Count == 1, "Undo brings it all back");

            // Joining sync leaves blank sheets behind, but not one holding a snippet.
            var fresh = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            var blank = fresh.AddSheet();
            var store2 = new SnippetStore(new Temp().Path);
            fresh.HoldsLocalContent = store2.Any;
            store2.Add(blank.Id, new SnippetPng(Png(Picture(8, 8)), 8, 8, null), At(9, 0));
            fresh.LeaveWelcomeBehind(_ => false);
            Check.True(!blank.IsDeleted, "a sheet with a snippet isn't blank");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void PasteCommandAndBlankPaper()
    {
        Ui.Run(errors =>
        {
            using var dir = new Temp();
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            var work = ws.AddSpace("Work");
            var view = new SpaceView(ws);
            var store = new SnippetStore(dir.Path);
            var notes = new NotesView();
            notes.Initialize(ws, view, null!, store, new Host());
            var editor = (TextBox)notes.FindName("Editor");
            editor.IsReadOnly = true; // so a slip could never paste this PC's real clipboard into the test
            using var window = Ui.Host(notes);

            var clip = Clip(("PNG", Stream(Png(Picture(120, 90)))));
            notes.ReadClipboard = () => clip;
            ApplicationCommands.Paste.Execute(null, editor);
            Check.Equal(1, store.Of(ws.ActiveSheet.Id).Count, "the menu's Paste adds a snippet");

            // Blank paper in a space: the paste makes the sheet, in that space.
            view.Show(work.Id);
            Ui.Pump();
            Check.True(view.ActiveSheet == null, "blank paper");
            ApplicationCommands.Paste.Execute(null, editor);
            Ui.Pump();
            var made = view.ActiveSheet;
            Check.True(made != null && made.Space == work.Id && made.Text == "", "a sheet made in Work, with no text");
            Check.Equal(1, store.Of(made!.Id).Count, "holding the snippet");
            Check.Equal(Visibility.Visible, ((FrameworkElement)notes.FindName("SnippetScroller")).Visibility, "on show");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void FromTasks()
    {
        Ui.Run(errors =>
        {
            using var dir = new Temp();
            var ws = new Workspace(new Settings(), new Hlc("desk0001"), WorkspaceData.Empty);
            var store = new SnippetStore(dir.Path);
            var host = new Host();
            var notes = Notes(ws, store, host);
            using var window = Ui.Host(notes);

            var found = SnippetImage.FromPaste(Clip(("PNG", Stream(Png(Picture(120, 90))))));
            notes.Keep(found, fromTasks: true);
            Check.Equal("Snippet added to Notes", host.Toasts.LastOrDefault(), "a toast says where it went");
            Check.Equal(1, store.Count, "kept");
            var file = store.PathOf(store.Of(ws.ActiveSheet.Id)[0]);
            host.Undo();
            Check.Equal(0, store.Count, "Undo takes it off");
            Check.True(!File.Exists(file), "and its file goes then and there");

            notes.Keep(new PastedImages([], PasteProblem.TooBig), fromTasks: true);
            Check.Equal("That image is too big to paste", host.Toasts.LastOrDefault(), "one that can't be kept says why");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void ViewerFits()
    {
        Ui.Run(errors =>
        {
            // This PC's main display: 2560x1440 at 125%, taskbar 60 pixels.
            var work = new Int32Rect(0, 0, 2560, 1380);
            var (bounds, box) = SnippetViewer.Layout(work, 1.25);
            Check.Equal(new Int32Rect(128, 69, 2304, 1242), bounds, "nine tenths of the work area, centred");
            Check.Equal((2284, 1222), box, "less the room around the image");

            using var dir = new Temp();
            string small = Path.Combine(dir.Path, "small.png"), large = Path.Combine(dir.Path, "large.png");
            File.WriteAllBytes(small, Png(Picture(900, 500)));
            File.WriteAllBytes(large, Png(Picture(4480, 1440)));
            var one = SnippetImage.Load(small, box)!;
            Check.Equal((900, 500), (one.PixelWidth, one.PixelHeight), "a snip that fits: pixel for pixel");
            var fitted = SnippetImage.Load(large, box)!;
            Check.True(fitted.PixelWidth <= box.Item1 && fitted.PixelHeight <= box.Item2 && fitted.PixelWidth >= box.Item1 - 1, $"both screens at once: scaled to fit ({fitted.PixelWidth}x{fitted.PixelHeight})");
            Check.True(SnippetImage.Load(Path.Combine(dir.Path, "missing.png"), box) == null, "a missing file: nothing (the panel says it couldn't open it)");

            foreach (bool dark in new[] { false, true })
            {
                void Look()
                {
                    var viewer = SnippetViewer.Create(one, 1.25, "Snippet pasted at 09:05");
                    viewer.Left = -32000;
                    viewer.Top = -32000;
                    viewer.ShowActivated = false;
                    viewer.SizeToContent = SizeToContent.WidthAndHeight; // as Open sizes it: the image and its room
                    viewer.Show();
                    Ui.Pump();
                    Check.True(Math.Abs(viewer.Image.ActualWidth - 900 / 1.25) < 0.5, $"drawn at its own pixels ({viewer.Image.ActualWidth} DIPs)");
                    Shots.Save((FrameworkElement)viewer.Content, dark ? "viewer-dark" : "viewer-light");
                    viewer.Close();
                    Ui.Pump();
                    Check.True(viewer.Image.Source == null, "closing lets go of the image");
                }
                if (dark) Shots.Dark(Look);
                else Look();
            }
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    // ================= Helpers =================

    /// <summary>The panel, as Notes sees it: toasts with Undo that lapse, announcements, the viewer.</summary>
    internal sealed class Host : INotesHost
    {
        private readonly UndoSlot _slot = new();

        public List<string> Toasts { get; } = new();
        public List<string> Announced { get; } = new();
        public List<(string Path, Snippet Snippet)> Viewed { get; } = new();
        public bool CanUndo => _slot.CanUndo;

        public void ShowToast(string message, Action? undo = null, Action? lapsed = null)
        {
            _slot.Hold(undo, lapsed);
            Toasts.Add(message);
        }

        public void Announce(string text) => Announced.Add(text);

        public void ViewSnippet(string path, Snippet snippet) => Viewed.Add((path, snippet));

        public void Undo() => _slot.Take()?.Invoke();

        public void Lapse() => _slot.Lapse();

        public void EndUndo() => _slot.Lapse();
    }

    internal static NotesView Notes(Workspace ws, SnippetStore store, INotesHost host)
    {
        var notes = new NotesView();
        notes.Initialize(ws, new SpaceView(ws), null!, store, host);
        return notes;
    }

    /// <summary>The paper is see-through (it sits on glass): a solid surface under it for looking at.</summary>
    private static Border Surface(FrameworkElement content)
    {
        var surface = new Border { Child = content, Padding = new Thickness(0, 8, 0, 0) };
        surface.SetResourceReference(Border.BackgroundProperty, "SolidSurfaceBrush");
        return surface;
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    internal static SnippetPng One(IDataObject data)
    {
        var found = SnippetImage.FromPaste(data);
        Check.Equal(PasteProblem.None, found.Problem, "an image");
        return found.Images.Single();
    }

    internal static DateTimeOffset At(int hour, int minute) => new(2026, 10, 24, hour, minute, 0, TimeSpan.Zero);

    /// <summary>
    /// Data as the clipboard hands it over: Clipboard.GetDataObject() wraps the clipboard's COM data object,
    /// so every format comes through FORMATETC and global memory. This one wraps a data object of our own the
    /// same way, so this PC's real clipboard is never touched.
    /// </summary>
    internal static IDataObject Clip(params (string Format, object Data)[] items) => new DataObject(new ComOnly(Plain(items)));

    /// <summary>Data as an object in this process holds it (no COM in between).</summary>
    internal static DataObject Plain(params (string Format, object Data)[] items)
    {
        var data = new DataObject();
        foreach (var (format, value) in items) data.SetData(format, value, autoConvert: false);
        return data;
    }

    internal static MemoryStream Stream(byte[] bytes) => new(bytes);

    /// <summary>Only the COM face of a data object, as the clipboard's own is.</summary>
    private sealed class ComOnly(ComDataObject inner) : ComDataObject
    {
        public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink sink, out int connection) => inner.DAdvise(ref format, advf, sink, out connection);
        public void DUnadvise(int connection) => inner.DUnadvise(connection);
        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) => inner.EnumDAdvise(out enumAdvise);
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => inner.EnumFormatEtc(direction);
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) => inner.GetCanonicalFormatEtc(ref formatIn, out formatOut);
        public void GetData(ref FORMATETC format, out STGMEDIUM medium) => inner.GetData(ref format, out medium);
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => inner.GetDataHere(ref format, ref medium);
        public int QueryGetData(ref FORMATETC format) => inner.QueryGetData(ref format);
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => inner.SetData(ref formatIn, ref medium, release);
    }

    /// <summary>
    /// A picture like a snip of instructions: pale with dark lines of "text", red in the top-left corner and
    /// blue in the bottom-right (so a picture upside down shows).
    /// </summary>
    internal static BitmapSource Picture(int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                (byte b, byte g, byte r) = x < width / 4 && y < height / 4 ? ((byte)0, (byte)0, (byte)255)
                    : x >= width * 3 / 4 && y >= height * 3 / 4 ? ((byte)255, (byte)0, (byte)0)
                    : y % 14 is >= 4 and < 7 && x % 90 < 70 ? ((byte)70, (byte)60, (byte)50)
                    : ((byte)246, (byte)244, (byte)240);
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    internal static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static byte[] Jpeg(BitmapSource bitmap)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A DIB as the clipboard has one: BITMAPINFOHEADER (bit-field masks after it if asked), then the rows.</summary>
    internal static byte[] Dib(BitmapSource picture, int bits, bool topDown = false, bool zeroAlpha = false, bool bitFields = false)
    {
        int w = picture.PixelWidth, h = picture.PixelHeight;
        var src = new byte[w * h * 4];
        new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0).CopyPixels(src, w * 4, 0);
        int stride = (w * bits + 31) / 32 * 4;
        int masks = bitFields ? 12 : 0;
        var dib = new byte[40 + masks + stride * h];
        using (var bw = new BinaryWriter(new MemoryStream(dib)))
        {
            bw.Write(40);
            bw.Write(w);
            bw.Write(topDown ? -h : h);
            bw.Write((short)1);
            bw.Write((short)bits);
            bw.Write(bitFields ? 3 : 0);
            bw.Write(stride * h);
            bw.Write(3780);
            bw.Write(3780);
            bw.Write(0);
            bw.Write(0);
            if (bitFields)
            {
                bw.Write(0x00FF0000);
                bw.Write(0x0000FF00);
                bw.Write(0x000000FF);
            }
        }
        for (int row = 0; row < h; row++)
        {
            int y = topDown ? row : h - 1 - row;
            int at = 40 + masks + row * stride;
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4;
                dib[at + x * (bits / 8)] = src[s];
                dib[at + x * (bits / 8) + 1] = src[s + 1];
                dib[at + x * (bits / 8) + 2] = src[s + 2];
                if (bits == 32) dib[at + x * 4 + 3] = zeroAlpha ? (byte)0 : (byte)255;
            }
        }
        return dib;
    }

    /// <summary>
    /// A PNG made by hand: <paramref name="width"/> by <paramref name="height"/> one-bit grey, all black (so even a
    /// vast one is a few KB), or with the image data given.
    /// </summary>
    internal static byte[] CraftPng(int width, int height, byte[]? idat = null)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 1; // bit depth
        ihdr[9] = 0; // greyscale
        Chunk(png, "IHDR", ihdr);
        if (idat == null)
        {
            using var raw = new MemoryStream();
            using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var row = new byte[1 + (width + 7) / 8];
                for (int y = 0; y < height; y++) z.Write(row);
            }
            idat = raw.ToArray();
        }
        Chunk(png, "IDAT", idat);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(body);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
        png.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    internal static BitmapSource Decode(byte[] png) =>
        new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];

    internal static Color Pixel(BitmapSource image, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    internal sealed class Temp : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("tack-snippets-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// Off-screen renders for looking at, written only when TACK_SHOTS names a folder (never during a normal run).
/// </summary>
internal static class Shots
{
    public static void Save(object target, string name, double scale = 1.25)
    {
        if (Environment.GetEnvironmentVariable("TACK_SHOTS") is not { Length: > 0 } dir) return;
        var element = target is Window window ? (FrameworkElement)window.Content : (FrameworkElement)target;
        element.UpdateLayout();
        int w = (int)Math.Ceiling(element.ActualWidth * scale), h = (int)Math.Ceiling(element.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(dir);
        using var file = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(file);
    }

    /// <summary>Runs <paramref name="look"/> in the dark palette, then goes back to the light one.</summary>
    public static void Dark(Action look)
    {
        var palettes = Application.Current.Resources.MergedDictionaries;
        palettes[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/Tack;component/Themes/Dark.xaml") };
        try
        {
            Ui.Pump();
            look();
        }
        finally
        {
            palettes[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/Tack;component/Themes/Light.xaml") };
            Ui.Pump();
        }
    }
}
