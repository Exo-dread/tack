using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Tack.Model;

namespace Tack.Views;

/// <summary>
/// The spaces in a "Move to" menu (a task's, a sheet's, Ctrl+M): each space, the current one greyed
/// out, <see cref="SpaceView.MaxListed"/> at a time. Past that, "More spaces" opens the next ones in a
/// menu of its own, made only when it opens. Only a crafted file or import makes that many.
/// </summary>
internal static class SpaceMenu
{
    public static void Fill(ItemCollection items, IReadOnlyList<SpaceTab> tabs, string current, Action<string> pick, int from = 0)
    {
        int end = Math.Min(tabs.Count, from + SpaceView.MaxListed);
        for (int i = from; i < end; i++)
        {
            var space = tabs[i];
            var item = new MenuItem
            {
                // A TextBlock, so an underscore in a name stays an underscore.
                Header = new TextBlock { Text = space.Name, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis },
                IsEnabled = space.Id != current,
            };
            AutomationProperties.SetName(item, space.Name);
            item.Click += (_, _) => pick(space.Id);
            items.Add(item);
        }
        if (end >= tabs.Count) return;

        string left = $"{tabs.Count - end:N0} more";
        var more = new MenuItem { Header = new TextBlock { Text = $"More spaces ({left})" } };
        AutomationProperties.SetName(more, $"More spaces, {left}");
        more.Items.Add(new MenuItem { Header = "…", IsEnabled = false }); // so it opens as a menu of its own
        bool filled = false;
        more.SubmenuOpened += (_, e) =>
        {
            if (filled || !ReferenceEquals(e.OriginalSource, more)) return;
            filled = true;
            more.Items.Clear();
            Fill(more.Items, tabs, current, pick, end);
        };
        items.Add(more);
    }
}
