using System.Windows.Input;
using System.Windows.Interop;
using static Tack.Interop.NativeMethods;

namespace Tack.Interop;

/// <summary>A keyboard shortcut such as Ctrl+Alt+Space.</summary>
internal readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static readonly Hotkey Default = new(ModifierKeys.Control | ModifierKeys.Alt, Key.Space);

    public bool IsValid => Key != Key.None && Modifiers != ModifierKeys.None && !IsModifierKey(Key);

    public override string ToString()
    {
        if (Key == Key.None) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        var mods = ModifierKeys.None;
        var key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (Enum.TryParse(raw, true, out Key k)) key = k;
                    break;
            }
        }
        return new Hotkey(mods, key);
    }

    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        Key.OemTilde => "`",
        Key.OemPeriod => "Period",
        Key.OemComma => "Comma",
        _ => k.ToString(),
    };
}

/// <summary>
/// Registers one system-wide shortcut through a message-only window.
/// Costs nothing while idle: Windows posts WM_HOTKEY only when the keys are pressed.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0x7AC1;
    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("TackHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers the shortcut, replacing any previous one. Returns false if another app owns it.</summary>
    public bool Register(Hotkey hotkey)
    {
        Unregister();
        if (!hotkey.IsValid) return false;

        uint mods = MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        _registered = RegisterHotKey(_source.Handle, HotkeyId, mods, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
