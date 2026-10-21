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
        // A copy without the field (an older version's) has a Zero clock, so it never undoes an assignment.
        if (Wins(remote.Clk.Space ?? Stamp.Zero, remote.Space ?? "", local.Clk.Space ?? Stamp.Zero, local.Space ?? ""))
            value = value with { Space = remote.Space ?? "", Clk = value.Clk with { Space = remote.Clk.Space ?? Stamp.Zero } };
        // Creation time never changes; if copies disagree (hand edits), keep the earlier one. A
        // deletion that arrived before the task did (device files carry deletions bare) has none.
        if (remote.Created.Length > 0 && (value.Created.Length == 0 || string.CompareOrdinal(remote.Created, value.Created) < 0))
            value = value with { Created = remote.Created };
        return (value, !ReferenceEquals(value, local));
    }

    /// <summary>
    /// Merges a sheet. Notes are prose, so a plain last-writer-wins would silently drop one
    /// device's paragraph. Given the clock both devices last agreed on (<paramref name="base"/>),
    /// or a test that knows what each side had seen (<paramref name="concurrent"/>, from sync),
    /// concurrent edits keep the later text in place and the other as a copy (<c>Conflict</c>).
    /// The copy's id is derived from the losing edit, so every device creates the same copy.
    /// </summary>
    public static (SheetData Value, bool Changed, SheetData? Conflict) Sheet(SheetData? local, SheetData remote, string? @base = null,
        Func<SheetData, SheetData, bool>? concurrent = null)
    {
        if (local == null) return (remote, true, null);
        var value = local;
        SheetData? conflict = null;

        bool textConcurrent = local.Text != remote.Text && (concurrent != null
            ? local.Clk.Text != remote.Clk.Text && concurrent(local, remote)
            : @base != null && Stamp.IsLater(local.Clk.Text, @base) && Stamp.IsLater(remote.Clk.Text, @base));

        if (Wins(remote.Clk.Text, remote.Text, local.Clk.Text, local.Text))
            value = value with { Text = remote.Text, Updated = remote.Updated, Clk = value.Clk with { Text = remote.Clk.Text } };
        if (Wins(remote.Clk.Order, remote.Order, local.Clk.Order, local.Order))
            value = value with { Order = remote.Order, Clk = value.Clk with { Order = remote.Clk.Order } };
        if (Wins(remote.Clk.Deleted, remote.Deleted, local.Clk.Deleted, local.Deleted))
            value = value with { Deleted = remote.Deleted, Clk = value.Clk with { Deleted = remote.Clk.Deleted } };
        if (Wins(remote.Clk.Space ?? Stamp.Zero, remote.Space ?? "", local.Clk.Space ?? Stamp.Zero, local.Space ?? ""))
            value = value with { Space = remote.Space ?? "", Clk = value.Clk with { Space = remote.Clk.Space ?? Stamp.Zero } };

        if (textConcurrent)
        {
            var loser = value.Text == local.Text ? remote : local;
            if (JsText.Trim(loser.Text).Length > 0)
            {
                // The copy stays in the space the losing text was written in.
                var (space, spaceClk) = SpaceField.Normal(loser.Space, loser.Clk.Space);
                conflict = new SheetData(
                    Ids.Derived($"{local.Id}:{loser.Clk.Text}"),
                    loser.Text,
                    loser.Updated,
                    OrderKey.After(value.Order),
                    false,
                    space,
                    SheetClocks.All(loser.Clk.Text) with { Space = spaceClk });
            }
        }
        return (value, !ReferenceEquals(value, local), conflict);
    }

    /// <summary>Merges a space, field by field (no copies: a name is short, the later one wins).</summary>
    public static (SpaceData Value, bool Changed) Space(SpaceData? local, SpaceData remote)
    {
        if (local == null) return (remote, true);
        var value = local;
        if (Wins(remote.Clk.Name, remote.Name, local.Clk.Name, local.Name))
            value = value with { Name = remote.Name, Clk = value.Clk with { Name = remote.Clk.Name } };
        if (Wins(remote.Clk.Order, remote.Order, local.Clk.Order, local.Order))
            value = value with { Order = remote.Order, Clk = value.Clk with { Order = remote.Clk.Order } };
        if (Wins(remote.Clk.Deleted, remote.Deleted, local.Clk.Deleted, local.Deleted))
            value = value with { Deleted = remote.Deleted, Clk = value.Clk with { Deleted = remote.Clk.Deleted } };
        return (value, !ReferenceEquals(value, local));
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
