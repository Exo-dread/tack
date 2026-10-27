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
   What only makes sense on one PC (launcher shortcuts, snippets, what a dropped task points to) sits
   beside that file and never leaves the PC.

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
- **Switching is one keystroke.** A slim strip of pills under the header (`All`, `Main`, `Work`, `+`: rounded,
  24 px, on the theme's subtle fill with a hairline; the space on show filled with the accent, its name in the
  accent's text colour, so it's a filled shape among outlined ones and never colour alone; no colour per
  space; the fill cross-fades to the new pill as the space changes, if Windows animates) and
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

### Search: one box, everything, then out of the way
With spaces, a Done list and sheets of notes, "where did I write that?" needs an answer that isn't
scrolling. **Ctrl+F** opens a box over the tabs (nothing else moves, and nothing shows until then) and
lists what matches as you type: open tasks, finished tasks and notes, from every space at once,
grouped, each with its space. It's deliberately plain:

- **One rule, both apps.** A query matches text that contains it, anywhere (all of a note), ignoring
  case and accents, with curly quotes, dashes and Unicode spaces taken as their plain forms. Both
  apps fold text the same way, a character at a time, and are held to the same cases
  (`tests/shared/search.json`), so the phone and the PC never disagree about what's there.
- **Going there is the point.** Enter opens the result where it lives: its space (unless All shows
  it), its tab, the task scrolled to and lit up for a moment, or the sheet with the match selected.
  Esc puts you back where you were.
- **Quick at any size.** Text is folded once per task or sheet and kept until it changes, so a
  keystroke only compares; each group draws its first fifty results, with Show more for the rest.
  No index on disk, and nothing runs while you aren't searching.

### The launcher: optional, and kept apart
The radial launcher is the one feature that isn't about *what was I doing?*: a ring of shortcuts around the
orb for the apps and folders you open all day, because the orb is already where your hand goes. So it's held
to stricter rules than anything else: off by default, reached only with Shift (ordinary clicks and drags of
the orb are untouched), settings in its own `launcher.json` that tack.json and sync never see, and, off,
nothing loaded beyond checking that file. **Recent files** (off by default too) answers "the model I had open
this morning": resting on an app shows the files it opened last, read from the app's Jump List and Revit's,
AutoCAD's and Navisworks' own lists on one short-lived thread as the launcher opens, and never stored.
Details: [LAUNCHER.md](LAUNCHER.md).

### What stays on this PC
Some things only make sense on the PC where they happened: a path on its disk, a screenshot taken on it,
its own shortcuts. They live in files of their own beside tack.json, and the rule is the same for each:

