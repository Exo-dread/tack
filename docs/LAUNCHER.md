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
| **Choose a display** | Keep **Shift** held while you rest on an app or folder a moment and a small map of your displays appears; drag onto one and release. With clicks: right-click a shortcut, or hold Shift while the pointer rests on it. The map never opens by itself: passing over a shortcut, resting on it or dragging on past it without Shift opens nothing, and releasing on it opens it normally. Shortcuts can also always ask, or remember a display (Settings). |
| **Recent files** (optional) | With **Settings → Launcher → Recent files** on, rest on an app a moment (without Shift) and the files it opened last fan out beside it (up to 8); drag onto one and release, or click it, and it opens with that app. While dragging, let go of Shift before resting to see them (Shift held shows the displays). With clicks: rest on the app, or press **R**. Off by default: resting then opens nothing. |
| **Keyboard** (after Shift+click) | Arrows/Tab move, **Enter** or **Space** opens, **M**, **Shift+Enter** or the Menu key shows the displays, **R** shows an app's recent files, **1–6** (top row or number pad) open a position, **Esc** steps back and closes, **Alt+F4** closes. The display map never opens by itself while you use the keys, Shift+Tab included (only M, Shift+Enter, or Shift held while the pointer rests on a shortcut). |
| **Narrator** | Each shortcut and display is a button with its name and what it opens; the keyboard focus follows the highlight, and messages ("can't be found", "Opening…") are read out. |

Shift+click and Shift+drag are told apart by movement only: a press that never moves more than 7 DIPs
is a click, however long you hold it. Ordinary (no Shift) clicks and drags of the orb are untouched.

## How it's built

Everything lives in `src/Tack/Launcher/`, plus four small hooks in existing files. No new dependencies,
no framework, no change to tasks, notes, tack.json or sync.

| File | What |
|---|---|
| `Core/` | Platform-neutral and unit-tested (`tests/Tack.Tests/LauncherTests.cs`, `LauncherRecentsTests.cs`): config and `launcher.json` (`LauncherConfig`, `LauncherConfigFile`), shortcut rules (`ShortcutRules`), layout (`RadialLayout`, `LauncherScene`), the interaction state machine (`LauncherSession`), window-placement maths (`Placement`) and picking an app's recent files from its lists (`RecentFiles`). |
| `LauncherModule.cs` | The controller: settings, opening/closing the window, routing input, launching. Every entry point catches its own errors: a launcher failure is logged, the launcher closes, Tack carries on. |
| `RadialWindow.cs` | The launcher window. Created when opened and **closed** (not hidden) when closed. Draws what the session says, forwards input. |
| `LauncherDesktop.cs` | Windows side: display enumeration, `ShellExecute` launching, and best-effort window placement (`WindowPlacer`). |
| `LauncherRecents.cs` | Windows side of recent files: Jump Lists (`IApplicationDocumentLists`), the Start menu's app IDs, Store apps' execution aliases, the registry, read on a background thread while the launcher opens. |
| `LauncherIcons.cs` | Shell icons for programs and folders (cached per path), and for kinds of file (by extension); glyphs for websites and groups. |
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

### Recent files (optional, off by default)

An app shortcut can show the files that app opened last, so a model is one gesture away: Shift+drag to Revit,
rest a moment, drag onto the model, let go.

- **Where they come from**, best first, up to 8 that are there:
  1. **The app's Jump List**, the list Windows keeps per app (`IApplicationDocumentLists`, read-only). It's kept
     under the app's AppUserModelID, which Tack finds from the program the shortcut starts: the Start menu entry
     for that very program (`shell:AppsFolder`: Revit, Navisworks and AutoCAD get `Microsoft.AutoGenerated.{…}`
     IDs from their Start menu shortcuts), a Store app's ID through its execution alias (`notepad.exe` → the new
     Notepad), the ID an app sets itself for another copy of it (VS Code installed for everyone and for you), and
     last the ID Windows makes from the program's path.
  2. **The app's own list**, when the Jump List gives fewer than 8: Revit's `Revit.ini` (`[Recent File List]`,
     in `%APPDATA%\Autodesk\Revit\Autodesk Revit 2024\`), AutoCAD's and Navisworks' `Recent File List` in the
     registry (`HKCU\Software\Autodesk\AutoCAD\R25.1\ACAD-…\`, `HKCU\Software\Autodesk\Navisworks Manage\24.0\`).
     Each is matched to the release installed in the shortcut's program folder (`AcadLocation`, Navisworks'
     `Location`), so Revit 2024 never shows Revit 2022's files.
- **When**: read each time the launcher opens, on one short-lived background thread, and only then: no polling,
  no file watching, nothing cached between openings except the Start menu's app list (about a second to read;
  kept, and read again after 30 minutes, on an opening). Only with the option on, and only for app shortcuts
  that point at a program (`.exe`). Nothing is written anywhere, and nothing comes from synced data.
- **What's shown**: full local or network paths to documents that exist. Entries that aren't (programs, scripts,
  shortcuts, web or cloud addresses like Revit's `cld://`, device paths, `..`, line breaks) are dropped: these lists
  can be written by any program you run. Files on network shares aren't checked (Tack never contacts a server by
  itself; Windows says so if one has gone). If Windows is set not to keep recently opened items (Settings →
  Personalization → Start, or the policy), there are none, from any list.
- **Gestures**: resting on an app with recent files (260 ms dragging, 550 ms with clicks) without Shift shows
  them; with Shift held, the rest opens the display picker instead (and puts a card that's out away), and so does
  right-click. An app with none shows nothing after a rest without Shift. While the card is out, passing over a neighbouring
  shortcut doesn't put it away (as with an open group); resting on the neighbour, or heading back toward the orb,
  does. Releasing on the app itself opens the app. Keys: **R** shows them, arrows move, **Enter** opens, **M**
  chooses a display, **Esc** steps back.
- **Opening one**: with that app, through the same rules as the app itself. Normally the app's program is started
  with the file as its own argument, after the shortcut's arguments (never through a command line), so a Revit 2024
  model opens in Revit 2024 even where `.rvt` files open in Revit 2022. When Windows opens that kind of file with
  that very program anyway, the file itself is opened instead, which lets a running copy take it (AutoCAD with
  `.dwg`). It follows the shortcut's display choice: a remembered display, or the picker if the shortcut asks.
- **The card**: a list (icon, name, extension) placed like the display picker, beyond the app and clear of the
  orb; its caption names the app, then the highlighted file's folder. The app keeps an accent ring while it's out.

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
- **Recent files on**: while opening, one background thread reads the app shortcuts' lists and ends (typically
  0.1–0.3 s; up to about a second more the first time, for the Start menu's app list). Kept while closed: that app
  list (a few tens of KB) and one small icon per kind of file shown.
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
- Recent files: an app that keeps no Jump List and no list Tack knows shows none. Revit's cloud models (`cld://`)
  aren't files on this PC and aren't shown. A file that has moved to another drive letter (a Google Drive or
  network drive remapped) isn't found under its old path. Opening a `.dwg` with `acad.exe` while AutoCAD is already
  open starts a second AutoCAD, unless `.dwg` files open with that AutoCAD (then the running one takes it).
  Shortcuts that point at a `.lnk` don't show recent files: point them at the program.
