# Designing Tack

Tack exists to answer one question at a glance: *what was I doing?* Everything else is in service of
that, or it isn't in Tack.

## Principles

1. **Minimal footprint.** Collapsed, Tack is a 40 px orb. Expanded, it's a compact panel that remembers its size.
2. **Minimal friction.** Open, type, Enter, Esc. No dialogs, no confirmation prompts, no required fields.
3. **Maximum accessibility.** One click, one shortcut, or one hover away from any app on any monitor.
4. **Invisible when idle.** No timers, no polling, no background CPU. It hands memory back to Windows when folded.
5. **Your data stays yours.** Offline, one human-readable file. Tack never connects to anything; if you
   turn on sync, your OneDrive app carries a file between your devices, with no Tack server in between.

## Name and identity

*Tack* as in a thumbtack: something pinned to your screen. It also borrows the sailing sense of staying
on course. The mark is the orb itself: a dark glass bead, a progress arc, and a dot at the centre (the tack).
The app follows your Windows accent colour by default, so it feels native on every PC; five presets are
there for people who want it to stand out.

## Decisions, including where Tack departs from the brief

### The orb is always on top; "pin" decides whether the panel stays open
The brief made always-on-top a toggle. In practice, an orb that can sink behind windows defeats its
purpose: the moment you need it, it's buried. So the orb is always topmost, and the pin answers a more
useful question: **should the panel stay open while I work elsewhere?**

- **Pinned:** the panel stays open above other apps, like a checklist taped to your monitor.
- **Unpinned (default):** the panel behaves like a flyout and folds back into the orb when you click away.

For moments when even a 40 px orb is in the way, Tack steps aside on its own while a full-screen app
(video, game, slideshow) is in front on the same monitor, and returns afterwards.

### No tray icon
The brief rules out minimising to the tray, and Tack doesn't add a tray icon either: the orb *is* the
presence. Right-click it for Open, Settings and Quit. One less thing in the notification area.

### One task in focus, nothing else
Instead of priorities, tags or due dates, a task can be **Now**. There's exactly one, it sits in an
accent card at the top, and it's what the orb's hover peek shows. That's the whole "active task" model.
If nothing is in focus, the peek shows the top of the list as *Up next*.

### Spaces: apart when you want, invisible when you don't
Work, development and personal tasks pile into one list for people who do all three, so Tack has
optional **spaces**. They're deliberately thin: a space is a name, not a project with its own settings,
colours, priorities or due dates, and there are still no tags.

- **Nothing until you ask.** Everything starts in **Main**. Until there's a second space, Tack looks and
  behaves exactly as it did before spaces; the only sign of them is Settings → Spaces, where you make
  one (and can rename Main, say to "Work").
- **One importance order.** A space shows the one global order, filtered. Reordering inside a space moves
  a task past the neighbours you can see and never re-keys tasks in other spaces, so switching to All
  shows the order you'd expect.
- **Switching is one keystroke.** A slim strip under the header (`All · Main · Work · +`, plain text, the
  space on show in the primary text colour with an accent underline, no colour per space) and
  **Alt+1..9 / Alt+0** that work even while you're typing a task (the draft stays). On the phone, the
  title becomes the switcher ("Work ▾").
- **New things land where you're looking.** The add box says "Add to Work", and a new task or sheet goes
  into the space on show (from All, Main). Moving one elsewhere is a menu (Move to ▸, Ctrl+M) with Undo,
  and changes nothing else about it.
- **Still one Now.** The focus task keeps its card in its own space and in All; elsewhere it's a slim
  line, "Now in Work: …", that takes you there. Done, Clear, the orb's count and ring and the peek go by
  the space on show.
- **Each device chooses what it shows** (kept in its own settings, never synced), so the PC can stay on
  Work while the phone shows Personal. A space deleted on another device just shows Main.
- **Deleting a space loses nothing.** Its tasks and notes fall back to Main, and Undo restores it all.

### Progress that resets itself
The orb's ring is *today's* finished share of work: done today ÷ (done today + still open). It needs no
reset button, no streaks, no stats screen. Each morning it starts empty again.

### Undo instead of "Are you sure?"
Deleting a task or a sheet, clearing Done, and bulk-adding from a paste all show a toast with **Undo**.
Ticking a task has a short grace period (a second click un-ticks it) before it moves to Done.

### Notes are sheets of paper
A single scratchpad turns into a junk drawer; a full notes app is out of scope. Tack has *sheets*:
plain text, titled by their first line, switched with a pill strip. There's no formatting. A few quiet
helpers make it pleasant: list continuation, F5 timestamps (a nod to Notepad), and Ctrl+Enter to promote
a line to a task, which is the bridge between the two halves of the app.

### First run teaches by doing
There's no tour. The first run opens the panel with five tasks that *are* the instructions ("Tick this
circle…", "Drag the dots…", "Press ⟨shortcut⟩…"), so learning Tack means using it. If the default
shortcut is already taken on that PC, Tack picks a free fallback first, so the tip is always true.