| File | What | Written |
|---|---|---|
| `launcher.json` | The launcher's shortcuts | Only from Settings → Launcher |
| `attachments.json`, `attachments\` | What each task dropped onto the orb points to (a path or a link), and copies of emails | Only by a drop |
| `snippets.json`, `snippets\` | Images pasted into Notes, by sheet | Only by a paste |

- **Never in tack.json, never synced, imported or exported.** The sync format, device files and the shared
  vectors don't know these exist, so every other device, and older versions of Tack, are untouched. On the
  phone a dropped task is its text and a sheet is its text.
- **Nothing until used.** None of these files or folders exists until the feature is first used; until
  then Tack does no more than look for them at startup.
- **Read like anything else from outside.** Each is read entry by entry with strict checks, bounded in size
  and count, and a file name is only ever made from an id Tack chose. What a damaged or hand-edited file
  says never becomes a path Tack wouldn't have written itself, or something to launch that a drop couldn't
  have made. Hostile cases for each are in `tests/Tack.Tests/HostileFileTests.cs`.
- **Tidied with what they belong to.** Deleting a task or a sheet for good (once its Undo has lapsed, or
  another device deleted it) lets its attachment or snippets go, and startup clears files nothing points to.
- **Never on a guess.** If tack.json and its backup can't be read, Tack starts empty (keeping the file
  aside), and nothing beside it is let go all session: the sheets and tasks it belongs to may come back.
  An index of these that can't be read is kept aside too, and it and its folder are left exactly as they
  are (as for one a later version wrote) until it reads again.

### Dropping onto the orb: capture without switching
The orb is always on screen, so it's also the quickest place to put something you've just seen: an email in
Outlook, a clash report in Explorer, a page in the browser. Drag it onto the orb and it's a task, and you never
left the app you were in. The orb lights up (an accent halo and a **+** in place of the count) and a bubble
beside it says what letting go will do; afterwards the same bubble says what was added, with Undo, and steps
aside on its own. Like the panel, it never takes focus.

- **One task per item, named the way you'd name it:** a file by its name without the extension, an email by
  its subject, a page by its title, text one line at a time (as a paste). In the space on show, first on top.
- **The task is what syncs; what it points to stays on this PC.** The path, link or saved email lives in
  `attachments.json` and an `attachments` folder beside tack.json, never in tack.json, never synced, imported
  or exported. A paperclip on the row (Ctrl+O, or the task's menu) opens it, through the launcher's rules:
  ShellExecute on a full path, or an http(s) address in the browser. A task from another device never has one.
- **Emails are kept, everything else is linked.** An email (or anything an app drags as a "virtual file") has
  no place on disk, so Tack saves a copy, marked as from outside the PC as a browser marks a download. A file
  in a temporary folder is copied too, since whoever put it there will clean it away. Programs are never kept.
- **Bounded, and never a move.** More than 20 items is refused before any of it is read; text is read up to
  2 MB, a copy up to 50 MB, the folder up to 500 MB. Tack tells the app it came from that it linked or copied,
  never that it moved, so an email stays in your mailbox and dragged text stays where it was.
- **Nothing lingers.** When a task is deleted for good (its Undo has lapsed, or another device deleted it
  some minutes ago), its attachment goes, and the saved copy with it.
- **Nothing until you use it.** Until something is dragged over the orb, the only cost is one look for
  attachments.json at startup; the bubble is made the first time.

### Progress that resets itself
The orb's ring is *today's* finished share of work: done today ÷ (done today + still open). It needs no
reset button, no streaks, no stats screen. Each morning it starts empty again.

### Undo instead of "Are you sure?"
Deleting a task or a sheet, clearing Done, bulk-adding from a paste, discarding a snippet and dropping onto
the orb all show a toast with **Undo** (a drop also offers it in the bubble beside the orb: the same Undo).
There's one Undo on offer at a time; whatever waits on it (a discarded snippet's file, a deleted task's
attachment) goes only once it can no longer be used.
Ticking a task has a short grace period (a second click un-ticks it) before it moves to Done.

### Notes are sheets of paper
A single scratchpad turns into a junk drawer; a full notes app is out of scope. Tack has *sheets*:
plain text, titled by their first line, switched with a pill strip. There's no formatting. A few quiet
helpers make it pleasant: list continuation, F5 timestamps (a nod to Notepad), and Ctrl+Enter to promote
a line to a task, which is the bridge between the two halves of the app.

### Snippets: a screenshot to follow, then gone
A lot of what people jot down is really a picture: a few steps of instructions snipped from a manual,
an email or a dialog, to follow in another app and throw away. So **Ctrl+V with an image** puts it on
the sheet as a *snippet*: a thumbnail strip above the text, a click to see it full size, × to discard.
The rules keep it a scrap, not a gallery:

- **Nothing until you paste one.** A sheet without snippets looks exactly as before; the strip, the
  `snippets` folder and `snippets.json` only appear with the first. Text is always what text pastes
  as: anything copied with text in it (cells from Excel, a slide) pastes its text, picture or not.
- **On this PC only.** Images are big and these are temporary, so they never go in tack.json, a
  device file, an import or an export. The sync format and every other device are untouched; there a
  sheet shows its text. (That also keeps screenshots of work out of anyone's cloud folder.)
- **Discarding is instant, with Undo.** × takes the snippet off the sheet at once and shows Undo; the
  file is deleted when that Undo lapses (the toast goes or another takes its place), and if Tack quits
  first, the next start clears it away. Deleting a sheet takes its snippets the same way, and a
  snippet whose sheet was deleted on another device goes at the next start.
- **Small and bounded.** Up to 50 snippets and 50 MB in all, counting discarded ones an Undo could
  still put back (a paste that needs their room ends that Undo). A pasted image is checked from its
  header before anything is decoded (40 million pixels at most), kept as it came when it fits, and
  otherwise scaled to fit 8192 pixels a side (16.7 million in all) and saved as a plain PNG. Only
  Windows' own PNG, BMP, JPEG, GIF and TIFF decoders are used, image files are read only from this
  PC's own drives (never a network share or a cloud placeholder), and only the first thousand names
  of a copied file list are looked at.
- **Keys too.** The strip is one tab stop before the text: ← → between thumbnails, Enter shows one full
  size, Delete discards it (with Undo), Shift+F10 opens its menu. A click leaves the caret in the text.
- **Full size means real pixels.** The viewer is a plain window on the panel's display: a snip that
  fits is shown one pixel to a pixel, so text in it is as sharp as it was; a bigger one is scaled to
  nine tenths of the display. Esc, a click on it, or clicking anywhere else closes it (an unpinned
  panel then folds away too, as it would have).
- **Pasted on Tasks**, an image has nowhere to go but a sheet, so it goes to the sheet on show in
  Notes, which comes forward with a toast saying so (Undo takes it back off).

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

### Look: a background you can see through, and a Tack that fades back
Two optional touches in Settings → Look, both leaving Tack exactly as it was until you use them:

- **Panel background, solid to see-through.** One slider, and only the background moves: text, icons and
  controls never fade, so they stay crisp and readable. With frosted glass it's the tint over the acrylic
  (from a solid tint to the bare glass, whose blur keeps text readable); without glass, what's behind the
  panel shows through unblurred, so it stops at 80%, where text keeps 4.5:1 contrast (secondary text 3:1)
  over a white or a black window, in either theme. Each mode keeps its own value, the default is a tick on
  the slider (close to it *is* the default), and the default writes nothing to tack.json.
- **Fade when idle** (off by default). The orb, and the panel while it's pinned, fade to half once the
  pointer has been away for three seconds, and come back the moment it returns or the panel gets the
  focus (or something is dragged over the orb). Never while the panel is in use, a menu, the undo toast, a
  snippet or the bubble beside the orb is up, or you drag or resize. A faded panel is truly see-through
  (half a solid panel: frosted glass can't be half there), so you can see the model behind it; the fade
  starts from a picture of the panel as the screen showed it, glass and all, fading away over the faded
  panel, so it never flashes the solid surface first. Nothing
  runs while idle: a one-shot timer starts when the pointer leaves, and the fade is one short animation,
  none if Windows' animation effects are off.

### Light and dark
Tack follows Windows' light/dark app mode by default (and switches live when Windows does), or can be set
to either. Light mode is not an inverted dark mode: it uses deeper shades of each accent so white ticks
stay legible, a pale tint over the acrylic, and a porcelain orb with a soft grey shadow that still reads on
white documents.

## Visual language

- **Glass:** the official Windows 11 system backdrop (Acrylic), with a light tint on top (dark or pale,
  per theme) for contrast over busy or bright windows. Turn it off for a solid, high-contrast surface. Windows normally
  drops acrylic to a flat colour when a window is inactive; a pinned panel is inactive most of the time,
  so Tack keeps its frame drawn as active (see `WindowEffects.KeepFrameActive`). The tint (or the solid
  surface) can be made more see-through in Settings → Look. Without glass, DWM fills a frame extended
  under the panel with the theme's solid colour, so a see-through or faded panel is *clear* instead: no
  frame, and blur-behind on an empty region, which since Windows 8 blurs nothing but has DWM honour the
  window's own transparency (`WindowEffects.SetClear`).
- **Shape:** native rounded corners and shadow from DWM, a 1 px system border, 7–10 px radii inside.
- **Type:** Segoe UI Variable (falling back to Segoe UI), 13.5 px body, a semibold display weight for the
  focus task, small-caps style overlines for labels.
- **Motion:** every animation is between 90 and 260 ms and eased. The panel grows out of the corner the
  orb was in; ticks draw themselves; rows glide when reordered (FLIP); the tab underline slides.
  Nothing loops, so nothing animates while you're not interacting. Things with a drop shadow (the peek,
  the undo toast, the bubble beside the orb) slide and fade as a picture of themselves (`Controls/StandIn`):
  drawing in software, WPF would blur the shadow again for every frame. See "Smooth enough, in software" below.
- **Hover-only chrome:** row actions appear in a small floating toolbar on hover, so they never take
  width from your text.

## Architecture

```
src/Tack
├── App.xaml(.cs)          Startup: single instance, data folder, first-run seeding, crash safety
├── AppShell.cs            The coordinator: orb ⇄ panel, placement, shortcut, full-screen, housekeeping
├── IdleFade.cs            Fade when idle (off by default): one-shot timers, the orb and a pinned panel at half
├── Model/                 Workspace (all user state), SpaceView (what the space on show shows), Search, TaskItem, NoteSheet, Settings
├── Services/              StateStore (JSON persistence), FolderSync (sync through a folder), accent colour, startup, log, single instance
├── Sync/                  The data model shared with Tack Focus: clocks, sort keys, merges, device files, the sync engine
├── Interop/               Win32/DWM: glass, monitors & DPI, global hotkey, shell watcher
├── Windows/               OrbWindow, PeekWindow, PanelWindow
├── Launcher/              The optional radial launcher: Core/ (platform-neutral, tested), its window, settings card, launching
├── Drop/                  Dropping onto the orb: Core/ (reading drags, attachments.json, the rules; tested), OLE, the module
├── Snippets/              Images pasted into Notes: reading a paste safely, the store beside tack.json, the full-size viewer
├── Views/                 TasksView, NotesView, SettingsView, SearchView (UserControls inside the panel)
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
Three top-level windows (more only while they're needed: the launcher, the bubble beside the orb after a
drop, a snippet shown full size), each doing one job:

