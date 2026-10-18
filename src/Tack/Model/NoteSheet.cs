using Tack.Sync;

namespace Tack.Model;

/// <summary>A sheet of scratch paper. Its title is simply its first line.</summary>
public sealed class NoteSheet : Observable
{
    private string _text;

    internal NoteSheet(SheetData data)
    {
        Id = data.Id;
        _text = data.Text;
        UpdatedIso = data.Updated;
        Order = data.Order;
        Clk = data.Clk;
        Apply(data);
    }

    public string Id { get; }

    /// <summary>The text. Edits are stamped (and <see cref="UpdatedAt"/> moved) by <see cref="Workspace"/>.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? "")) return;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public DateTimeOffset UpdatedAt { get; private set; }

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

    // ---- What merging needs (see Sync/Records.cs) ----

    /// <summary>When the text last changed, as the ISO text it was stored with.</summary>
    internal string UpdatedIso { get; private set; }

    internal string Order { get; set; }

    internal bool IsDeleted { get; set; }

    internal SheetClocks Clk { get; set; }

    internal void SetUpdated(string iso)
    {
        UpdatedIso = iso;
        UpdatedAt = IsoTime.Parse(iso) ?? DateTimeOffset.UnixEpoch;
    }

    internal SheetData ToData() => new(Id, _text, UpdatedIso, Order, IsDeleted, Clk);

    /// <summary>Takes every field from a record (loading or merging; not a new edit).</summary>
    internal void Apply(SheetData data)
    {
        Text = data.Text;
        if (data.Updated != UpdatedIso || UpdatedAt == default) SetUpdated(data.Updated);
        Order = data.Order;
        IsDeleted = data.Deleted;
        Clk = data.Clk;
    }

    public override string ToString() => Title;
}
