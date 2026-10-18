namespace Tack.Model;

/// <summary>User preferences and remembered UI state. Serialized as-is into tack.json.</summary>
public sealed class Settings : Observable
{
    public const double DefaultWidth = 340;
    public const double DefaultNotesHeight = 480;
    public const double MinWidth = 280;
    public const double MinHeight = 200;

    private bool _launchAtStartup;
    private bool _hideInFullscreen = true;
    private bool _glass = true;
    private string _theme = "system";
    private string _hotkey = "Ctrl+Alt+Space";
    private string _accent = "system";
    private bool _pinned;
    private string _tab = "tasks";
    private bool _doneExpanded;
    private int? _orbX;
    private int? _orbY;
    private double _tasksWidth = DefaultWidth;
    private double? _tasksHeight;
    private double _notesWidth = DefaultWidth;
    private double _notesHeight = DefaultNotesHeight;
    private string? _activeSheet;
    private string? _syncFolder;

    // ---- Preferences ----
    public bool LaunchAtStartup { get => _launchAtStartup; set => Set(ref _launchAtStartup, value); }

    /// <summary>Step out of the way while a full-screen app (video, game, slideshow) is in front.</summary>
    public bool HideInFullscreen { get => _hideInFullscreen; set => Set(ref _hideInFullscreen, value); }

    /// <summary>Frosted acrylic backdrop (Windows 11). When off, the panel is solid.</summary>
    public bool Glass { get => _glass; set => Set(ref _glass, value); }

    /// <summary>"system" follows the Windows app mode; otherwise "light" or "dark".</summary>
    public string Theme { get => _theme; set => Set(ref _theme, value is "light" or "dark" ? value : "system"); }

    public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value ?? ""); }

    /// <summary>The folder Tack syncs through (normally OneDrive's "Apps\Tack Focus"); null: sync is off.</summary>
    public string? SyncFolder { get => _syncFolder; set => Set(ref _syncFolder, string.IsNullOrWhiteSpace(value) ? null : value); }

    /// <summary>"system" follows the Windows accent colour; otherwise a #RRGGBB colour.</summary>
    public string Accent { get => _accent; set => Set(ref _accent, string.IsNullOrWhiteSpace(value) ? "system" : value); }

    // ---- Remembered state ----

    /// <summary>Pinned: the panel stays open above other apps. Unpinned: it folds back into the orb when you click away.</summary>
    public bool Pinned { get => _pinned; set => Set(ref _pinned, value); }

    public string Tab { get => _tab; set => Set(ref _tab, value == "notes" ? "notes" : "tasks"); }

    public bool DoneExpanded { get => _doneExpanded; set => Set(ref _doneExpanded, value); }

    /// <summary>Centre of the orb in physical screen pixels (virtual desktop coordinates).
    /// The panel unfolds from (and folds back to) this point, whichever tab is showing.</summary>
    public int? OrbX { get => _orbX; set => Set(ref _orbX, value); }

    public int? OrbY { get => _orbY; set => Set(ref _orbY, value); }

    // Each tab keeps its own size, in device-independent pixels so it looks the same on every monitor.

    public double TasksWidth { get => _tasksWidth; set => Set(ref _tasksWidth, ClampWidth(value)); }

    /// <summary>Null means "fit the tasks": the panel grows and shrinks with the list.
    /// Set once the user resizes the panel on the Tasks tab.</summary>
    public double? TasksHeight { get => _tasksHeight; set => Set(ref _tasksHeight, value is { } h ? ClampHeight(h) : null); }

    public double NotesWidth { get => _notesWidth; set => Set(ref _notesWidth, ClampWidth(value)); }

    public double NotesHeight { get => _notesHeight; set => Set(ref _notesHeight, ClampHeight(value)); }

    public string? ActiveSheet { get => _activeSheet; set => Set(ref _activeSheet, value); }

    // ---- Tack 1.0 stored a single panel size; read it once so upgrades keep their size. ----

    public double? PanelWidth { get; set; }

    public double? PanelHeight { get; set; }

    /// <summary>Moves a 1.0-era single panel size into the per-tab sizes.</summary>
    public void MigrateLegacySize()
    {
        if (PanelWidth is double w)
        {
            TasksWidth = w;
            NotesWidth = w;
        }
        if (PanelHeight is double h) NotesHeight = h;
        PanelWidth = null;
        PanelHeight = null;
    }

    private static double ClampWidth(double w) => Math.Clamp(double.IsFinite(w) ? w : DefaultWidth, MinWidth, 1200);

    private static double ClampHeight(double h) => Math.Clamp(double.IsFinite(h) ? h : DefaultNotesHeight, MinHeight, 1600);
}
