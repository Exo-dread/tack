using System.Windows;
using System.Windows.Threading;
using Tack.Drop.Core;
using Tack.Launcher;
using Tack.Launcher.Core;
using Tack.Model;
using Tack.Services;

namespace Tack.Drop;

/// <summary>What the orb hands a drag to: the effect to report back for each step (never Move).</summary>
internal interface IOrbDropTarget
{
    DragDropEffects DragEnter(System.Windows.IDataObject data, DragDropEffects allowed);
    DragDropEffects DragOver(DragDropEffects allowed);
    void DragLeave();
    DragDropEffects Drop(System.Windows.IDataObject data, DragDropEffects allowed);
}

/// <summary>The panel's toast, as a drop uses it: one Undo on offer at a time, for Tack as a whole.</summary>
internal interface IUndoToast
{
    void ShowToast(string message, Action? undo = null);
    void Announce(string text);
    bool HasPendingUndo { get; }
    bool Offers(Action undo);
    bool UndoIf(Action undo);
    event Action? UndoSettled;
}

/// <summary>The bubble beside the orb.</summary>
internal interface IDropBubble
{
    void ShowHint(string title, string? caption, bool refused);
    void ShowDone(string title, string? caption, Action undo, TimeSpan showFor);
    void ShowNote(string title, TimeSpan showFor);
    void Dismiss();
    Action? OfferedUndo { get; }
    bool IsShown { get; }
    event Action<Action>? UndoRequested;
}

/// <summary>
/// Dropping onto the orb: files, folders, links, emails and text become tasks in the space on show, each with
/// what it came from kept on this PC for its paperclip to open. Until something is dragged over the orb, it's
/// one look for attachments.json (and the paperclips it puts back on); the bubble is made on the first drag.
/// Every entry point catches its own errors: a drop that goes wrong adds nothing and never takes Tack down.
/// </summary>
internal sealed class DropModule : IOrbDropTarget
{
    /// <summary>As long as the panel's toast offers Undo.</summary>
    internal static readonly TimeSpan UndoFor = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan NoteFor = TimeSpan.FromSeconds(3);

    private readonly SpaceView _view;
    private readonly Workspace _ws;
    private readonly AttachmentStore _store;
    private readonly DropIntake _intake;
    private Action<bool> _highlight = _ => { };
    private IUndoToast? _toast;
    private Func<IDropBubble>? _makeBubble;
    private IDropBubble? _bubble;
    private DropRead? _preview;

    public DropModule(string dataFolder, Workspace ws, SpaceView view, DropContext? context = null)
    {
        _view = view;
        _ws = ws;
        _store = new AttachmentStore(dataFolder, DesktopDrop.IsProgram);
        _store.Load((message, ex) => Log.Error($"Drop: {message}", ex));
        _intake = new DropIntake(ws, _store, context ?? DesktopDrop.Context(), DesktopDrop.MarkFromOutside);
        _intake.ShowAttachments();
    }

    internal AttachmentStore Store => _store;

    /// <param name="highlight">Lights the orb up while something it can take is over it.</param>
    /// <param name="bubble">Makes the bubble, the first time something is dragged over the orb.</param>
    public void Attach(Action<bool> highlight, IUndoToast toast, Func<IDropBubble> bubble)
    {
        _highlight = highlight;
        _toast = toast;
        _makeBubble = bubble;
        var dispatcher = Dispatcher.CurrentDispatcher;
        // Raised before a used Undo runs: what depends on it waits until it has.
        toast.UndoSettled += () => dispatcher.BeginInvoke(() =>
        {
            if (_bubble?.OfferedUndo is { } offered && !toast.Offers(offered)) _bubble.Dismiss();
            Prune();
        });
    }

    // ================= Dragging over the orb =================

    public DragDropEffects DragEnter(System.Windows.IDataObject data, DragDropEffects allowed)
    {
        try
        {
            _preview = _intake.Preview(new OleDropData(data));
            var effect = Effect(_preview, allowed);
            if (_preview.Family == DropFamily.None) return effect;
            _highlight(effect != DragDropEffects.None);
            Bubble()?.ShowHint(_preview.Refusal ?? Hint(_preview.Count), _preview.Refusal == null ? Where() : null,
                refused: effect == DragDropEffects.None);
            return effect;
        }
        catch (Exception ex)
        {
            Log.Error("Drop: couldn't read what's dragged", ex);
            _preview = DropRead.None;
            return DragDropEffects.None;
        }
    }

    public DragDropEffects DragOver(DragDropEffects allowed) => _preview is { } p ? Effect(p, allowed) : DragDropEffects.None;

    public void DragLeave()
    {
        _preview = null;
        _highlight(false);
        if (_bubble is { OfferedUndo: null }) _bubble.Dismiss();
    }

