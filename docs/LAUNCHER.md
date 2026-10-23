# Radial launcher (optional)

An optional ring of shortcuts around Tack's orb, for the apps, folders and websites you open most. It's
**off by default**; turn it on in **Settings → Launcher**. Off, Tack is exactly the Tack it was: the orb
behaves as before, nothing extra is loaded beyond reading a settings file if one exists, and no
`launcher.json` is ever created.

## Using it

| | |
|---|---|
| **Open (click)** | **Shift+click** the orb. The shortcuts expand around it. Click one to open it; click a group to fan out its shortcuts. Click the orb again, press **Esc**, or click anywhere else to close. |
| **Open (gesture)** | **Shift+press** the orb and, without letting go, drag toward a shortcut (it highlights) and release. Release over nothing, over an empty position or back on the orb, and nothing happens. |
| **Groups** | Drag onto a group and its shortcuts fan out beyond it; carry on to one and release. Releasing on the group itself just leaves it open. |
| **Choose a display** | Keep dragging *past* an app or folder (or rest on it a moment) and a small map of your displays appears; drag onto one and release. With clicks: right-click a shortcut, or rest on it. Shortcuts can also always ask, or remember a display (Settings). |
| **Keyboard** (after Shift+click) | Arrows/Tab move, **Enter** or **Space** opens, **M**, **Shift+Enter** or the Menu key shows the displays, **1–6** (top row or number pad) open a position, **Esc** steps back and closes, **Alt+F4** closes. The display map never opens by itself while you use the keys (only when the pointer rests on a shortcut). |
| **Narrator** | Each shortcut and display is a button with its name and what it opens; the keyboard focus follows the highlight, and messages ("can't be found", "Opening…") are read out. |

Shift+click and Shift+drag are told apart by movement only: a press that never moves more than 7 DIPs
is a click, however long you hold it. Ordinary (no Shift) clicks and drags of the orb are untouched.

## How it's built

Everything lives in `src/Tack/Launcher/`, plus four small hooks in existing files. No new dependencies,
no framework, no change to tasks, notes, tack.json or sync.

| File | What |
|---|---|
| `Core/` | Platform-neutral and unit-tested (`tests/Tack.Tests/LauncherTests.cs`): config and `launcher.json` (`LauncherConfig`, `LauncherConfigFile`), shortcut rules (`ShortcutRules`), layout (`RadialLayout`, `LauncherScene`), the interaction state machine (`LauncherSession`) and window-placement maths (`Placement`). |
| `LauncherModule.cs` | The controller: settings, opening/closing the window, routing input, launching. Every entry point catches its own errors: a launcher failure is logged, the launcher closes, Tack carries on. |
| `RadialWindow.cs` | The launcher window. Created when opened and **closed** (not hidden) when closed. Draws what the session says, forwards input. |
| `LauncherDesktop.cs` | Windows side: display enumeration, `ShellExecute` launching, and best-effort window placement (`WindowPlacer`). |
| `LauncherIcons.cs` | Shell icons for programs and folders (cached per path); glyphs for websites and groups. |
| `LauncherSettingsView.cs` | Settings → Launcher: the switch, six positions, a compact editor. |

Hooks in existing code: `OrbWindow` hands a press to the launcher only when it's on **and** Shift is
held; `AppShell` creates the module and closes the launcher when the panel opens, a full-screen app
appears or displays change; `SettingsView` hosts the settings card; `NativeMethods` gains the P/Invoke
declarations (all in one place, as before).

### Interaction state machine (`LauncherSession`)

`Closed → Armed` (Shift+press) → `Gesture` (moved ≥ 7 DIPs) or `Click` (released without moving).
In `Gesture`, release launches the highlighted shortcut or chosen display, leaves a group open (→ `Click`),
or cancels. In `Click`, a press and release on the same target activates it; on different targets nothing
happens. Gestures choose by direction within each ring's band, so imprecise or fast movements still land;
clicks need to be on (or very near) the shortcut. While a group is open during a gesture, passing over a
neighbour doesn't close it; resting on the neighbour for 350 ms, or heading back toward the orb, does.
Time is passed in (no clocks inside), which is what makes all of this testable.

### Layout (`RadialLayout`)

All in physical pixels, on the orb's monitor's work area (never across a monitor boundary: one DPI per
window, nothing out of reach). Where there's room, six fixed positions at 60° steps from the top; near an
edge or corner, an evenly spaced inward-facing arc in the widest open direction, its radius grown only as
far as needed to keep shortcuts apart (up to 190 DIPs), numbered from the top end (or the left end on a
top/bottom edge). The orb stays the centre and never moves. A group's shortcuts fan out beyond it within
about 100°, nudged inward near edges. The display picker sits just beyond the shortcut, kept on screen and
clear of the orb and of the other shortcuts on show. A shortcut's name goes on its far side from the orb, or
beside, below or above it, wherever it covers least; with the picker open, the picker's caption names the
shortcut and display instead. Negative coordinates and odd arrangements are just numbers to it.

### Launching

Three distinct shortcut types, validated separately (`ShortcutRules`):

- **App**: a full path to `.exe`, `.lnk`, `.appref-ms`, `.url`, `.bat` or `.cmd`, or a bare program name
  (`notepad.exe`) that Windows finds through App Paths/PATH. Optional arguments are kept separately.
- **Folder**: a full path to an existing folder; opened through the shell (File Explorer).
- **Website**: `http://` or `https://` only (`github.com` gets `https://`). `file:`, `javascript:`,
  custom protocols and paths are refused.

