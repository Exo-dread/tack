using Tack.Sync;

namespace Tack.Model;

/// <summary>
/// A single task. Deliberately tiny: text, and when it was created and finished. Behind the scenes
/// it also carries what merging with other devices needs (a sort key, a tombstone and a clock per
/// field); <see cref="Workspace"/> keeps those up to date.
/// </summary>
public sealed class TaskItem : Observable
{
    private string _text;
    private string? _completedIso;
    private bool _isChecking;
    private string? _spaceLabel;

    internal TaskItem(TaskData data)
    {
        Id = data.Id;
        _text = data.Text;
        CreatedIso = data.Created;
        Order = data.Order;
        Clk = data.Clk;
        Apply(data);
    }

    public string Id { get; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value ?? "");
    }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsDone => _completedIso != null;

    /// <summary>UI-only: the tick animation is playing before the task moves to Done.</summary>
    public bool IsChecking
    {
        get => _isChecking;
        set => Set(ref _isChecking, value);
    }

    /// <summary>UI-only: the name of its space, shown on its row in All while there are spaces; else null.</summary>
    public string? SpaceLabel
    {
        get => _spaceLabel;
        set => Set(ref _spaceLabel, value);
    }

    public bool CompletedToday => CompletedAt is { } c && c.LocalDateTime.Date == DateTime.Today;

    // ---- What merging needs (see Sync/Records.cs) ----

    /// <summary>When it was created, as the ISO text it was stored with (kept exactly, so other devices see no change).</summary>
    internal string CreatedIso { get; private set; }

    internal string? CompletedIso => _completedIso;

    /// <summary>Completion time in whole milliseconds, which is what orders the Done list on every device.</summary>
    internal long CompletedMs { get; private set; }

    /// <summary>Position among open tasks (see <see cref="OrderKey"/>); lower sorts first.</summary>
    internal string Order { get; set; }

    /// <summary>Deleted tasks are kept as tombstones so the deletion can reach other devices.</summary>
    internal bool IsDeleted { get; set; }

    /// <summary>The space it was put in ("" if never: Main). See <see cref="Spaces.Effective"/>.</summary>
    internal string Space { get; set; } = "";

    internal TaskClocks Clk { get; set; }

    internal void SetCompleted(string? iso)
    {
        bool wasDone = IsDone;
        _completedIso = iso;
        CompletedMs = IsoTime.ParseMs(iso) ?? 0;
        CompletedAt = iso == null ? null : IsoTime.Parse(iso) ?? DateTimeOffset.UnixEpoch;
        OnPropertyChanged(nameof(CompletedAt));
        if (wasDone != IsDone) OnPropertyChanged(nameof(IsDone));
    }

    internal TaskData ToData() => new(Id, _text, CreatedIso, _completedIso, Order, IsDeleted, Space, Clk);

    /// <summary>Takes every field from a record (loading or merging; not a new edit).</summary>
    internal void Apply(TaskData data)
    {
        Text = data.Text;
        if (data.Created != CreatedIso || CreatedAt == default)
        {
            CreatedIso = data.Created;
            CreatedAt = IsoTime.Parse(data.Created) ?? DateTimeOffset.UnixEpoch;
        }
        if (data.Completed != _completedIso) SetCompleted(data.Completed);
        Order = data.Order;
        IsDeleted = data.Deleted;
        Space = data.Space;
        Clk = data.Clk;
    }

    /// <summary>Screen readers announce list items by their ToString().</summary>
    public override string ToString() => IsDone ? $"{Text} (done)" : Text;
}