### Each tab has its own size; the panel has one home
A task list and a page of notes want different shapes, so each tab remembers its own size:

- **Tasks fits its list.** Until you resize it, the panel is exactly as tall as the tasks it shows (with a
  floor for the empty state), grows as you add more, and stops at 620 DIP or three quarters of the
  screen, whichever is smaller. After that the list scrolls.
- **Notes keeps a writing-sized page** (340 × 480 by default).
- **Dragging an edge wins.** Whatever size you drag a tab to becomes that tab's size and automatic sizing
  stops for it. Double-click the header to hand it back to Tack.

Position, though, is shared: the panel always unfolds from the orb's corner. Giving each tab its own
position would make every tab switch move the window, the opposite of "no jumping". Instead, when the size
changes (tab switch, Settings, a task added or finished) the panel glides to its new size over 180 ms with
the corner nearest the orb held perfectly still, and is nudged back on screen if it would spill over the
edge. Sizes and that home position survive restarts.

### Light and dark
Tack follows Windows' light/dark app mode by default (and switches live when Windows does), or can be set
to either. Light mode is not an inverted dark mode: it uses deeper shades of each accent so white ticks
stay legible, a pale tint over the acrylic, and a porcelain orb with a soft grey shadow that still reads on
white documents.

## Visual language

- **Glass:** the official Windows 11 system backdrop (Acrylic), with a light tint on top (dark or pale,
  per theme) for contrast over busy or bright windows. Turn it off for a solid, high-contrast surface. Windows normally
  drops acrylic to a flat colour when a window is inactive; a pinned panel is inactive most of the time,
  so Tack keeps its frame drawn as active (see `WindowEffects.KeepFrameActive`).
- **Shape:** native rounded corners and shadow from DWM, a 1 px system border, 7–10 px radii inside.
- **Type:** Segoe UI Variable (falling back to Segoe UI), 13.5 px body, a semibold display weight for the
  focus task, small-caps style overlines for labels.
- **Motion:** every animation is between 90 and 260 ms and eased. The panel grows out of the corner the
  orb was in; ticks draw themselves; rows glide when reordered (FLIP); the tab underline slides.
  Nothing loops, so nothing animates while you're not interacting.
- **Hover-only chrome:** row actions appear in a small floating toolbar on hover, so they never take
  width from your text.

## Architecture

```
src/Tack
├── App.xaml(.cs)          Startup: single instance, data folder, first-run seeding, crash safety
├── AppShell.cs            The coordinator: orb ⇄ panel, placement, shortcut, full-screen, housekeeping
├── Model/                 Workspace (all user state), SpaceView (what the space on show shows), TaskItem, NoteSheet, Settings
├── Services/              StateStore (JSON persistence), FolderSync (sync through a folder), accent colour, startup, log, single instance
├── Sync/                  The data model shared with Tack Focus: clocks, sort keys, merges, device files, the sync engine
├── Interop/               Win32/DWM: glass, monitors & DPI, global hotkey, shell watcher
├── Windows/               OrbWindow, PeekWindow, PanelWindow
├── Views/                 TasksView, NotesView, SettingsView (UserControls inside the panel)
├── Controls/              OrbVisual (custom-drawn), animation helpers, converters
└── Themes/                Theme.xaml (typography and every control style), Dark.xaml / Light.xaml (palettes)
```

The model is plain `INotifyPropertyChanged` classes; views bind to them directly and keep interaction
logic in code-behind. For an app this size that's easier to follow than a full MVVM framework, and it
adds no dependencies. The project references **nothing** beyond .NET 8 and WPF.

Spaces sit between the two: `SpaceView` filters the workspace's lists for the space on show and
keeps them in step, and with no space besides Main (or in All) it hands the views the workspace's own
collections, which is what keeps the panel exactly as it was until a second space exists.

### Windows
Three top-level windows, each doing one job:

| Window | Kind | Why |
|---|---|---|
| Orb | Layered (per-pixel alpha), topmost, `WS_EX_NOACTIVATE` | Round shape, click-through corners, never steals focus |
| Peek | Layered, topmost, click-through | Informational only; can't get in the way |
| Panel | Normal window with DWM backdrop, topmost | Real acrylic, native corners/shadow, resizable |

### Placement across monitors
All placement is done in **physical pixels** through `SetWindowPos`, never WPF's `Left`/`Top`
(which are DPI-relative and unreliable across mixed-DPI monitors). Sizes are stored in DIPs and converted
with the target monitor's DPI. WPF applies DPI changes to hidden windows late, so `ScreenGeometry.PlaceExact`
checks WPF's scale against `GetDpiForWindow` and replays `WM_DPICHANGED` when they disagree.

The panel unfolds from the orb toward the middle of the screen, so the orb's corner of the panel sits
exactly where the orb was. Move the panel and the orb's home moves to the panel's nearest corner. Remembered
positions are clamped back onto a connected monitor at startup and whenever displays change.

