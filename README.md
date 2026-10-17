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
  No projects, tags, priorities or due dates.
- **Notes like scrap paper.** Plain-text sheets that save as you type. Lists continue themselves,
  F5 stamps the time, and Ctrl+Enter turns a line into a task.
- **Windows 11 glass, light or dark.** Native acrylic, rounded corners, your accent colour, smooth and
  short animations. Follows Windows' light/dark mode, or pick one. Crisp on every monitor, at any scaling.
- **Sized to fit.** The Tasks panel grows and shrinks with your list (then scrolls), Notes keeps its own
  size, and any size you drag to is remembered per tab. Switching tabs never makes the panel jump.
- **Polite.** Steps aside while a full-screen video, game or slideshow is in front.
- **Yours.** No account, no cloud, no telemetry, no network access at all. Your data is one readable JSON file.

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

### Notes

- Sheets are plain text; a sheet's title is its first line. **+** (or **Ctrl+N**) adds a sheet;
  right-click a sheet's name to delete it.
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
| Backspace | An empty task | Delete it |
| Ctrl+Enter | Notes | Send the current line (or selection) to Tasks |
| F5 | Notes | Insert the date and time |
| Ctrl+Tab | Notes | Next sheet |

## Your data

Everything lives in `%LOCALAPPDATA%\Tack\tack.json`: plain, indented JSON you can read, back up or
sync with whatever you like. Saves are debounced, written atomically (temp file, then replace), and the
previous version is kept as `tack.json.bak`, so a crash or power cut can't corrupt your list. If the file
is ever unreadable, Tack recovers from the backup and keeps the damaged file aside.

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
iPhone, Android and any browser (to be hosted at tackfocus.app). It works offline, keeps everything on
the device, and reads and writes the same `tack.json` format. Syncing between Tack Focus and Tack
for Windows through your own OneDrive or Google Drive is designed in [docs/SYNC.md](docs/SYNC.md).

## License

[MIT](LICENSE). Free forever: no ads, no accounts, no telemetry, no paid tier.
