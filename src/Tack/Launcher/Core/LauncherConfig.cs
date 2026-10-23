using System.Text.Json.Serialization;

namespace Tack.Launcher.Core;

internal enum ShortcutKind { App, Folder, Url, Group }

/// <summary>What happens when a shortcut is chosen without picking a display.</summary>
internal enum LaunchMode
{
    /// <summary>Open it the ordinary way; Windows and the app decide where.</summary>
    Normal,
    /// <summary>Open it, then try to move its window to the remembered display.</summary>
    Preferred,
    /// <summary>Show the display picker first.</summary>
    Ask,
}

/// <summary>Which display a shortcut prefers. Matched by device name and bounds, so an unplugged display is noticed.</summary>
internal sealed class MonitorRef
{
    public string Device { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public static MonitorRef From(Display d) => new()
    {
        Device = d.Device,
        X = (int)Math.Round(d.Bounds.X), Y = (int)Math.Round(d.Bounds.Y),
        Width = (int)Math.Round(d.Bounds.Width), Height = (int)Math.Round(d.Bounds.Height),
    };

    public MonitorRef Clone() => (MonitorRef)MemberwiseClone();
}

/// <summary>
/// One launcher entry. Stored as plain strings so a hand-edited or newer launcher.json never stops
/// the file from loading: anything unknown is dropped or reset by <see cref="LauncherConfig.Normalize"/>.
/// </summary>
internal sealed class Shortcut
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>"app", "folder", "url" or "group".</summary>
    public string Kind { get; set; } = "app";

    /// <summary>App: a full path (or a bare program name like notepad.exe). Folder: a full path. Url: http(s) address. Group: unused.</summary>
    public string Target { get; set; } = "";

    /// <summary>App only: arguments passed to the program as they are (never through a command shell).</summary>
    public string? Arguments { get; set; }

    /// <summary>"normal", "preferred" or "ask".</summary>
    public string Launch { get; set; } = "normal";

    public MonitorRef? Monitor { get; set; }

    /// <summary>Group only: up to <see cref="LauncherConfig.MaxChildren"/> shortcuts (never groups).</summary>
    public List<Shortcut>? Children { get; set; }

    [JsonIgnore]
    public ShortcutKind KindValue => ParseKind(Kind) ?? ShortcutKind.App;

    [JsonIgnore]
    public LaunchMode LaunchValue => Launch switch
    {
        "preferred" => LaunchMode.Preferred,
        "ask" => LaunchMode.Ask,
        _ => LaunchMode.Normal,
    };

    [JsonIgnore]
    public bool IsGroup => KindValue == ShortcutKind.Group;

    /// <summary>Apps and folders open a window that can be moved to another display; websites open in the browser's own windows.</summary>
    [JsonIgnore]
    public bool CanTargetDisplay => KindValue is ShortcutKind.App or ShortcutKind.Folder;

    public static ShortcutKind? ParseKind(string? kind) => kind switch
    {
        "app" => ShortcutKind.App,
        "folder" => ShortcutKind.Folder,
        "url" => ShortcutKind.Url,
        "group" => ShortcutKind.Group,
        _ => null,
    };

    public static string KindName(ShortcutKind kind) => kind switch
    {
        ShortcutKind.Folder => "folder",
        ShortcutKind.Url => "url",
        ShortcutKind.Group => "group",
        _ => "app",
    };

    public static string LaunchName(LaunchMode mode) => mode switch
    {
        LaunchMode.Preferred => "preferred",
        LaunchMode.Ask => "ask",
        _ => "normal",
    };

    public Shortcut Clone() => new()
    {
        Id = Id, Label = Label, Kind = Kind, Target = Target, Arguments = Arguments, Launch = Launch,
        Monitor = Monitor?.Clone(),
        Children = Children?.Select(c => c.Clone()).ToList(),
    };
}

/// <summary>The radial launcher's settings, kept in launcher.json beside tack.json (never in tack.json, never synced).</summary>
internal sealed class LauncherConfig
{
    public const int SlotCount = 6;
    public const int MaxChildren = 8;
    public const int MaxLabel = 40;
    public const int MaxTarget = 2048;
    public const int MaxArguments = 1024;
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Off by default: Tack without the launcher is exactly the Tack it was.</summary>
    public bool Enabled { get; set; }

