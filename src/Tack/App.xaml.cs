using System.IO;
using System.Windows;
using System.Windows.Threading;
using Tack.Model;
using Tack.Services;

namespace Tack;

public partial class App : Application
{
    private SingleInstance? _instance;
    private StateStore? _store;
    private AppShell? _shell;
    private FolderSync? _sync;
    private readonly Queue<DateTime> _recentErrors = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.TryAcquire();
        if (_instance == null)
        {
            // Already running: the other instance has been asked to open its panel.
            Shutdown();
            return;
        }

        ConfigureRendering();

        string folder = StateStore.ResolveFolder();
        _store = new StateStore(folder);
        Log.Initialize(folder);

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("Fatal error", args.ExceptionObject as Exception);
            _store?.Flush();
        };
        SessionEnding += (_, _) => { _shell?.PrepareForSessionEnd(); _store?.Flush(); };

        bool firstRun = !File.Exists(_store.FilePath) && !File.Exists(_store.FilePath + ".bak");
        var workspace = _store.Attach(_store.Load());
        _sync = new FolderSync(workspace, _store);
        _shell = new AppShell(workspace, _store, _sync);
        if (firstRun)
        {
            _shell.ChooseFreeHotkey();
            SeedWelcome(workspace);
        }

        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(() => _shell.Expand()));

        bool launchedAtLogin = e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        _shell.Start(openPanel: firstRun && !launchedAtLogin);
    }

    /// <summary>How WPF draws Tack, before any window exists (also used by the frame-timing harness in tests/Tack.Tests).</summary>
    internal static void ConfigureRendering()
    {
        // Tack's UI is small and mostly still, so WPF's software renderer is just as smooth here
        // while skipping the GPU driver stack, which roughly halves private memory. DWM still
        // composites the acrylic on the GPU. Set TACK_GPU=1 to compare.
        if (Environment.GetEnvironmentVariable("TACK_GPU") != "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _store?.Flush();
        _sync?.Dispose();
        _shell?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static readonly string[] WelcomeTips =
    [
        "Fold Tack into its orb with Esc — then drag the orb anywhere",
        "Drag the dots on the left to reorder",
        "Hover a task and click ◎ to make it your focus",
    ];

    private const string FirstTask = "Tick this circle to finish your first task";

    /// <summary>A first-run list that teaches Tack by using it. Delete or tick them off.</summary>
    private static void SeedWelcome(Workspace ws)
    {
        var tips = new List<string>(WelcomeTips);
        if (!string.IsNullOrEmpty(ws.Settings.Hotkey))
            tips.Insert(1, $"Press {ws.Settings.Hotkey} in any app to open or hide Tack");
        foreach (var tip in tips) ws.AddTask(tip);
        ws.AddTask(FirstTask, asNow: true);
        ws.YieldFocus();
    }

    /// <summary>One of the welcome tasks (whatever the shortcut was then).</summary>
    internal static bool IsWelcomeTip(string text) =>
        text == FirstTask || WelcomeTips.Contains(text)
        || (text.StartsWith("Press ", StringComparison.Ordinal) && text.EndsWith(" in any app to open or hide Tack", StringComparison.Ordinal));

    /// <summary>
    /// A small utility shouldn't vanish because of one unexpected UI error: log it, save, carry on.
    /// If errors repeat in quick succession, something is truly wrong, so let it close.
    /// </summary>
    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI error", e.Exception);
        _store?.Flush();

        var now = DateTime.UtcNow;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > TimeSpan.FromMinutes(1)) _recentErrors.Dequeue();
        e.Handled = _recentErrors.Count <= 3;
    }
}
