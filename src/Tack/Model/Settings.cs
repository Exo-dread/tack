namespace Tack.Model;

/// <summary>User preferences and remembered UI state. Serialized as-is into tack.json.</summary>
public sealed class Settings : Observable
{
    public const double DefaultPanelWidth = 340;
    public const double DefaultPanelHeight = 480;

    private bool _launchAtStartup;
    private bool _hideInFullscreen = true;
    private bool _glass = true;
    private string _hotkey = "Ctrl+Alt+Space";
    private string _accent = "system";
    private bool _pinned;
    private string _tab = "tasks";
    private bool _doneExpanded;
    private int? _orbX;
    private int? _orbY;
    private double _panelWidth = DefaultPanelWidth;
    private double _panelHeight = DefaultPanelHeight;
    private string? _activeSheet;

    // ---- Preferences ----
    public bool LaunchAtStartup { get => _launchAtStartup; set => Set(ref _launchAtStartup, value); }

    /// <summary>Step out of the way while a full-screen app (video, game, slideshow) is in front.</summary>
    public bool HideInFullscreen { get => _hideInFullscreen; set => Set(ref _hideInFullscreen, value); }

    /// <summary>Frosted acrylic backdrop (Windows 11). When off, the panel is solid.</summary>
    public bool Glass { get => _glass; set => Set(ref _glass, value); }

    public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value ?? ""); }

    /// <summary>"system" follows the Windows accent colour; otherwise a #RRGGBB colour.</summary>
    public string Accent { get => _accent; set => Set(ref _accent, string.IsNullOrWhiteSpace(value) ? "system" : value); }

    // ---- Remembered state ----

    /// <summary>Pinned: the panel stays open above other apps. Unpinned: it folds back into the orb when you click away.</summary>
    public bool Pinned { get => _pinned; set => Set(ref _pinned, value); }

    public string Tab { get => _tab; set => Set(ref _tab, value == "notes" ? "notes" : "tasks"); }

    public bool DoneExpanded { get => _doneExpanded; set => Set(ref _doneExpanded, value); }

    /// <summary>Centre of the orb in physical screen pixels (virtual desktop coordinates).</summary>
    public int? OrbX { get => _orbX; set => Set(ref _orbX, value); }

    public int? OrbY { get => _orbY; set => Set(ref _orbY, value); }

    /// <summary>Panel size in device-independent pixels, so it looks the same on every monitor.</summary>
    public double PanelWidth { get => _panelWidth; set => Set(ref _panelWidth, Math.Clamp(value, 280, 900)); }

    public double PanelHeight { get => _panelHeight; set => Set(ref _panelHeight, Math.Clamp(value, 260, 1400)); }

    public string? ActiveSheet { get => _activeSheet; set => Set(ref _activeSheet, value); }
}
