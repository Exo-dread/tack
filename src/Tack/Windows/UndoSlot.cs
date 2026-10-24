using Tack.Services;

namespace Tack.Windows;

/// <summary>
/// The toast's Undo: what it undoes, and what waits until it can't be undone any more (a discarded snippet's
/// file is deleted then). Showing another toast, the toast going, or Tack quitting all make it lapse; using
/// it means nothing lapses.
/// </summary>
internal sealed class UndoSlot
{
    private Action? _undo;
    private Action? _lapsed;

    public bool CanUndo => _undo != null;

    /// <summary>Holds a new Undo; the one it replaces lapses first.</summary>
    public void Hold(Action? undo, Action? lapsed)
    {
        Lapse();
        _undo = undo;
        _lapsed = lapsed;
    }

    /// <summary>Takes the Undo out to run it (so nothing lapses); null when there's none.</summary>
    public Action? Take()
    {
        var undo = _undo;
        if (undo == null) return null;
        _undo = null;
        _lapsed = null;
        return undo;
    }

    /// <summary>The Undo is past using: whatever was waiting on it goes ahead, once.</summary>
    public void Lapse()
    {
        _undo = null;
        var lapsed = _lapsed;
        _lapsed = null;
        try { lapsed?.Invoke(); }
        catch (Exception ex) { Log.Error("Couldn't finish what a toast's Undo held back", ex); }
    }
}