| Window | Kind | Why |
|---|---|---|
| Orb | Layered (per-pixel alpha), topmost, `WS_EX_NOACTIVATE` | Round shape, click-through corners, never steals focus |
| Peek | Layered, topmost, click-through | Informational only; can't get in the way |
| Panel | Normal window with DWM backdrop, topmost | Real acrylic, native corners/shadow, resizable |
| Launcher (optional) | Layered, topmost; created when opened, closed after | Exists only while it's open |
| Drop bubble | Layered, topmost, `WS_EX_NOACTIVATE`; click-through until it offers Undo | Made the first time something is dragged over the orb |
| Snippet viewer | Plain window on the panel's display; made when a snippet is opened, closed after | A pasted image at its own pixels |

### Placement across monitors
All placement is done in **physical pixels** through `SetWindowPos`, never WPF's `Left`/`Top`
(which are DPI-relative and unreliable across mixed-DPI monitors). Sizes are stored in DIPs and converted
with the target monitor's DPI. WPF applies DPI changes to hidden windows late, so `ScreenGeometry.PlaceExact`
checks WPF's scale against `GetDpiForWindow` and replays `WM_DPICHANGED` when they disagree.

The panel unfolds from the orb toward the middle of the screen, so the orb's corner of the panel sits
exactly where the orb was. Move the panel and the orb's home moves to the panel's nearest corner. Remembered
positions are clamped back onto a connected monitor at startup and whenever displays change.