All three go through `ShellExecute` (`Process.Start` with `UseShellExecute`): the program, folder or
address is the thing opened and arguments are passed as their own field. Nothing is ever joined into a
command line or run through `cmd`. Paths with quotes, pipes, wildcards or line breaks are refused.
`%VARIABLES%` are expanded and `/` is read as `\`. Declining Windows' admin prompt is not an error. A missing
or malformed target shows a red dot on the shortcut and a short reason when chosen; nothing launches.

`ShellExecute` runs on a short-lived background thread: opening something on a server that can't be reached
keeps Windows busy for 20 s or more, and Tack's orb, panel and launcher must not freeze meanwhile. Most
shortcuts start within a moment, and the launcher then closes (or says why it couldn't open). After 0.4 s it
says "Opening …" and can be dismissed (Esc, a click elsewhere); a failure that arrives after that is logged.

### Display targeting (best effort)

- **Displays**: `EnumDisplayMonitors` + `GetMonitorInfo` (bounds, work area, device name) +
  `GetDpiForMonitor`, read each time the launcher opens. The picker draws the real arrangement; numbers go
  left to right, top to bottom (not Windows' internal numbering), and the main display says "(main)". The
  orb's display is marked with a dot.
- **Finding the window**: before launching, a one-off list of visible top-level windows. After launching,
  two out-of-context WinEvent hooks (`EVENT_OBJECT_SHOW`, `EVENT_SYSTEM_FOREGROUND`) for at most 15 s,
  extended once to 60 s if the app shows a splash screen first. A window counts if it belongs to the started
  process or to the expected program (`QueryFullProcessImageName`), is new, top-level, unowned, not a tool
  window, not cloaked, has a frame and a sensible size. Splash screens (frameless popups) are skipped.
- **Already-running / single-instance apps**: if the started process exits and an existing window of that
  program comes forward, Tack waits 1.2 s for a new window (VS Code opens one per folder) and otherwise
  moves the one that came forward. While the started process is still running, it's a new instance and
  existing windows are left alone. The window you were in before opening the launcher is never moved.
- **Moving it**: normal windows keep their relative position in the work area and end up resized by the
  displays' scale ratio (so they look the same size), clamped to the target's work area (`SetWindowPos`,
  no activation). Who does the resizing depends on the app (`GetWindowDpiAwarenessContext`): a
  per-monitor-aware app (Explorer, Notepad, browsers, Office, WPF, Electron) rescales itself when it arrives
  (`WM_DPICHANGED`), so it's moved at its current size; any other window is given the scaled size. If the
  size still comes out more than 3% off, it's corrected once. Maximised/minimised windows get a restored
  rectangle on the target via `SetWindowPlacement` (workspace coordinates converted, same size rule), then
  are maximised again there.
- **Apps that move themselves back** (restoring a saved position): checked at 0.35, 0.9 and 2 s; at most two
  corrections, then Tack lets go.
- **Elevated apps**: Windows doesn't let a normal process move an administrator's window (UIPI). Tack logs
  it and leaves the window where it opened. It never asks for admin rights to move a window.
- **Websites** don't offer a display: they open in the browser's existing windows.
- **Unplugged display**: a remembered display is matched by device name and bounds; if it's gone the
  shortcut opens normally (Settings shows "Unplugged").

## Resource use

- **Off**: one `File.Exists` at start-up (and a small read if `launcher.json` exists). No window, timer,
  hook or icon. The orb asks the launcher nothing (`OrbWindow.Launcher` is null).
- **On, closed**: the loaded settings (a few KB) and the icon cache (a few KB per shortcut). No window, no
  timer, no hook, no polling. Tack's existing memory trim runs 4 s after the launcher closes.
- **Open**: one small layered window around the orb, and a 40 ms timer (hover delays; Esc during a gesture,
  read with `GetAsyncKeyState` rather than a keyboard hook). Both end when it closes.
- **After a display-targeted launch**: the two WinEvent hooks and three one-shot timers above, for at most
  15 s (60 s with a splash screen), then nothing.
- No network access, no usage tracking, no file indexing, no tray icon. Icons come from the shell; websites
  get a globe glyph rather than a downloaded favicon.

## Limitations

- Placement is best effort. Apps that restore their own position after the checks, re-create their main
  window late (after 15 s without a splash screen), or don't expose a normal framed top-level window may
  open where they like. Apps running as administrator can't be moved.
- **Revit / Navisworks** (not testable here): both show a splash screen and create the main window late,
  which extends the wait to 60 s; both restore their last position and may maximise, which the checks
  correct twice at most. If Revit is installed per-machine and started normally it isn't elevated, so moving
  should be allowed; if it's set to run as administrator it won't move. Needs checking on a real install.
- `.lnk`, `.url` and `.appref-ms` shortcuts are recognised only by the started process (Tack doesn't resolve
  the link), so single-instance apps behind a `.lnk` may not be moved. Pointing the shortcut at the `.exe`
  fixes that. `.bat`/`.cmd` aren't moved.
- A single-instance app already in front while the launcher is used by gesture may not report a window
  change; it then stays where it is.
- Network paths (`\\server\share`, `//server/share`) and mapped network drives aren't checked for existence
  (an unreachable server would stall the launcher or Settings); Windows reports a missing one when it's
  opened.
- The launcher stays on the orb's monitor; it doesn't spill onto a neighbouring display.
- Icons are the shell's 32 px icons, drawn at 22 DIPs: slightly soft above 150% scaling.