    /// <summary>No expand/collapse motion (Windows' own "Animation effects" setting is respected as well).</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Exactly <see cref="SlotCount"/> fixed positions, clockwise from the top. Null is an empty position:
    /// removing a shortcut never moves the others, so muscle memory keeps working.</summary>
    public List<Shortcut?> Slots { get; set; } = new();

    public static LauncherConfig CreateDefault() => new LauncherConfig().Normalize();

    /// <summary>
    /// Makes any loaded config safe to use: six slots, two levels at most, known kinds only,
    /// bounded text, unique ids. Idempotent.
    /// </summary>
    public LauncherConfig Normalize()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var slots = new List<Shortcut?>(SlotCount);
        foreach (var s in Slots ?? new List<Shortcut?>())
        {
            if (slots.Count == SlotCount) break;
            slots.Add(Clean(s, topLevel: true, ids));
        }
        while (slots.Count < SlotCount) slots.Add(null);
        Slots = slots;
        Version = CurrentVersion;
        return this;
    }

    private static Shortcut? Clean(Shortcut? s, bool topLevel, HashSet<string> ids)
    {
        if (s == null) return null;
        var kind = Shortcut.ParseKind(s.Kind?.Trim().ToLowerInvariant());
        if (kind == null) return null;
        if (kind == ShortcutKind.Group && !topLevel) return null; // two levels at most

        s.Kind = Shortcut.KindName(kind.Value);
        s.Label = Truncate(OneLine(s.Label), MaxLabel);
        s.Target = Truncate((s.Target ?? "").Trim(), MaxTarget);
        s.Arguments = kind == ShortcutKind.App && !string.IsNullOrWhiteSpace(s.Arguments)
            ? Truncate(s.Arguments.Trim(), MaxArguments) : null;
        s.Launch = s.Launch is "preferred" or "ask" ? s.Launch : "normal";
        if (s.LaunchValue == LaunchMode.Preferred && s.Monitor == null) s.Launch = "normal";
        if (kind is not (ShortcutKind.App or ShortcutKind.Folder))
        {
            s.Launch = "normal";
            s.Monitor = null;
        }
        if (s.Monitor != null) s.Monitor.Device ??= ""; // "device": null in a hand-edited file
        if (s.Monitor != null && (s.Monitor.Width <= 0 || s.Monitor.Height <= 0)) { s.Monitor = null; if (s.Launch == "preferred") s.Launch = "normal"; }

        if (string.IsNullOrWhiteSpace(s.Id) || s.Id.Length > 64 || !ids.Add(s.Id))
        {
            s.Id = NewId();
            ids.Add(s.Id);
        }

        if (kind == ShortcutKind.Group)
        {
            var children = new List<Shortcut>();
            foreach (var c in s.Children ?? new List<Shortcut>())
            {
                if (children.Count == MaxChildren) break;
                if (Clean(c, topLevel: false, ids) is { } clean) children.Add(clean);
            }
            s.Children = children;
            s.Target = "";
        }
        else s.Children = null;

        if (s.Label.Length == 0) s.Label = DefaultLabel(s);
        return s;
    }

    /// <summary>A label when the user gave none: the program, folder or site name.</summary>
    public static string DefaultLabel(Shortcut s)
    {
        string t = s.Target.Trim().Trim('"');
        string label = s.KindValue switch
        {
            ShortcutKind.Group => "Group",
            ShortcutKind.Url => Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Host.Length > 0
                ? (u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host) : "Website",
            ShortcutKind.Folder => LastSegment(t) is { Length: > 0 } f ? f : "Folder",
            _ => LastSegment(t) is { Length: > 0 } a ? StripExtension(a) : "App",
        };
        return Truncate(label, MaxLabel);
    }

    private static string LastSegment(string path)
    {
        string p = path.TrimEnd('\\', '/');
        int i = p.LastIndexOfAny(new[] { '\\', '/' });
        return i >= 0 ? p[(i + 1)..] : p;
    }

    private static string StripExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var chars = text.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        return new string(chars).Trim();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>"1 shortcut", "3 shortcuts".</summary>
    public static string CountOf(int n) => n == 1 ? "1 shortcut" : $"{n} shortcuts";

    public LauncherConfig Clone() => new()
    {
        Version = Version, Enabled = Enabled, ReduceMotion = ReduceMotion,
        Slots = Slots.Select(s => s?.Clone()).ToList(),
    };
}
