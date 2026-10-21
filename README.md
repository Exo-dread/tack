<p align="center">
  <img src="docs/icon.png" width="96" alt="Tack icon" />
</p>

<h1 align="center">Tack</h1>

<p align="center">
  A tiny, always-on-top home for what you're doing <em>right now</em>.<br/>
  Tasks and quick notes in a frosted-glass panel that folds into a 40-pixel orb.
</p>

<p align="center">
  <img src="docs/screenshots/tasks.png" width="300" alt="Tack's task panel in the dark theme" />
  &nbsp;
  <img src="docs/screenshots/tasks-light.png" width="300" alt="Tack's task panel in the light theme" />
</p>
<p align="center">
  <img src="docs/screenshots/notes.png" width="300" alt="Tack's notes" />
</p>
<p align="center">
  <img src="docs/screenshots/peek.png" width="300" alt="Hovering the orb shows the current focus" />
  &nbsp;
  <img src="docs/screenshots/peek-light.png" width="300" alt="The orb and its peek in the light theme" /><br/>
  <sub>Folded away, Tack is a small orb. Hover it to see what you're focusing on without opening anything.</sub>
</p>

---

When you work across many windows and monitors, it's easy to lose the thread. Most task apps make
you switch to them, navigate, and switch back. Tack stays on top of everything, takes almost no space,
and gets out of the way the moment you're done.

- **One orb, always on top.** Tack folds into a small glass orb you can drag anywhere. It snaps neatly
  to screen edges, never takes focus from the app you're in, and shows today's progress as a ring.
- **Peek without opening.** Hover the orb to see your current focus ("Now") and what's left.
- **Open in a click or a keystroke.** Click the orb or press **Ctrl+Alt+Space** from any app. Type a task,
  press Enter, press Esc, and you're back at work.
- **One task in focus.** Mark one task as *Now* and it gets pride of place at the top (and in the peek).
  No tags, priorities or due dates.
- **Spaces, if you want them.** Keep work, development and personal tasks apart: make a space in
  Settings and a slim strip under the header switches between them (Alt+1 to Alt+9, Alt+0 for all).
  Until you make one, there's nothing extra on screen.
- **Notes like scrap paper.** Plain-text sheets that save as you type. Lists continue themselves,
  F5 stamps the time, and Ctrl+Enter turns a line into a task.
- **Windows 11 glass, light or dark.** Native acrylic, rounded corners, your accent colour, smooth and
  short animations. Follows Windows' light/dark mode, or pick one. Crisp on every monitor, at any scaling.
- **Sized to fit.** The Tasks panel grows and shrinks with your list (then scrolls), Notes keeps its own
  size, and any size you drag to is remembered per tab. Switching tabs never makes the panel jump.
- **Polite.** Steps aside while a full-screen video, game or slideshow is in front.
- **Yours.** No account, no telemetry, and Tack never connects to anything. Your data is one readable JSON
  file. Optional sync with Tack Focus on your phone goes through your own OneDrive folder.

## Download

Grab the latest `Tack-x.y.z-win-x64.zip` from [**Releases**](../../releases/latest), unzip it and run
`Tack.exe`. Nothing to install, and no .NET runtime needed.

- Windows 10 or 11, x64 (developed and tested on Windows 11). Frosted glass needs Windows 11 22H2 or
  later; elsewhere the panel is solid.
- The exe isn't code-signed yet, so Windows SmartScreen may warn on first launch:
  choose **More info → Run anyway**.
- Already have the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)?
  The `-net8.zip` download is under 1 MB.

To start Tack with Windows, turn on **Settings → Start with Windows**.

## Using Tack

| | |
|---|---|
| **Open / fold** | Click the orb, or press **Ctrl+Alt+Space** anywhere. **Esc** folds it back. |
| **Move** | Drag the orb, or drag the panel by its header. The orb follows the panel's nearest corner. |
| **Resize** | Drag the panel's edges. Each tab remembers its own size; until you resize Tasks, it fits its list. Double-click the header to go back to automatic sizing. |
| **Theme** | **Settings → Theme**: System (follows Windows), Light or Dark. |
| **Sync** | **Settings → Sync → Turn on sync** keeps Tack in step with [Tack Focus](web/README.md) through your OneDrive (see below). |
| **Import** | **Settings → Import** merges a tack.json from [Tack Focus](web/README.md) (its Settings → Export), another PC or a backup. Undo puts it back. |
| **Pin** | 📌 keeps the panel open over other apps. Unpinned, it folds away when you click elsewhere. |
| **Orb menu** | Right-click the orb for Open, Settings and Quit. |

If another app already uses Ctrl+Alt+Space, Tack picks a free fallback on first run (Win+Ctrl+T, then
Win+Alt+N) and the welcome tasks tell you which. You can change it in **Settings → Shortcut**.

### Tasks

- Type in **Add a task** and press **Enter**. **Ctrl+Enter** adds it *and* makes it your focus.
- Paste a list (bullets and numbering are stripped) to add many tasks at once.
- Click any task to edit it in place. Hover for **◎ focus** and **× delete**; right-click for more.
- Tick the circle to finish a task. You have a moment to change your mind before it moves to **Done**.
- Drag the dots at a task's left edge to reorder.
- Deleting is undoable: every delete shows an **Undo** button.

### Spaces

Everything starts in **Main**. To keep work, development and personal tasks apart, add a space in
**Settings → Spaces → + New space** (rename Main there too, say to "Work"). Spaces are optional:
until there's a second one, Tack looks exactly as it always has.

