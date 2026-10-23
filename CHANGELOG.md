# Changelog

## Unreleased

- **Radial launcher (optional, off by default).** Turn it on in Settings → Launcher. Shift+click the orb
  for six shortcut positions around it (apps, folders, websites, or groups of up to eight), or Shift+drag
  to one and let go. Keep dragging past an app or folder to pick the display it opens on, from a small map
  of your real display arrangement (best effort: some apps put themselves back, and apps running as
  administrator can't be moved). Shortcuts can also always ask, or remember a display. Works at screen
  edges and corners, on any monitor and scaling, with the keyboard, and with reduced motion. Kept in its
  own launcher.json; tack.json and sync are unchanged. Off, Tack is exactly as before. See docs/LAUNCHER.md.

## 1.5.0 (2026-10-22)

Spaces arrive on screen: keep work, development and personal tasks apart, or ignore them and
nothing changes.

- **Spaces** (Work, Dev, Personal). Make one in **Settings → Spaces → + New space**, where Main can
  be renamed too. Until there's a second space, Tack looks and behaves exactly as before. Then a slim
  strip under the header lists **All · Main · Work · …** and **+** for a new one; click a space, or
  press **Alt+1** to **Alt+9** (the spaces in order) and **Alt+0** (All), even while typing a task.
  With more spaces than fit, the strip scrolls (the mouse wheel too) and fades at the edge, and **+**
  stays in sight.
- A space shows its own tasks and sheets in the one order you set, and new ones go into it ("Add to
  Work"; from All, into Main). Reordering within a space never moves tasks in the others.
- **Move to ▸** on a task's menu (after Move to top; on the Now card after Stop focusing), **Ctrl+M** on
  a task, and on a sheet's menu: "Moved to Personal · Undo".
- Still one task in focus: in other spaces it shows as "Now in Work: …", which takes you there.
  **All** shows everything, each task with its space.
- Done and Clear, the orb's count and ring, the Tasks badge and the peek ("Work · 3 open · 2 done
  today") go by the space on show. Each PC remembers which space it shows; it isn't synced.
- Notes: a space with no sheets shows blank paper, and its sheet is made when you start writing.
  Ctrl+Enter makes the task in the sheet's own space.
- **Settings → Spaces** renames spaces in place, moves them up and down, and deletes them: "Deleted Dev
  · Undo", and meanwhile their tasks and notes show in Main. Main can't be deleted.
- Fixed, with or without spaces: a toast too long for the panel (say "Added to tasks: …" from a long
  line) ends in an ellipsis, and Undo stays in the panel where you can click it. It used to be pushed
  past the edge.
- Nothing about files, merging or sync changes: spaces sync as they did in 1.4.0, and the new ways of
  adding and reordering stamp exactly as Tack Focus does (shared test vectors).

## 1.4.0 (2026-10-21)

The groundwork for spaces, so every device understands them before the screens for them arrive.
Nothing looks different yet.

- **Groundwork for spaces** (Work, Dev, Personal), with nothing visible yet. Tasks and notes can
  carry the space they're in, and spaces themselves sync like tasks do, so once the screens for them
  arrive, every device already understands them. Files are unchanged until a space exists, and older
  versions keep syncing (they simply show everything together). After updating, Tack reads every
  other device's file once more and writes its own once, so nothing older versions left out is missed.

## 1.3.1 (2026-10-21)

Sync is sturdier: a damaged or crafted file in the sync folder can't stop it any more. From a
security review of Tack and Tack Focus.

- **Sync shrugs off bad files.** A damaged or crafted file in the sync folder can no longer stop
  sync: unreadable text (half an emoji, invalid UTF-8) sets that one file aside instead of stopping
  every cycle, and the rest keeps syncing. Files are checked against generous limits (size before
  reading, number of records, text length, real dates, sane clocks), and one file's word that it
  has "seen" another device can no longer hide that device's changes. Importing such a file says
  so instead of failing silently. Found in a security review; see docs/SYNC.md.
- An empty "completed" date now means not done, as in Tack Focus.
- A file that can't be read right now (the OneDrive app holds it) no longer holds up the others.
- If this PC's sync file would grow past 20 MB, Settings says so instead of writing a file other
  devices would refuse.

## 1.3.0 (2026-10-20)

Tack and Tack Focus (tackfocus.app) now keep each other up to date through your own OneDrive, with
no account and no Tack server.

- **Sync with Tack Focus through your OneDrive.** Settings → Sync → Turn on sync finds Tack Focus's
  folder in OneDrive on your PC (or choose any folder). Tack writes its own file there and reads the
  others'; the OneDrive app carries them. Tack itself still never connects to anything, and nothing
  runs while idle: it reacts to the folder changing, your edits, opening the panel and waking up.
- While syncing, deletions are kept (as an id and a time) instead of being forgotten after 90 days,
  so a device that was offline for months can't bring them back.
- A new PC joining sync leaves its untouched welcome tasks and blank sheet behind, and your focus
  from another device wins over the welcome one.
- For contributors: `TACK_INSTANCE` lets a development copy run beside your installed Tack.

## 1.2.0 (2026-10-18)

Tack now has a companion for your phone: **Tack Focus** (tackfocus.app), the same tasks and notes as an
installable web app that works offline. This release lets the two share your data.

- **Bring your phone's tasks to your PC: Settings → Import.** In Tack Focus, Settings → Export saves a
  tack.json; import it here (or one copied from another PC, or a backup). Anything new is added, where
  both sides changed the newer edit wins, and a note written in both places is kept twice rather than
  overwritten. Undo puts everything back. Tack Focus can import a Tack for Windows tack.json the same
  way.
- **Ready to sync with Tack Focus.** tack.json gains a `tackFocus` section: a clock for every change,
  sort keys for the list, and deletions remembered for 90 days so they can reach your other devices
  (then forgotten for good). The top level of the file is unchanged, so Tack 1.1 still opens it, and a
  file saved by 1.1 or edited by hand is taken as it is. Merging is a port of Tack Focus's engine,
  checked against the same test vectors. Syncing itself, through your own OneDrive or Google Drive,
  comes later; Tack still makes no network requests.
- Notes keep Windows line breaks in the editor but are stored the way Tack Focus stores them, so a
  note opened on both never looks changed when it isn't.
- After an import or merge, a task can always be moved between any two others (even two that ended up
  in the same spot).

## 1.1.0 (2026-10-17)

- **Light mode.** Settings → Theme: System (follows Windows' light/dark mode, live), Light or Dark.
  Light mode has its own palette, deeper accent shades and a porcelain orb.
- **Tasks fits its list.** The panel grows and shrinks with your tasks, from a sensible minimum up to
  620 DIP (or three quarters of the screen), then scrolls.
- **Each tab remembers its own size.** Notes keeps a writing-sized page; Tasks keeps auto-fitting.
  Dragging an edge sets that tab's size and turns automatic sizing off for it; double-click the header
  to turn it back on. Sizes survive restarts.
- **No jumping.** Size changes glide, with the corner nearest the orb held still, and the panel is kept
  on screen. Settings opens at a comfortable height even if Tasks is short.
- Text and ticks on the accent colour now pick white or black by contrast.
- Upgrading keeps your 1.0 panel size for Notes.

## 1.0.0 (2026-10-14)

First release.

- The orb: an always-on-top glass bead with a progress ring and open-task count. Drag it anywhere; it
  snaps to screen edges and never takes focus. Hover to peek at your focus task; right-click for a menu.
- The panel: Windows 11 acrylic, Tasks and Notes tabs, pin to keep it open over other apps.
- Tasks: quick add (paste a list to add many), edit in place, one task in focus ("Now"), tick to finish
  with a short grace period, drag or Alt+↑/↓ to reorder, Done section, undo for deletes.
- Notes: plain-text sheets titled by their first line, list continuation, F5 timestamps, Ctrl+Enter to
  turn a line into a task.
- Global shortcut (Ctrl+Alt+Space, with automatic fallbacks if it's taken), Start with Windows,
  step aside for full-screen apps, accent colours, solid mode.
- Per-monitor DPI and multi-monitor placement; positions recover when a monitor is unplugged.
- Crash-safe JSON storage with backup, and portable mode (a `TackData` folder next to the exe).