Resizing while open goes through `WindowBoundsAnimator`, which interpolates each edge separately, so the
anchored edges don't move by even a pixel, and retargets smoothly if the size changes again mid-glide
(as it does every frame while a finished task's row folds away). Auto-fit measures the Tasks view at the
panel's width with unlimited height; a manual resize is recognised by `WM_SIZING` between
`WM_ENTERSIZEMOVE` and `WM_EXITSIZEMOVE`, so Tack's own resizing never counts as yours.

### Theming
Every neutral colour lives in a palette dictionary (`Themes/Dark.xaml` or `Themes/Light.xaml`, same keys)
merged first into the application resources, and every style references it with `DynamicResource`.
`ThemeManager` swaps that one dictionary and everything restyles in place, with no window rebuilt. Accent
brushes are generated in code (`AccentPalette`) in the right shade for the theme. The custom-drawn orb
picks its materials from `ThemeManager.IsLight`, and DWM is told the theme so the acrylic is tinted to match.

### Desktop awareness without polling
- Foreground changes: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`, used to re-assert topmost and to check
  for full-screen apps, plus up to three re-checks over the next 1.2 s for apps that go full-screen just
  after taking focus.
- Full-screen apps: an app-bar registration (`ABM_NEW`, never sized, so it reserves no space) delivers the
  shell's `ABN_FULLSCREENAPP` notifications.
- Global shortcut: `RegisterHotKey` on a message-only window.
- Second launch: a named event wakes the running instance; the new process exits.

### Persistence
`StateStore` serialises the workspace with the System.Text.Json source generator (no reflection) to
`tack.json`. Writes are debounced (600 ms), skipped when nothing changed, written to a temp file with
write-through, then swapped in with `File.Replace`, which also keeps the previous version as `tack.json.bak`.
Unreadable files are kept aside (`tack.json.unreadable-<time>`) rather than overwritten. Loading tolerates
a BOM, comments and trailing commas, for people who edit the file by hand.

The file also carries Tack Focus's `tackFocus` section (`StateFile`, `Sync/`): per-field hybrid
logical clocks, fractional sort keys, tombstones and the focus register, the model in
[SYNC.md](SYNC.md). The top level is still written exactly as before and is the source of truth for
what you see: a hand edit there (or a file saved by 1.1, which drops the section) is taken as a fresh
edit, and the section's records are checked one by one, so a damaged section never stops the file
from loading. `Workspace` stamps every mutation the same way Tack Focus does, and keeps the open
list sorted by key, so both apps produce the same records for the same edits.

### Staying light
- **Software rendering.** Tack's UI is small and mostly still, so WPF's software renderer is just as smooth
  here while skipping the GPU driver stack: roughly half the private memory. DWM still composites the
  acrylic on the GPU. (`TACK_GPU=1` switches back, for comparison.)
- **Uncompressed single-file exe.** A compressed bundle has to be unpacked into RAM (~90 MB extra);
  uncompressed, it's memory-mapped. The release zip does the compressing instead.
- **Slim bundle.** The portable build drops Windows Forms and a few other unused framework libraries (~30 MB).
  WPF's native libraries can't load from inside the bundle, so .NET unpacks them (~8 MB) to
  `%TEMP%\.net\Tack` once; shipping them beside the exe would break the one-file download.
- **Workstation GC without the background thread**, English-only framework resources.
- **Idle trim.** A few seconds after the panel folds, Tack runs a compacting GC and trims its working set.

## Why WPF?

The first choice was a native Win32/Direct2D app (~1 MB, ~10 MB RAM), but it would have meant writing a
text editor, IME support and accessibility from scratch. WPF on .NET 8 gives robust text editing, UI
Automation for screen readers, per-monitor DPI and composited animation, with no third-party dependencies.
It costs more memory than native C++, but far less than a web-view app, and with the measures above it idles
at a few MB of working set and zero CPU.

## Known limitations and ideas

- **Windows 10** gets a solid panel with square corners (no system backdrop API there).
- **Not code-signed**, so SmartScreen warns on first run. Signing is the next distribution step.
- **No arm64 release yet.** `./build.ps1 -Runtime win-arm64` builds one, but it hasn't been tested on hardware.
- **Localisation** isn't wired up; strings are in XAML and code.
- **Keyboard access to row actions** goes through shortcuts (Ctrl+Enter, Ctrl+D, Backspace) and the context
  menu (Shift+F10), not Tab, because the hover toolbar isn't in the tab order.
- Deliberately **not** planned: reminders, due dates, priorities, tags, collaboration. Spaces (above) are as
  far as grouping goes: a name to keep work, development and personal apart, not projects with settings.
- **Sync** with Tack Focus is opt-in and serverless: through a folder in your own OneDrive that the
  OneDrive app keeps in step, never a Tack server, and no network code in Tack. See [SYNC.md](SYNC.md).
  Google Drive and end-to-end encryption are for later.