    public DragDropEffects Drop(System.Windows.IDataObject data, DragDropEffects allowed)
    {
        var preview = _preview;
        _preview = null;
        _highlight(false);
        try
        {
            var effect = preview is { } p ? Effect(p, allowed) : DragDropEffects.None;
            if (effect == DragDropEffects.None)
            {
                if (_bubble is { OfferedUndo: null }) _bubble.Dismiss();
                return effect;
            }
            var ole = new OleDropData(data);
            Log.Info($"Drop onto the orb: {preview!.Family}, offered as {Describe(ole.Formats)}");
            Report(_intake.Take(ole, _view.NewItemSpace));
            return effect;
        }
        catch (Exception ex)
        {
            Log.Error("Drop: couldn't add what was dropped", ex);
            Bubble()?.ShowNote("Tack couldn't add this", NoteFor);
            return DragDropEffects.None;
        }
    }

    private void Report(DropOutcome outcome)
    {
        if (outcome.Added.Count == 0)
        {
            Bubble()?.ShowNote(outcome.Message ?? "Nothing here Tack can add", NoteFor);
            return;
        }
        string message = outcome.Message!;
        _toast?.ShowToast(outcome.NotKept == 0 ? message : $"{message} · {outcome.NotKept} not kept", outcome.Undo);
        _toast?.Announce(message);
        string? caption = outcome.Note ?? (outcome.Added.Count == 1 ? outcome.Added[0].Text : Where());
        Bubble()?.ShowDone(message, caption, outcome.Undo!, UndoFor);
    }

    private IDropBubble? Bubble()
    {
        if (_bubble != null || _makeBubble == null) return _bubble;
        _bubble = _makeBubble();
        _bubble.UndoRequested += undo => _toast?.UndoIf(undo);
        return _bubble;
    }

    /// <summary>The panel opened, the orb moved, or it's going away: the bubble goes.</summary>
    public void Dismiss() => _bubble?.Dismiss();

    /// <summary>The bubble is beside the orb (where the peek would show): the peek waits.</summary>
    public bool BubbleShown => _bubble is { IsShown: true };

    private static string Hint(int count) => count == 1 ? "Drop to add a task" : $"Drop to add {count} tasks";

    /// <summary>"In Work" once there are spaces and one is on show (new tasks go there); else nothing.</summary>
    private string? Where() => _view.HasSpaces && !_view.IsAll ? $"In {SpaceView.Shorten(_view.ShownName, 40)}" : null;

    /// <summary>
    /// The effect Tack reports back: a link to files, folders and pages (they stay where they are), a copy of
    /// anything else, never a move (which would tell the app it came from to delete it: an email, a paragraph).
    /// </summary>
    internal static DragDropEffects Effect(DropRead read, DragDropEffects allowed)
    {
        if (read.Family == DropFamily.None || read.Refusal != null || read.Count == 0) return DragDropEffects.None;
        if (read.Family is DropFamily.Files or DropFamily.Links && allowed.HasFlag(DragDropEffects.Link)) return DragDropEffects.Link;
        if (allowed.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
        if (allowed.HasFlag(DragDropEffects.Link)) return DragDropEffects.Link;
        return DragDropEffects.None;
    }

    /// <summary>The formats a drag offered, for the log (their names only, never what's in them).</summary>
    private static string Describe(IReadOnlyList<string> formats) =>
        string.Join(", ", formats.Take(24).Select(f => DropNames.OneLine(f, 48))) + (formats.Count > 24 ? $" and {formats.Count - 24} more" : "");

    // ================= Paperclips =================

    /// <summary>Opens a task's attachment (its paperclip, or Ctrl+O), or says why it can't.</summary>
    public void Open(TaskItem task, Action<string> report)
    {
        try
        {
            if (_store.For(task.Id) is not { } attachment) return;
            var plan = AttachmentRules.Plan(attachment, _store.Folder, RealFileProbe.Instance, DesktopDrop.IsProgram, out string? problem);
            if (plan == null)
            {
                report(problem ?? "Tack can't open this.");
                return;
            }
            ShortcutLauncher.Start(plan, (_, error) => { if (error != null) report(error); }, "Windows couldn't open this.");
        }
        catch (Exception ex)
        {
            Log.Error("Drop: couldn't open an attachment", ex);
            report("Windows couldn't open this.");
        }
    }

    // ================= Letting attachments go =================

    /// <summary>
    /// Lets go of the attachments of tasks deleted for good (and their saved copies): only while no Undo is on
    /// offer. Called when an Undo lapses or is used, at midnight and at startup.
    /// </summary>
    public void Prune()
    {
        try
        {
            if (_store.Count == 0 || _toast?.HasPendingUndo == true) return;
            int n = _intake.Prune(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (n > 0) Log.Info($"Drop: let go of {n} attachment{(n == 1 ? "" : "s")} of deleted tasks.");
        }
        catch (Exception ex) { Log.Error("Drop: couldn't let attachments go", ex); }
    }

    /// <summary>
    /// At startup: copies no attachment points to (Tack stopped between saving one and recording it). Not in a
    /// session that started empty because tack.json couldn't be read: nothing beside it is tidied then.
    /// </summary>
    public void SweepOrphans()
    {
        if (_ws.LoadFailed) return;
        try { _store.SweepOrphans(DateTime.UtcNow.AddMinutes(-10)); }
        catch (Exception ex) { Log.Error("Drop: couldn't tidy the attachments folder", ex); }
    }
}