- Once there's a second space, a slim strip under the header lists **All · Main · Work · …**. Click one
  (or press **Alt+1** to **Alt+9** for the spaces in order, **Alt+0** for All) to show it. The keys work
  even while you're typing a task, and the draft stays. **+** at the end of the strip makes a space.
- A space shows its own tasks and sheets, in the one order you set. New tasks and sheets go into the
  space on show ("Add to Work"); from All, into Main. **All** shows everything, each task with its space.
- Right-click a task (or a sheet's name) → **Move to ▸** to put it in another space, or press **Ctrl+M**
  on a task. Undo moves it back.
- There's still one task in focus. In other spaces it shows as a slim line, "Now in Work: …"; click it
  to go there.
- Done, Clear, the orb's count and ring, and the peek ("Work · 3 open · 2 done today") are per space.
- **Settings → Spaces** renames, reorders and deletes spaces. Deleting one puts its tasks and notes
  back in Main (Undo brings the space back). Main can be renamed, never deleted.
- Each device remembers which space it shows; spaces themselves sync with Tack Focus.

### Notes

- Sheets are plain text; a sheet's title is its first line. **+** (or **Ctrl+N**) adds a sheet;
  right-click a sheet's name to delete it (or, with spaces, move it to another).
- Start a line with `- `, `* `, `1. ` or `[ ] ` and Enter continues the list.

### Keyboard

| Keys | Where | Does |
|---|---|---|
| Ctrl+Alt+Space | Anywhere | Open Tack / fold it away |
| Esc | Panel | Cancel the current edit, then fold away |
| Ctrl+1 / Ctrl+2 | Panel | Tasks / Notes |
| Ctrl+, | Panel | Settings |
| Ctrl+Z | Panel | Undo the last delete |
| Enter / Ctrl+Enter | Add box | Add task / add and focus on it |
| ↑ / ↓ | Tasks | Move between tasks |
| Ctrl+Enter | A task | Make it the focus |
| Ctrl+D | A task | Mark done |
| Alt+↑ / Alt+↓ | A task | Move it up or down |
| Ctrl+M | A task | Move it to another space (once there are spaces) |
| Backspace | An empty task | Delete it |
| Ctrl+Enter | Notes | Send the current line (or selection) to Tasks |
| F5 | Notes | Insert the date and time |
| Ctrl+Tab | Notes | Next sheet |
| Alt+1 … Alt+9 / Alt+0 | Panel | Show the 1st … 9th space / All (once there are spaces) |

## Your data

Everything lives in `%LOCALAPPDATA%\Tack\tack.json`: plain, indented JSON you can read, back up or
sync with whatever you like. Saves are debounced, written atomically (temp file, then replace), and the
previous version is kept as `tack.json.bak`, so a crash or power cut can't corrupt your list. If the file
is ever unreadable, Tack recovers from the backup and keeps the damaged file aside.

The file also has a `tackFocus` section: a clock for every change, sort keys and recently deleted
items, so Tack can merge edits made elsewhere. Older versions of Tack ignore it. **Settings → Import**
brings in a tack.json exported from Tack Focus (or copied from another PC, or a backup): anything new
is added, where both changed the newer edit wins, a note written in both places is kept twice, and
Undo puts everything back.

**Sync (optional).** Turn on sync in Tack Focus first (it makes an "Apps › Tack Focus" folder in your
OneDrive); once the OneDrive app has brought that folder to your PC, **Settings → Sync → Turn on sync**
here finds it by itself. From then on the two keep each other up to date: Tack writes its own file in
that folder and reads the other devices' files, and the OneDrive app carries them. Tack itself still
never goes online, works fully offline, and keeps everything here if you turn sync off. Your tasks and
notes are stored in OneDrive as ordinary files, not end-to-end encrypted yet. How it works, and what's
verified: [docs/SYNC.md](docs/SYNC.md).

**Portable mode:** create a folder named `TackData` next to `Tack.exe` and Tack keeps everything there
instead, which is handy on a USB stick. (Like any single-file .NET app, the portable exe unpacks about 8 MB
of native WPF libraries into `%TEMP%\.net\Tack` on first launch; it's safe to delete.)

## Lightweight by design

Measured on Windows 11 with the portable build:

| | |
|---|---|
| Startup to orb on screen | ~0.4 s normally; a few seconds on the very first launch (one-time unpacking) |
| CPU while idle | 0%: no timers or polling. Everything is event-driven |
| Memory when folded and idle | ~2–5 MB working set (Tack hands unused memory back to Windows); ~50 MB committed |
| Download | ~53 MB zip (one self-contained exe) |

## Build from source

Requires Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet run --project src/Tack                     # run in debug
./build.ps1 -Version 1.0.0                        # release zips in ./artifacts
```

Pushing a tag like `v1.0.0` runs [`release.yml`](.github/workflows/release.yml), which builds the downloads
and publishes a GitHub Release. See [CONTRIBUTING.md](CONTRIBUTING.md) for the project layout, and
[docs/DESIGN.md](docs/DESIGN.md) for the thinking behind the design.

## Tack Focus: Tack on your phone

[`web/`](web/README.md) holds **Tack Focus**, Tack's tasks and notes as an installable web app for
iPhone, Android and any browser, at [tackfocus.app](https://tackfocus.app). It works offline, keeps
everything on the device, and reads and writes the same `tack.json` format. Syncing with Tack for
Windows through your own OneDrive is built and in review ([docs/SYNC.md](docs/SYNC.md)).

## License

[MIT](LICENSE). Free forever: no ads, no accounts, no telemetry, no paid tier.
