namespace Tack.Sync;

/// <summary>
/// Merging two copies of the same record, field by field: the change with the later clock wins
/// (port of web/src/model/merge.ts). Merges are commutative, associative and idempotent, so
/// devices that have seen the same changes end up identical, whatever order the changes arrived in.
/// </summary>
internal static class Merge
{
    public static (TaskData Value, bool Changed) Task(TaskData? local, TaskData remote)
    {
        if (local == null) return (remote, true);
        var value = local;
        if (Wins(remote.Clk.Text, remote.Text, local.Clk.Text, local.Text))
            value = value with { Text = remote.Text, Clk = value.Clk with { Text = remote.Clk.Text } };
        if (Wins(remote.Clk.Completed, remote.Completed, local.Clk.Completed, local.Completed))
            value = value with { Completed = remote.Completed, Clk = value.Clk with { Completed = remote.Clk.Completed } };
        if (Wins(remote.Clk.Order, remote.Order, local.Clk.Order, local.Order))
            value = value with { Order = remote.Order, Clk = value.Clk with { Order = remote.Clk.Order } };
        if (Wins(remote.Clk.Deleted, remote.Deleted, local.Clk.Deleted, local.Deleted))
            value = value with { Deleted = remote.Deleted, Clk = value.Clk with { Deleted = remote.Clk.Deleted } };
        // Creation time never changes; if copies disagree (hand edits), keep the earlier one.
        if (remote.Created.Length > 0 && string.CompareOrdinal(remote.Created, value.Created) < 0)
            value = value with { Created = remote.Created };
        return (value, !ReferenceEquals(value, local));
    }

    /// <summary>
    /// Merges a sheet. Notes are prose, so a plain last-writer-wins would silently drop one
    /// device's paragraph. Given the clock both devices last agreed on (<paramref name="base"/>),
    /// concurrent edits keep the later text in place and the other as a copy (<c>Conflict</c>).
    /// The copy's id is derived from the losing edit, so every device creates the same copy.
    /// </summary>
    public static (SheetData Value, bool Changed, SheetData? Conflict) Sheet(SheetData? local, SheetData remote, string? @base = null)
    {
        if (local == null) return (remote, true, null);
        var value = local;
        SheetData? conflict = null;

        bool textConcurrent = @base != null && Stamp.IsLater(local.Clk.Text, @base) && Stamp.IsLater(remote.Clk.Text, @base)
            && local.Text != remote.Text;

        if (Wins(remote.Clk.Text, remote.Text, local.Clk.Text, local.Text))
            value = value with { Text = remote.Text, Updated = remote.Updated, Clk = value.Clk with { Text = remote.Clk.Text } };
        if (Wins(remote.Clk.Order, remote.Order, local.Clk.Order, local.Order))
            value = value with { Order = remote.Order, Clk = value.Clk with { Order = remote.Clk.Order } };
        if (Wins(remote.Clk.Deleted, remote.Deleted, local.Clk.Deleted, local.Deleted))
            value = value with { Deleted = remote.Deleted, Clk = value.Clk with { Deleted = remote.Clk.Deleted } };

        if (textConcurrent)
        {
            var loser = value.Text == local.Text ? remote : local;
            if (JsText.Trim(loser.Text).Length > 0)
            {
                conflict = new SheetData(
                    Ids.Derived($"{local.Id}:{loser.Clk.Text}"),
                    loser.Text,
                    loser.Updated,
                    OrderKey.After(value.Order),
                    false,
                    SheetClocks.All(loser.Clk.Text));
            }
        }
        return (value, !ReferenceEquals(value, local), conflict);
    }

    public static (FocusData Value, bool Changed) Focus(FocusData local, FocusData remote) =>
        Wins(remote.Clk, remote.Id, local.Clk, local.Id) ? (remote, true) : (local, false);

    /// <summary>
    /// Does the remote value replace the local one? Later clock wins. Equal clocks mean the same
    /// write (a clock includes its device id); if values still differ (a hand-edited file), the
    /// larger serialised value wins so both sides agree.
    /// </summary>
    private static bool Wins(string remoteClk, string? remoteValue, string localClk, string? localValue)
    {
        if (remoteClk != localClk) return Stamp.IsLater(remoteClk, localClk);
        if (remoteValue == localValue) return false;
        return string.CompareOrdinal(JsText.Stringify(remoteValue), JsText.Stringify(localValue)) > 0;
    }

    private static bool Wins(string remoteClk, bool remoteValue, string localClk, bool localValue)
    {
        if (remoteClk != localClk) return Stamp.IsLater(remoteClk, localClk);
        // JSON.stringify: "true" sorts after "false".
        return remoteValue != localValue && remoteValue;
    }
}
