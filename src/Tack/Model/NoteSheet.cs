namespace Tack.Model;

/// <summary>A sheet of scratch paper. Its title is simply its first line.</summary>
public sealed class NoteSheet : Observable
{
    private string _text;
    private DateTimeOffset _updatedAt;

    public NoteSheet(string text = "", string? id = null, DateTimeOffset? updatedAt = null)
    {
        _text = text;
        Id = id ?? Guid.NewGuid().ToString("N")[..12];
        _updatedAt = updatedAt ?? DateTimeOffset.Now;
    }

    public string Id { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? "")) return;
            UpdatedAt = DateTimeOffset.Now;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        private set => Set(ref _updatedAt, value);
    }

    public bool IsEmpty => string.IsNullOrWhiteSpace(_text);

    public string Title
    {
        get
        {
            foreach (var line in _text.AsSpan().EnumerateLines())
            {
                var t = line.Trim().TrimStart("#-*• ".AsSpan()).Trim();
                if (t.IsEmpty) continue;
                return t.Length > 16 ? string.Concat(t[..15].TrimEnd(), "…") : t.ToString();
            }
            return "Untitled";
        }
    }

    public override string ToString() => Title;
}