Resizing while open goes through `WindowBoundsAnimator`, which interpolates each edge separately, so the
anchored edges don't move by even a pixel. Resizing a WPF window renders it inside `SetWindowPos`, and that
render raises a frame of its own. The animator never steps from inside it (were those renders instant, each
step would set off the next and the glide would run through in a burst); it notes it, and once the step
returns takes the next one straight away if most of a refresh (0.8 of the display's) has gone by, else on
the next frame. So in software the glide moves at the pace the panel can be drawn, never faster than the
display, and each step costs one render rather than two. A glide's clock starts on its
first frame, after a one-pixel step, so the wait for that frame doesn't eat the start of the curve. If the
size changes again mid-glide, it carries on from where it is and keeps its end time (at least 120 ms more
for a big change late on). A row folding away (a task ticked, deleted or moved to another space) is left out
of the list's height from its first frame, so the panel glides once, in step with the row's own clock and
ease-in, instead of chasing it. Auto-fit measures the Tasks view at the
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

### Smooth enough, in software
`dotnet run --project tests/Tack.Tests -c Release -- --frames` runs a copy of Tack in its own process on
the real displays and times each animation frame by frame (open and fold, tabs, Settings, adding, ticking
and deleting a task, the toast, the orb's hover and peek): how often a frame draws something new, how far
apart, how many of the display's refreshes show no new frame, and the CPU it takes. What it found, on a
100 Hz display at 150%:

- WPF's software renderer isn't tied to the display: small animations run at 110–140 frames a second.
  Animations that redraw the whole panel (opening it, a tab, Settings) are bound by the pixels, about
  13 ms a frame for a 510 × 930 pixel panel whatever is in it, so 65–85 frames a second.
- A drop shadow is blurred again for every frame in software: half the cost of the peek and the toast.
  They (and the bubble beside the orb) now move as a picture: the peek from 75 to 145 new frames a second,
  both at about half the CPU.
- Not worth it: the GPU renderer (`TACK_GPU=1`) doubles private memory (49 → 108 MB folded) and opens the
  panel no faster; `Timeline.DesiredFrameRate` set to the display's rate makes every animation *less* smooth
  (WPF then waits on a coarse timer between frames: small ones drop from 110–140 to about 90); waiting for
  DWM each frame (`DwmFlush`) does too; `BitmapCache` and pictures of the whole panel cost as much as drawing it.
- The panel's size glide (`WindowBoundsAnimator`) has to resize the window each step, since DWM draws the
  acrylic, shadow and corners for the window's own rectangle, and each resize draws the panel. Taking the
  next step as soon as that drawing is done (not waiting for a frame of its own, which drew the panel a
  second time) gave, on a 240 Hz display at 150%, 130–170 new heights a second for a tab switch and
  150–190 for an added row (22–26 heights a glide), against 100–130 and 120–155 (14–20 heights).

`-- --screens` takes pictures of the panel's looks as DWM shows them (over a striped window of its own),
`-- --frames --exp …` tries the rejected options above.

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
- **Snippets** stay on the PC they were pasted on, and the viewer has no zoom: a snip is shown at its own
  pixels, or fitted to the display.
- Deliberately **not** planned: reminders, due dates, priorities, tags, collaboration. Spaces (above) are as
  far as grouping goes: a name to keep work, development and personal apart, not projects with settings.
- **Sync** with Tack Focus is opt-in and serverless: through a folder in your own OneDrive that the
  OneDrive app keeps in step, never a Tack server, and no network code in Tack. See [SYNC.md](SYNC.md).
  Google Drive and end-to-end encryption are for later.
- **Limits far beyond real use**, the same as Tack Focus's: 100,000 characters in a task, 1,000,000 in a
  note, 10,000 lines in one paste or Ctrl+Enter, and 180,000 tasks, sheets and spaces (deleted ones
  included) taken in from imports or other devices, so no device holds anything its peers would refuse.
  See [SYNC.md](SYNC.md#damaged-and-hostile-files).
