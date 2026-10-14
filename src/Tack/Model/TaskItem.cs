namespace Tack.Model;

/// <summary>A single task. Deliberately tiny: text, and when it was created and finished.</summary>
public sealed class TaskItem : Observable
{
    private string _text;
    private DateTimeOffset? _completedAt;
    private bool _isChecking;

    public TaskItem(string text, string? id = null, DateTimeOffset? createdAt = null, DateTimeOffset? completedAt = null)
    {
        _text = text;
        Id = id ?? Guid.NewGuid().ToString("N")[..12];
        CreatedAt = createdAt ?? DateTimeOffset.Now;
        _completedAt = completedAt;
    }

    public string Id { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value ?? "");
    }

    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set
        {
            if (Set(ref _completedAt, value)) OnPropertyChanged(nameof(IsDone));
        }
    }

    public bool IsDone => _completedAt != null;

    /// <summary>UI-only: the tick animation is playing before the task moves to Done.</summary>
    public bool IsChecking
    {
        get => _isChecking;
        set => Set(ref _isChecking, value);
    }

    public bool CompletedToday => _completedAt is { } c && c.LocalDateTime.Date == DateTime.Today;

    /// <summary>Screen readers announce list items by their ToString().</summary>
    public override string ToString() => IsDone ? $"{Text} (done)" : Text;
}
