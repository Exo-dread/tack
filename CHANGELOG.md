# Changelog

## Unreleased

Get things into Tack without leaving the app you're in, and find them again: drop onto the orb,
snippets in Notes, search, and recent files in the launcher. Everything new is either off until you
turn it on or out of sight until you use it; until then Tack looks and behaves as 1.6.1 did.

- **Drop onto the orb.** Drag an email from Outlook, files or folders from Explorer, a link from your
  browser or a few lines of text onto the orb: it lights up and says what it will add, and letting go
  makes a task of each (up to 20), in the space on show, with Undo right beside the orb. An email
  becomes a task named by its subject, a file by its name, a page by its title. The app you dragged
  from keeps the focus, and nothing is ever moved out of it.
- **A paperclip opens what you dropped.** Tasks made by a drop show a small paperclip (and take
  Ctrl+O, or Open in their menu) that opens the file, folder or page, or the copy of the email Tack
  kept. What a task points to stays on this PC, in `attachments.json` and an `attachments` folder
  beside tack.json: never in tack.json, never synced, imported or exported. Deleting the task lets it
  go. Programs are never kept, and Tack keeps at most 500 MB of copies.
- **Snippets in Notes.** Snip some instructions (Win+Shift+S) and press Ctrl+V in a sheet: the picture
  sits above the sheet's text as a thumbnail while you follow it. Click it to see it full size on that
  display (Esc or a click closes it), and × throws it away, with Undo. Image files copied in Explorer
  paste the same way, and an image pasted on Tasks goes to the sheet in Notes. Snippets stay on this
  PC (in a `snippets` folder beside tack.json), never synced, imported or exported: other devices show
  the sheet's text. Up to 50 snippets (50 MB); very large images are scaled down, and deleting a sheet
  deletes its snippets. Anything copied with text in it still pastes as text, and until you paste an
  image nothing about Notes changes.
- **Search.** Press **Ctrl+F** in the panel and type: open tasks, finished tasks and notes from every
  space are listed as you type, grouped Tasks, Done and Notes, each with its space while you have
  spaces. Case and accents don't matter (so "cafe" finds "Café", and curly quotes or a non-breaking
  space pasted from Outlook still match), and a note matches anywhere in it, not just its title.
  ↑ ↓ and Enter (or a click) go there: its space and tab, the task scrolled to and lit up for a
  moment, or the sheet with the match selected. Esc closes it and puts the caret back. Until you press
  Ctrl+F nothing on screen changes. Tack Focus finds exactly the same things.
- **Recent files in the launcher** (optional, off by default: Settings → Launcher → Recent files). Rest
  on an app in the launcher and the files it opened last fan out beside it; drag onto one and let go
  (or click it, or press R and Enter) and it opens with that app, on the display the shortcut uses.
  Shift+drag to Revit 2024, rest, drop on the model: it opens in Revit 2024, even where .rvt files
  open in another version. The files come from the app's Jump List, then Revit's, AutoCAD's and
  Navisworks' own recent lists, up to 8 that are still there, read each time the launcher opens and
  never stored. Dragging on past an app still chooses a display.
- **Panel background.** Settings → Look (formerly Appearance) has a slider from solid to see-through
  for the panel's background. Only the background changes: text, icons and controls stay as they are.
  With frosted glass it sets how much of the glass shows through the tint; without, how much of
  what's behind the panel shows through, never so much that text gets hard to read. The tick marks the
  look Tack has always had, and Default goes back to it.
- **Fade when idle** (Settings → Look, off by default). The orb, and the panel while it's pinned, fade
  to half after a few seconds without the pointer, so you can see more of what's behind them, and come
  back the moment you point at them (or drag something over the orb). Never while you're typing in
  the panel, or a menu, the undo toast, a snippet or the bubble beside the orb is up. With Windows'
  animation effects off, they fade without the animation.
- **Settings → Shortcut** lists the panel's new keys: Ctrl+F to search, Ctrl+O to open what was
  dropped on a task.
- **Smoother panel resizing.** When the panel changes size (switching tabs, opening Settings, adding a
  task), it now glides all the way instead of jumping most of the way and then settling. When you tick,
  delete or move a task, the panel shrinks with the row as it folds away and stops when the row is gone,
  with no strip of empty glass trailing behind.
- **Smoother peek and toast.** The peek you get when you hover the orb now slides in at the display's
  own pace (on a 100 Hz display, from about 75 new frames a second to well over 100), and both it and
  the undo toast take about half the work, so they stay smooth when the PC is busy: while they move, a
  picture of them stands in, so their shadow isn't drawn again for every frame.
- Notes: a sheet switch that comes in the middle of an edit (from a keystroke, say) waits for the
  edit to end before clearing Undo, instead of being able to crash Tack. As before, the switch isn't
  an undo step, and what's typed on blank paper stays undoable.

## 1.6.1 (2026-10-24)

Sync stands up better to damaged and hostile files. From a second security review of Tack and
Tack Focus.

- **Sturdier against bad files in the sync folder.** Ways a damaged or hostile file could stop sync
  between your other devices, even after it was gone, are closed. Each PC now takes in no more
  tasks, notes and spaces from the folder than its own sync file can carry (far beyond real use): a
  file that would go past that is set aside, Settings names the device whose changes aren't
  arriving, and the rest keeps syncing; everyday changes from your other devices keep coming in.
  Tack never reads more than 20 MB of a file. What Tack writes stays readable by earlier versions of
  Tack and Tack Focus, and a device still on an earlier version keeps syncing with this one. See
  docs/SYNC.md.
- **Turn on sync takes only OneDrive\Apps\Tack Focus by itself.** A Tack sync folder anywhere else
  (under a translated "Apps" folder, say) is pointed out instead, and Choose folder… picks it if
  it's yours.
- **Imports, pasting and typing keep to what sync can carry.** Importing a file with more tasks and
  notes than Tack can keep in sync says so instead of importing it, and a task over 100,000
  characters or a note over 1,000,000 is skipped, as sync skips it; the boxes stop there too.
  Pasting a list, or Make task from a note, takes up to 10,000 lines at a time.
- An imported task that was finished stays finished on every device, even when its file gave the
  date without a time.
- If this PC holds more than sync can carry, Settings keeps saying so until it fits again, instead
  of going back to "In step with the sync folder".
- **Take it in anyway.** When Settings says a device's changes aren't arriving because its file
  holds too much at once, a button next to it takes that file in anyway, still within what sync can
  carry.
- A list of more than 1,000 spaces shows 1,000 at a time, with More spaces, so the strip, menus and
  Settings stay quick.
- A PC that rebuilds its sync state from its own file no longer keeps an older copy of a note twice.
- Downloads are built only from versions already merged into main.

## 1.6.0 (2026-10-23)

An optional radial launcher around the orb, for the apps and folders you open all day. It's off
until you turn it on; off, Tack is exactly as before.

- **Radial launcher (optional, off by default).** Turn it on in Settings → Launcher. Shift+click the orb
  for six shortcut positions around it (apps, folders, websites, or groups of up to eight), or Shift+drag
  to one and let go. Keep dragging past an app or folder to pick the display it opens on, from a small map
  of your real display arrangement (best effort: some apps put themselves back, and apps running as
  administrator can't be moved). Shortcuts can also always ask, or remember a display. Works at screen
  edges and corners, on any monitor and scaling, with the keyboard and Narrator, and with reduced motion.
  Kept in its own launcher.json; tack.json and sync are unchanged. Off, Tack is exactly as before. See
  docs/LAUNCHER.md.

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
