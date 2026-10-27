using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Tack.Model;
using Tack.Services;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// A copy of Tack in this process, on the real displays (for --frames and --screens): its own data in a temp
/// folder, no shortcut, never launched at startup, TACK_INSTANCE=uitestLook (or a uitest… name already set), per-monitor DPI as Tack.exe's
/// manifest asks. Nothing is typed or clicked; a script drives it the way Tack's own handlers do.
/// Starting a copy of Tack with launch at startup off removes HKCU\...\Run "Tack"; the value found is put back.
/// </summary>
internal static class TestCopy
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>Runs <paramref name="script"/> against a fresh copy of Tack seeded by <paramref name="seed"/>. Returns 0, or 1 if it failed.</summary>
    public static int Run(Action<Workspace> seed, Func<AppShell, Workspace, Task> script, Action? beforeWindows = null)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        string? runBefore = ReadRun();
        // uitestLook, or another uitest… name already set (so two test copies can run side by side).
        string name = Environment.GetEnvironmentVariable("TACK_INSTANCE") is { } set && set.StartsWith("uitest", StringComparison.Ordinal)
            && set.Length <= 40 && set.All(char.IsAsciiLetterOrDigit) ? set : "uitestLook";
        Environment.SetEnvironmentVariable("TACK_INSTANCE", name);
        using var instance = SingleInstance.TryAcquire();
        if (instance == null)
        {
            Console.WriteLine($"Another {name} copy of Tack is running; close it first.");
            return 2;
        }
        string folder = Path.Combine(Path.GetTempPath(), "tack-" + name, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var settings = new Settings { Theme = "dark", Glass = true, Pinned = true, Hotkey = "", LaunchAtStartup = false };
            var seeded = new Workspace(settings, new Hlc("uitest01"));
            seed(seeded);
            File.WriteAllBytes(Path.Combine(folder, "tack.json"),
                JsonSerializer.SerializeToUtf8Bytes(StateFile.ToDocument(seeded), StateJsonContext.Default.StateDocument));

            App.ConfigureRendering();
            beforeWindows?.Invoke();
            // A plain Application with App.xaml's dictionaries, never Tack's own App: its startup would start a
            // second Tack on the real data folder (here it only stops, as this process holds the instance).
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string dictionary in new[] { "Dark", "Theme" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Tack;component/Themes/{dictionary}.xaml") });
            Exception? failure = null;
            app.DispatcherUnhandledException += (_, e) =>
            {
                failure ??= e.Exception;
                e.Handled = true;
            };

            var store = new StateStore(folder);
            Log.Initialize(folder);
            var ws = store.Attach(store.Load());
            var sync = new FolderSync(ws, store);
            var shell = new AppShell(ws, store, sync);
            shell.Start(openPanel: false);

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try { await script(shell, ws); }
                catch (Exception ex) { failure ??= ex; }
                finally { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);

            shell.Dispose();
            sync.Dispose();
            if (failure == null) return 0;
            Console.WriteLine("The run failed: " + failure);
            return 1;
        }
        finally
        {
            RestoreRun(runBefore);
            try { Directory.Delete(folder, recursive: true); } catch { /* a log file still open: leave it */ }
        }
    }

    public static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? ReadRun()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Tack") as string;
    }

    /// <summary>Puts HKCU\...\Run "Tack" back exactly as it was.</summary>
    private static void RestoreRun(string? before)
    {
        if (before != null && ReadRun() != before)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue("Tack", before);
        }
        string? now = ReadRun();
        Console.WriteLine($"Run \"Tack\": {(now == before ? "as it was" : "CHANGED")}: {now ?? "(none)"}");
    }

    // ---------------------------------------------------------------- the displays

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE devMode);

    /// <summary>How many times a second a display refreshes (60 if Windows doesn't say).</summary>
    public static int RefreshRate(string device)
    {
        var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(device, -1, ref mode) && mode.dmDisplayFrequency is >= 24 and <= 500 ? (int)mode.dmDisplayFrequency : 60;
    }

    /// <summary>How many times a second the display a window is on refreshes.</summary>
    public static int RefreshRate(IntPtr hwnd)
    {
        var info = new Tack.Interop.NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<Tack.Interop.NativeMethods.MONITORINFOEX>() };
        var monitor = Tack.Interop.NativeMethods.MonitorFromWindow(hwnd, Tack.Interop.NativeMethods.MONITOR_DEFAULTTONEAREST);
        return Tack.Interop.NativeMethods.GetMonitorInfoEx(monitor, ref info) ? RefreshRate(info.szDevice) : 60;
    }

    /// <summary>The connected displays, for the report: size, scale, refresh rate, which is the main one.</summary>
    public static List<string> Displays() =>
        Tack.Launcher.DisplayInfo.Current()
            .Select(d => $"{d.Device} {d.Bounds.Width}x{d.Bounds.Height} at {d.Scale:P0}, {RefreshRate(d.Device)} Hz{(d.Primary ? " (main)" : "")}")
            .ToList();

    // ---------------------------------------------------------------- the screen

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    /// <summary>What the screen shows in a rectangle (physical pixels), as DWM composed it: acrylic, layered windows and all.</summary>
    public static BitmapSource Capture(Int32Rect r)
    {
        IntPtr screen = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, r.Width, r.Height);
        try
        {
            IntPtr old = SelectObject(dc, bitmap);
            BitBlt(dc, 0, 0, r.Width, r.Height, screen, r.X, r.Y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
            SelectObject(dc, old);
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static void SavePng(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>The colour at a pixel of a captured image.</summary>
    public static Color Pixel(BitmapSource image, int x, int y)
    {
        var px = new byte[4];
        var converted = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Bgr32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }
}
