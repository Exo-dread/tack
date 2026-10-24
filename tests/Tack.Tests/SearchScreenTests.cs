using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tack.Model;
using Tack.Services;
using Tack.Sync;
using Tack.Views;

namespace Tack.Tests;

/// <summary>
/// Search on screen (Ctrl+F), in this process: the panel's own header markup and the results view,
/// shown far off screen in windows that are never activated (no input goes anywhere else). With
/// TACK_SHOTS set to a folder, it also saves pictures of search open, light and dark, at 125%.
/// </summary>
internal static class SearchScreenTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("search on screen: the box sits over the tabs, inside the header, and opening it moves nothing else", BoxInHeader);
        yield return ("search on screen: results grouped Tasks, Done, Notes, each with its space while there are spaces, the match picked out", Grouped);
        yield return ("search on screen: arrows choose a row (staying at either end), Enter opens it; no space labels with only Main", Keys);
        yield return ("search on screen: before typing a hint, when nothing matches a hint that says so", Hints);
        yield return ("search on screen: results are a named list of named groups of named items, for screen readers", Automation);
        yield return ("search on screen: a group draws 50 rows and Show more the rest, quickly, over 10,000 tasks and 500 long notes", ShowMore);
        yield return ("search on screen: opening goes to the space of what's found (unless All shows it), its tab, and Done for a finished task", Plan);
        yield return ("search on screen: a task found is scrolled to, lit up, then back as it was, with the caret in it", RevealTask);
        yield return ("search on screen: a finished task found is in sight in Done", RevealDone);
        yield return ("search on screen: a sheet found opens with the match selected and in sight", RevealNote);
        yield return ("search on screen: Settings lists Ctrl+F among the shortcuts", SettingsLists);
    }

    private static JsonElement SharedSearch() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.Root, "tests", "shared", "search.json"))).RootElement.GetProperty("search");

    private static SearchView Results(Workspace ws, SpaceView view, string query)
    {
        var search = new SearchView();
        search.Initialize(ws, view);
        search.SetQuery(query);
        search.Flush();
        return search;
    }

    // ---------------------------------------------------------------- the box

    /// <summary>The panel's header (PanelWindow.xaml), with the panel's own styles and without its handlers, to lay out on its own.</summary>
    internal static Grid Header(double width)
    {
        string xaml = File.ReadAllText(Path.Combine(Program.Root, "src", "Tack", "Windows", "PanelWindow.xaml"));
        string resources = Element(xaml, "<Window.Resources>", "Window.Resources");
        resources = resources["<Window.Resources>".Length..^"</Window.Resources>".Length];
        string header = Element(xaml, "<Grid x:Name=\"Header\"", "Grid");
        header = Regex.Replace(header, @"\s(Click|Checked|Unchecked|MouseLeftButtonDown|TextChanged|PreviewKeyDown|LostKeyboardFocus|PreviewMouseWheel|ScrollChanged)=""[^""]*""", "");
        return (Grid)XamlReader.Parse(
            $"<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
            $"Width=\"{width}\" Height=\"48\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\">" +
            $"<Grid.Resources>{resources}</Grid.Resources>{header}</Grid>");
    }

    /// <summary>An element of the markup, from its opening tag to its own closing tag (elements of the same name nested inside it included).</summary>
    private static string Element(string xaml, string start, string tag)
    {
        int from = xaml.IndexOf(start, StringComparison.Ordinal);
        Check.True(from >= 0, $"{start} is in PanelWindow.xaml");
        int depth = 0;
        var tags = new Regex($@"<{Regex.Escape(tag)}[\s>]|</{Regex.Escape(tag)}>");
        for (var m = tags.Match(xaml, from); m.Success; m = m.NextMatch())
        {
            if (m.Value.StartsWith("</", StringComparison.Ordinal))
            {
                if (--depth == 0) return xaml[from..(m.Index + m.Length)];
            }
            else
            {
                // An element that closes itself (<Grid ... />) opens nothing.
                int end = xaml.IndexOf('>', m.Index);
                if (xaml[end - 1] != '/') depth++;
            }
        }
        throw new TestFailure($"{start}: no end");
    }

    private static Rect Bounds(FrameworkElement e, Visual root) => e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize));

    private static void BoxInHeader()
    {
        Ui.Run(errors =>
        {
            foreach (double width in new[] { Settings.DefaultWidth, Settings.MinWidth, 520.0 })
            {
                var header = Header(width);
                using var window = Ui.Host(header);
                T Named<T>(string name) where T : FrameworkElement => (T)header.FindName(name);
                var bar = Named<Grid>("SearchBar");
                var tabs = Named<StackPanel>("Tabs");
                string[] others = ["TasksTab", "NotesTab", "PinButton", "SettingsButton", "CollapseButton"];
                Check.Equal(Visibility.Collapsed, bar.Visibility, $"{width}: not there until it's opened");
                var before = others.Select(n => Bounds(Named<FrameworkElement>(n), header)).ToList();

                tabs.Visibility = Visibility.Hidden;
                bar.Visibility = Visibility.Visible;
                Ui.Pump();
                var after = others.Select(n => Bounds(Named<FrameworkElement>(n), header)).ToList();
                for (int i = 0; i < others.Length; i++) Check.Equal(before[i], after[i], $"{width}: {others[i]} stays where it was");

                var box = Bounds(bar, header);
                var pin = Bounds(Named<FrameworkElement>("PinButton"), header);
                Check.True(box.Left >= before[0].Left - 0.5 && box.Right <= pin.Left + 0.5, $"{width}: between the brand and the buttons ({box})");
                Check.True(box.Top >= 4 && box.Bottom <= 44 && Math.Abs(box.Top + box.Height / 2 - 24) < 1, $"{width}: centred in the header ({box})");
                Check.True(box.Width >= (width > Settings.MinWidth ? 190 : 120), $"{width}: room to type ({box.Width:F0})");
                var count = Named<TextBlock>("SearchCount");
                count.Text = "1,234";
                Ui.Pump();
                var c = Bounds(count, header);
                Check.True(c.Right <= box.Right && c.Left >= box.Left + box.Width / 2, $"{width}: the count inside the box, at its end ({c})");

                // Closed again: as it was.
                bar.Visibility = Visibility.Collapsed;
                tabs.Visibility = Visibility.Visible;
                Ui.Pump();
                var again = others.Select(n => Bounds(Named<FrameworkElement>(n), header)).ToList();
                for (int i = 0; i < others.Length; i++) Check.Equal(before[i], again[i], $"{width}: {others[i]} back as it was");
            }
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    // ---------------------------------------------------------------- results

    private static void Grouped()
    {
        Ui.Run(errors =>
        {
            var ws = SearchTests.SharedWorkspace(SharedSearch());
            var view = new SpaceView(ws);
            var search = Results(ws, view, "clash");
            using var window = Ui.Host(search);
            var rows = search.Rows;
            Check.Sequence([SearchRowKind.Task, SearchRowKind.Task, SearchRowKind.Done, SearchRowKind.Note, SearchRowKind.Note], rows.Select(r => r.Kind).ToList(), "tasks, done, notes");
            Check.Sequence(["TASKS · 2", "TASKS · 2", "DONE · 1", "NOTES · 2", "NOTES · 2"], rows.Select(r => r.Group).ToList(), "their groups");
            Check.Sequence(["Now · Work", "Work", "Dev", "Work", "Dev"], rows.Select(r => r.Meta).ToList(), "each with its space; the task in focus says so");
            Check.Equal(new Excerpt("Send the ", "clash", " report to the MEP team"), rows[1].Line, "a task: the match picked out");
            Check.Equal(new Excerpt("Coordination meeting", "", ""), rows[3].Line, "a sheet: its title");
            Check.Equal(new Excerpt("- ", "clash", " list: Friday"), rows[3].Second, "and the line that matches");
            Check.Equal(new Excerpt("", "CLASH", " of the titans (film)"), rows[4].Line, "a match in the title: the title, picked out");
            Check.True(rows[4].Second == null, "and nothing under it");
            Check.True(search.Current == rows[0], "the first row chosen");

            // On screen: the group headings, and every row's text inside the view.
            var list = (ListBox)search.FindName("Results");
            Check.Equal(Visibility.Visible, list.Visibility, "the list shows");
            var headings = Ui.Descendants<GroupItem>(list).Select(g => (string)((CollectionViewGroup)g.DataContext).Name).ToList();
            Check.Sequence(["TASKS · 2", "DONE · 1", "NOTES · 2"], headings, "group headings");
            foreach (var text in Ui.Descendants<TextBlock>(list).Where(t => t.IsVisible && t.ActualWidth > 0))
            {
                var r = Bounds(text, list);
                Check.True(r.Right <= list.ActualWidth + 0.5, $"'{text.Text}' inside the results ({r.Right:F1} of {list.ActualWidth:F1})");
            }
            Shots(ws, view, "clash");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void Keys()
    {
        Ui.Run(errors =>
        {
            var ws = new Workspace(new Settings(), new Hlc("search05"), WorkspaceData.Empty);
            ws.AddTask("Third report");
            ws.AddTask("Second report");
            ws.AddTask("First report", asNow: true);
            ws.AddSheet(null, "Report template\r\n- intro");
            var view = new SpaceView(ws);
            var search = Results(ws, view, "REPORT");
            using var window = Ui.Host(search);
            Check.Sequence(["Now", "", "", ""], search.Rows.Select(r => r.Meta).ToList(), "no space labels with only Main; Now still says so");
            Check.True(search.Current == search.Rows[0], "the first chosen");
            search.Move(-1);
            Check.True(search.Current == search.Rows[0], "Up at the top stays there");
            search.Move(1);
            search.Move(1);
            Check.True(search.Current == search.Rows[2], "Down twice: the third");
            search.Move(8);
            Check.True(search.Current == search.Rows[3], "Page Down stops at the last");
            SearchHit? opened = null;
            string? query = null;
            search.Opened += (hit, q) => (opened, query) = (hit, q);
            Check.True(search.OpenCurrent(), "Enter opens it");
            Check.True(opened?.Item == ws.Sheets.Last(), "the sheet");
            Check.Equal("REPORT", query, "with the query it was found by");

            // Typed but not searched yet: Enter searches first, so it never opens a stale row.
            search.SetQuery("third");
            Check.True(search.OpenCurrent(), "Enter straight after typing");
            Check.Equal("Third report", ((TaskItem)opened!.Item).Text, "opens what the new query finds");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void Hints()
    {
        Ui.Run(errors =>
        {
            var ws = new Workspace(new Settings(), new Hlc("search06"), WorkspaceData.Empty);
            ws.AddTask("Check the clash report");
            var view = new SpaceView(ws);
            var search = Results(ws, view, "");
            using var window = Ui.Host(search);
            var hint = (FrameworkElement)search.FindName("Hint");
            var title = (TextBlock)search.FindName("HintTitle");
            var text = (TextBlock)search.FindName("HintText");
            var list = (FrameworkElement)search.FindName("Results");
            Check.Equal(Visibility.Visible, hint.Visibility, "before typing: the hint");
            Check.Equal(Visibility.Collapsed, list.Visibility, "and no list");
            Check.Equal("Search tasks and notes", title.Text, "what search does");
            Check.True(!text.Text.Contains("space", StringComparison.Ordinal), $"with only Main, nothing about spaces ({text.Text})");
            ws.AddSpace("Work");
            search.Clear();
            Ui.Pump();
            Check.True(text.Text.Contains("every space", StringComparison.Ordinal), $"with spaces: every space ({text.Text})");

            search.SetQuery("zebra crossing");
            search.Flush();
            Ui.Pump();
            Check.Equal(Visibility.Visible, hint.Visibility, "nothing found: the hint");
            Check.Equal("Nothing matches “zebra crossing”", title.Text, "saying so");
            search.SetQuery("clash");
            search.Flush();
            Ui.Pump();
            Check.Equal(Visibility.Collapsed, hint.Visibility, "found: no hint");
            Check.Equal(Visibility.Visible, list.Visibility, "the list");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void Automation()
    {
        Ui.Run(errors =>
        {
            var ws = SearchTests.SharedWorkspace(SharedSearch());
            var view = new SpaceView(ws);
            var search = Results(ws, view, "clash");
            string? counted = null, chosen = null;
            search.Counted += t => counted = t;
            search.Chosen += t => chosen = t;
            search.SetQuery("clash ");
            search.Flush();
            using var window = Ui.Host(search);
            Check.Equal("5 results", counted, "the count, for screen readers");
            Check.Equal("In focus: Review the coordination clashes, in Work", chosen, "and the row chosen");
            search.Move(2);
            Check.Equal("Done: Clash detection in Navisworks, in Dev", chosen, "as it moves");

            var list = (ListBox)search.FindName("Results");
            var peer = UIElementAutomationPeer.CreatePeerForElement(list);
            Check.Equal(AutomationControlType.List, peer.GetAutomationControlType(), "a list");
            Check.Equal("Results", peer.GetName(), "named");
            var groups = peer.GetChildren() ?? [];
            Check.Sequence(["TASKS · 2", "DONE · 1", "NOTES · 2"], groups.Select(g => g.GetName()).ToList(), "of named groups");
            Check.True(groups.All(g => g.GetAutomationControlType() == AutomationControlType.Group), "groups");
            var items = groups.SelectMany(g => g.GetChildren() ?? []).ToList();
            Check.True(items.All(i => i.GetAutomationControlType() == AutomationControlType.ListItem), "of list items");
            Check.Sequence(
                ["In focus: Review the coordination clashes, in Work", "Send the clash report to the MEP team, in Work", "Done: Clash detection in Navisworks, in Dev",
                 "Note Coordination meeting: - clash list: Friday, in Work", "Note: CLASH of the titans (film), in Dev"],
                items.Select(i => i.GetName()).ToList(), "named for what they are");
            var selected = items.Where(i => i.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider { IsSelected: true }).ToList();
            Check.Equal(1, selected.Count, "one of them selected");
            Check.Equal("Done: Clash detection in Navisworks, in Dev", selected[0].GetName(), "the one chosen");

            search.SetQuery("nothing like it");
            search.Flush();
            Check.Equal("No results", counted, "nothing found: said so");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void ShowMore()
    {
        Ui.Run(errors =>
        {
            var ws = SearchTests.BigWorkspace();
            var view = new SpaceView(ws);
            var search = new SearchView();
            search.Initialize(ws, view);
            using var window = Ui.Host(search);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            search.SetQuery("task number");
            search.Flush();
            Ui.Pump();
            long firstMs = clock.ElapsedMilliseconds;
            Check.Equal(SearchView.FirstRows + 1, search.Rows.Count, "fifty, and Show more");
            var more = search.Rows[^1];
            Check.Equal(SearchRowKind.More, more.Kind, "Show more last");
            Check.Equal($"Show {SearchView.MoreRows} more (9,950 left)", more.Line.Before, "saying how many");
            Check.Equal("TASKS · 10,000", more.Group, "in the group, which counts them all");

            search.Move(100);
            Check.True(search.Current == more, "Show more can be chosen");
            clock.Restart();
            search.OpenCurrent();
            Ui.Pump();
            long moreMs = clock.ElapsedMilliseconds;
            Check.Equal(SearchView.FirstRows + SearchView.MoreRows + 1, search.Rows.Count, "Enter on it: as many again");
            Check.Equal(SearchRowKind.Task, search.Current?.Kind, "a new row chosen");
            Check.True(search.Current == search.Rows[SearchView.FirstRows], "the one where Show more was");

            // Every keystroke of a query over all of it, laid out each time.
            clock.Restart();
            foreach (string q in new[] { "f", "fa", "fac", "faca", "facad", "facade" })
            {
                search.SetQuery(q);
                search.Flush();
                Ui.Pump();
            }
            double perKey = clock.ElapsedMilliseconds / 6.0;
            Check.Equal(SearchView.FirstRows, search.Rows.Count(r => r.Kind == SearchRowKind.Note), "notes: fifty");
            // Generous budgets: they catch something accidentally slow, not small slowdowns.
            Check.True(firstMs < 4000, $"the first search, folding everything ({firstMs} ms)");
            Check.True(moreMs < 1500, $"Show more ({moreMs} ms)");
            Check.True(perKey < 400, $"each keystroke after that ({perKey:F0} ms)");
            Console.WriteLine($"        (first search {firstMs} ms, Show more {moreMs} ms, a keystroke {perKey:F0} ms)");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void Plan()
    {
        var ws = new Workspace(new Settings(), new Hlc("search07"), WorkspaceData.Empty);
        var plain = ws.AddTask("Main's own");
        var view = new SpaceView(ws);
        Check.Equal(((string?)null, "tasks", false), SearchView.Plan(view, ws.Settings, plain), "only Main: nothing to switch");

        var work = ws.AddSpace("Work");
        var dev = ws.AddSpace("Dev");
        var inDev = ws.AddTask("Fix the build", space: dev.Id);
        var inWork = ws.AddTask("Send the model", space: work.Id);
        var finished = ws.AddTask("Old one", space: dev.Id);
        ws.Complete(finished);
        var sheet = ws.AddSheet(dev.Id, "Dev notes");
        view.Show(work.Id);
        Check.Equal(((string?)dev.Id, "tasks", false), SearchView.Plan(view, ws.Settings, inDev), "from Work, a task in Dev: Dev");
        Check.Equal(((string?)null, "tasks", false), SearchView.Plan(view, ws.Settings, inWork), "a task in Work: stays");
        Check.Equal(((string?)SpaceField.Main, "tasks", false), SearchView.Plan(view, ws.Settings, plain), "a task in Main: Main");
        Check.Equal(((string?)dev.Id, "tasks", true), SearchView.Plan(view, ws.Settings, finished), "a finished one: Done unfolds");
        ws.Settings.DoneExpanded = true;
        Check.Equal(((string?)dev.Id, "tasks", false), SearchView.Plan(view, ws.Settings, finished), "already unfolded");
        Check.Equal(((string?)dev.Id, "notes", false), SearchView.Plan(view, ws.Settings, sheet), "a sheet: Notes, in its space");
        view.Show(SpaceView.All);
        Check.Equal(((string?)null, "tasks", false), SearchView.Plan(view, ws.Settings, inDev), "All shows everything: stays");
        Check.Equal(((string?)null, "notes", false), SearchView.Plan(view, ws.Settings, sheet), "a sheet too");

        Check.True(SearchView.IsLive(ws, inDev) && SearchView.IsLive(ws, finished) && SearchView.IsLive(ws, sheet), "all there");
        ws.Delete(inDev);
        ws.DeleteSheet(sheet);
        Check.True(!SearchView.IsLive(ws, inDev) && !SearchView.IsLive(ws, sheet), "deleted meanwhile: nothing to open");
    }

    // ---------------------------------------------------------------- going there

    private static void RevealTask()
    {
        Ui.Run(errors =>
        {
            var ws = new Workspace(new Settings(), new Hlc("search08"), WorkspaceData.Empty);
            for (int i = 300; i >= 1; i--) ws.AddTask($"Task {i}");
            var target = ws.Open[249];
            var view = new SpaceView(ws);
            var tasks = new TasksView();
            tasks.Initialize(ws, view, null!);
            using var window = Ui.Host(tasks);
            var scroller = (ScrollViewer)tasks.FindName("Scroller");
            Check.Equal(0.0, scroller.VerticalOffset, "at the top");

            tasks.Reveal(target);
            Ui.Pump();
            var list = (ItemsControl)tasks.FindName("OpenList");
            var container = (ContentPresenter)list.ItemContainerGenerator.ContainerFromItem(target);
            var at = Bounds(container, scroller);
            Check.True(scroller.VerticalOffset > 0, "scrolled");
            Check.True(at.Top >= -0.5 && at.Bottom <= scroller.ViewportHeight + 0.5, $"the task in sight ({at.Top:F0}..{at.Bottom:F0} of {scroller.ViewportHeight:F0})");
            var row = (Border)container.ContentTemplate.FindName("Row", container);
            var editor = (TextBox)container.ContentTemplate.FindName("Editor", container);
            Check.True(editor.IsFocused, "the caret in it");
            Check.Equal(editor.Text.Length, editor.CaretIndex, "at the end");
            var lit = row.Background as SolidColorBrush;
            Check.True(lit != null && lit.Color == ((SolidColorBrush)row.FindResource("AccentSoftBrush")).Color, "lit up");

            Ui.WaitUntil(() => row.ReadLocalValue(Border.BackgroundProperty) == DependencyProperty.UnsetValue, (int)TasksView.LitFor.TotalMilliseconds + 2000);
            Check.True(row.ReadLocalValue(Border.BackgroundProperty) == DependencyProperty.UnsetValue, "then back as it was");
            Check.True(row.ReadLocalValue(Border.BorderBrushProperty) == DependencyProperty.UnsetValue, "its border too");

            // The task in focus: its card lights up and goes back to its own look.
            ws.SetNow(ws.Open[10]);
            Ui.Pump();
            var card = (Border)tasks.FindName("NowCard");
            var fill = ((SolidColorBrush)card.FindResource("AccentFaintBrush")).Color;
            var edge = ((SolidColorBrush)card.FindResource("AccentSoftBrush")).Color;
            tasks.Reveal(ws.Now!);
            Ui.Pump();
            Check.True(((TextBox)tasks.FindName("NowEditor")).IsFocused, "the caret in the card");
            Check.True(((SolidColorBrush)card.Background).Color != fill, "the card lit up");
            Ui.WaitUntil(() => card.Background is SolidColorBrush { Opacity: 1 } b && b.Color == fill, (int)TasksView.LitFor.TotalMilliseconds + 2000);
            Check.Equal(fill, ((SolidColorBrush)card.Background).Color, "the card's own fill again");
            Check.Equal(edge, ((SolidColorBrush)card.BorderBrush).Color, "and its own border");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void RevealDone()
    {
        Ui.Run(errors =>
        {
            var ws = new Workspace(new Settings(), new Hlc("search09"), WorkspaceData.Empty);
            for (int i = 60; i >= 1; i--) ws.AddTask($"Task {i}");
            var finished = ws.AddTask("Finished long ago");
            ws.Complete(finished);
            var view = new SpaceView(ws);
            var tasks = new TasksView();
            tasks.Initialize(ws, view, null!);
            using var window = Ui.Host(tasks);
            ws.Settings.DoneExpanded = true; // as opening it does (see Plan)
            tasks.Reveal(finished);
            Ui.Pump();
            var scroller = (ScrollViewer)tasks.FindName("Scroller");
            var done = (ItemsControl)tasks.FindName("DoneList");
            var container = (FrameworkElement)done.ItemContainerGenerator.ContainerFromItem(finished);
            Check.True(container != null && container.IsVisible, "Done shows it");
            var at = Bounds(container!, scroller);
            Check.True(at.Top >= -0.5 && at.Bottom <= scroller.ViewportHeight + 0.5, $"in sight ({at.Top:F0}..{at.Bottom:F0} of {scroller.ViewportHeight:F0})");
            Check.True(((TextBox)tasks.FindName("AddBox")).IsFocused, "the caret in the add box (a finished task has no text box)");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void RevealNote()
    {
        Ui.Run(errors =>
        {
            var ws = new Workspace(new Settings(), new Hlc("search10"), WorkspaceData.Empty);
            var lines = Enumerable.Range(1, 300).Select(i => i == 250 ? "- level 3 ducts vs beams (Crème)" : $"Line {i} of the coordination notes");
            var sheet = ws.AddSheet(null, "Coordination\n" + string.Join("\n", lines));
            ws.AddSheet(null, "Another sheet");
            var view = new SpaceView(ws);
            var notes = new NotesView();
            notes.Initialize(ws, view, null!);
            using var window = Ui.Host(notes);
            var editor = (TextBox)notes.FindName("Editor");

            view.ActiveSheet = sheet;
            var match = Search.FindIn(sheet.Text, "ducts VS beams (creme)");
            Check.True(match != null, "found");
            notes.Reveal(sheet, match);
            Ui.Pump();
            Check.True(editor.IsFocused, "the caret in the paper");
            Check.Equal("ducts vs beams (Crème)", editor.SelectedText, "the match selected, as typed");
            var at = editor.GetRectFromCharacterIndex(editor.SelectionStart);
            Check.True(!at.IsEmpty && at.Top >= 0 && at.Bottom <= editor.ActualHeight, $"in sight ({at.Top:F0} of {editor.ActualHeight:F0})");
            Check.True(at.Top < editor.ActualHeight * 0.6, $"towards the top, with what follows below it ({at.Top:F0})");
            Check.Equal(0, errors.Count, "no error\n" + string.Join("\n", errors));
        });
    }

    private static void SettingsLists()
    {
        string xaml = File.ReadAllText(Path.Combine(Program.Root, "src", "Tack", "Views", "SettingsView.xaml"));
        int shortcut = xaml.IndexOf("Text=\"Shortcut\"", StringComparison.Ordinal);
        int next = xaml.IndexOf("Style=\"{StaticResource SectionLabel}\"", shortcut + 40, StringComparison.Ordinal);
        Check.True(shortcut >= 0 && next > shortcut, "Settings has its Shortcut card");
        string card = xaml[shortcut..next];
        Check.True(card.Contains("Text=\"Search tasks and notes\"", StringComparison.Ordinal) && card.Contains("Text=\"Ctrl+F\"", StringComparison.Ordinal),
            "in the Shortcut card: Search tasks and notes, Ctrl+F");
    }

    // ---------------------------------------------------------------- pictures

    /// <summary>With TACK_SHOTS set: search open over a panel-sized surface (header and results), light and dark, at 125%.</summary>
    private static void Shots(Workspace ws, SpaceView view, string query)
    {
        string? folder = Environment.GetEnvironmentVariable("TACK_SHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        var palettes = Application.Current.Resources.MergedDictionaries;
        var light = palettes[0];
        try
        {
            foreach (string theme in new[] { "light", "dark" })
            {
                palettes[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Tack;component/Themes/{(theme == "light" ? "Light" : "Dark")}.xaml") };
                foreach (double width in new[] { Settings.DefaultWidth, Settings.MinWidth })
                {
                    var header = Header(width);
                    ((FrameworkElement)header.FindName("Tabs")).Visibility = Visibility.Hidden;
                    ((FrameworkElement)header.FindName("SearchBar")).Visibility = Visibility.Visible;
                    ((FrameworkElement)header.FindName("TabIndicator")).Opacity = 0;
                    ((TextBox)header.FindName("SearchBox")).Text = query;
                    var search = Results(ws, view, query);
                    ((TextBlock)header.FindName("SearchCount")).Text = search.LastResults.Total.ToString("N0");
                    header.Width = double.NaN;
                    header.HorizontalAlignment = HorizontalAlignment.Stretch;
                    var surface = new Grid { Width = width, Height = 400, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                    surface.SetResourceReference(Panel.BackgroundProperty, "SolidSurfaceBrush");
                    surface.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    TextElement.SetFontFamily(surface, (FontFamily)surface.FindResource("UiFont"));
                    TextElement.SetFontSize(surface, 13.5);
                    AccentPalette.Apply(surface.Resources, AccentPalette.Resolve("#60CDFF", theme == "light"));
                    surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
                    surface.RowDefinitions.Add(new RowDefinition());
                    surface.Children.Add(header);
                    Grid.SetRow(search, 1);
                    surface.Children.Add(search);
                    using (Ui.Host(surface))
                    {
                        search.Refresh(); // drawn for the width it has now
                        Ui.Pump();
                        Save(surface, Path.Combine(folder, $"search-{theme}-{width:F0}.png"));
                    }
                }
            }
        }
        finally
        {
            palettes[0] = light;
        }
    }

    internal static void Save(FrameworkElement element, string path, double scale = 1.25)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }
}
