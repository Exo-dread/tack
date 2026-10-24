# Tack Focus changelog

## Unreleased

- **Search.** Tap the search button beside Settings (on a phone), or press **Ctrl+F** (⌘F), and type:
  open tasks, finished tasks and notes from every space are listed as you type, grouped Tasks, Done
  and Notes, each with its space while you have spaces. Case and accents don't matter, and a note
  matches anywhere in it, not just its title. ↑ ↓ and Enter (or a tap) go there: its space and page,
  the task scrolled to and lit up for a moment, or the sheet with the match selected. Esc closes it
  and puts the focus back. On a narrow phone the header's buttons sit a little closer, so the space's
  name keeps its room. Tack for Windows finds exactly the same things.
- **Share to Tack (Android).** An installed Tack Focus now appears in Android's share sheet. Share a
  page, a link or some text from any app and it becomes a task in the space on show, with *Undo*:
  a page is its title with its link, and text of several lines is a task per line, as when you
  paste a list (up to 10,000 lines). Whatever was shared stays on the device and is only ever kept
  as task text. Android may take a day to add Tack Focus to the share sheet after the update;
  reinstalling adds it at once. iPhone doesn't let web apps receive shares.

## 0.4.3 (24 October 2026)

Sync stands up better to damaged and hostile files, and is gentler on Microsoft. From a second
security review of Tack and Tack Focus.

- **Sturdier against bad files in the sync folder.** Ways a damaged or hostile file could stop sync
  between your other devices, even after it was gone, are closed. Each device now takes in no more
  tasks, notes and spaces from the folder than its own sync file can carry (far beyond real use): a
  file that would go past that is set aside, Settings names the device whose changes aren't
  arriving, and the rest keeps syncing; everyday changes from your other devices keep coming in.
  Downloads stop at 20 MB, whatever OneDrive's listing said, and a folder full of files Tack didn't
  put there pauses sync with a message instead of costing hundreds of requests on every sync. What
  Tack Focus writes stays readable by earlier versions of Tack Focus and Tack, and a device still on
  an earlier version keeps syncing with this one. Found in a second security review; see
  docs/SYNC.md.
- **Gentler on Microsoft.** When Microsoft asks Tack Focus to slow down, it waits as long as asked
  (up to six hours). While it waits, or after an error from Microsoft, typing and switching apps no
  longer bring the next sync forward; *Sync now* skips Tack's own wait, but not the one Microsoft
  asked for. A busy sign-in service or download is waited out, not taken for a spent sign-in or an
  expired link.
- **Staying signed in where the background renewal keeps timing out.** It's tried less often (after
  15 minutes, then 30), and after three in a row Tack Focus renews with its quick visit to Microsoft
  as it opens instead, so the sign-in no longer quietly runs out after a day. A few slow tries don't
  switch the background renewal off: it's tried again after that visit.
- **Imports, pasting and typing keep to what sync can carry.** Importing a file with more tasks and
  notes than Tack can keep in sync says so instead of importing it, and a task over 100,000
  characters or a note over 1,000,000 is skipped, as sync skips it; the boxes stop there too.
  Pasting a list, or *Make task* from a note, takes up to 10,000 lines at a time.
- An imported task that was finished stays finished on every device, even when its file gave the
  date without a time.
- If this device holds more than sync can carry, Settings keeps saying so until it fits again,
  instead of going back to "Up to date".
- **Take it in anyway.** When Settings says a device's changes aren't arriving because its file
  holds too much at once, a button next to it takes that file in anyway, still within what sync can
  carry.
- A list of more than 1,000 spaces shows 1,000 at a time, with *More spaces*, so the switcher, Move
  and Settings stay quick.
- A device that rebuilds its sync state from its own file no longer keeps an older copy of a note
  twice.

## 0.4.2 (22 October 2026)

- **No more blank screen while the OneDrive sign-in renews.** Once a day, where the browser can't
  renew it in the background, Tack Focus pops over to Microsoft as it opens, and that used to leave
  an empty screen for as long as Microsoft took. Now it says "Renewing your OneDrive sign-in…",
  offers *Open Tack now* if it's slow, opens by itself after 25 seconds, and stays put when
  Microsoft can't be reached (Wi-Fi without internet, say) instead of landing on the browser's
  error page; a renewal cut short is tried again an hour later.

## 0.4.1 (22 October 2026)

- **Updates arrive by themselves.** A new version used to apply only as you left the app, and a
  phone can put the app to sleep before that happens, so you could come back to the old version
  until a reload. Now it also applies when you open Tack Focus or come back to it, before you've
  touched anything, or straight away while you're elsewhere. Never while you're editing a task or
  have a sheet open, and a half-typed task comes back as it was.

## 0.4.0 (22 October 2026)

- **Spaces** (Work, Dev, Personal), now on screen. Make one in Settings → Spaces → *New space*,
  where Main can be renamed too; until there's a second space, Tack Focus looks and behaves
  exactly as before. Then the title shows the space on show ("Work ▾"): tap it for All, each space
  with its open tasks, and *New space*. With a keyboard, **Alt+1** to **Alt+9** show the spaces in
  order and **Alt+0** shows All, even while you type a task (the draft stays; on a Mac, outside text
  fields, since Option+digit types characters there).
- A space shows its own tasks and sheets in the one order you set, and new ones go into it ("Add to
  Work"; from All, into Main). Reordering within a space never moves tasks in the others.
- Tap a task → **Move** to put it in another space, or Notes ⋯ → **Move sheet to…**: "Moved to
  Personal · Undo". On a phone, ⋯ holds *Delete sheet* too, so the header keeps room for the
  space's name.
- Still one task in focus: in other spaces it shows as "Now in Work: …", which takes you there.
  **All** shows everything, each task with its space.
- Done and Clear, the count under the title, the orb and the app badge go by the space on show,
  which each device remembers for itself (it isn't synced).
- Notes: a space with no sheets shows blank paper, and its sheet is made when you start writing.
  *Make task* puts the task in the sheet's own space.
- **Settings → Spaces** renames spaces in place, moves them up and down, and deletes them: "Deleted
  Dev · Undo", and meanwhile their tasks and notes show in Main. Main can't be deleted.
- Fixed, with or without spaces: on a phone, a toast too long for the screen (say "Added to Tasks:
  …" from a long line) ends in an ellipsis, and Undo stays on screen. It used to run past the right
  edge.
- Nothing about files, merging or sync changes; the new ways of adding and reordering stamp exactly
  as Tack for Windows does (shared test vectors).

## 0.3.0 (21 October 2026)

- **Groundwork for spaces** (Work, Dev, Personal), with nothing visible yet: tasks and notes can
  carry the space they're in, and spaces sync like everything else, so every device understands them
  before the screens arrive. Files and storage are unchanged until a space exists, and older versions
  keep syncing. After updating, Tack reads every other device's file once more and writes its own once.

## 0.2.1 (21 October 2026)

- **Sync shrugs off bad files.** Text that only half spells a character is cleaned up when read
  and never written out, so it can't reach Tack for Windows. A device id such as "constructor" no
  longer stops this device's changes from being shared. Files too big to be Tack's are never
  downloaded, one file that fails to download no longer holds up the others, and files are checked
  against generous limits (records, text length, real dates, sane clocks). One file's word that it
  has "seen" another device can no longer hide that device's changes. Found in a security review;
  see docs/SYNC.md.
- An empty "completed" date means not done, as in Tack for Windows.
- If this device's sync file would grow past 20 MB, Settings says so instead of writing a file
  other devices would refuse.

## 0.2.0 (20 October 2026)

- **Sync with Tack for Windows through your own OneDrive.** Settings → Sync → Turn on sync, sign in
  with Microsoft once; from then on it's automatic. Tack can open only its own folder in your
  OneDrive ("Apps › Tack Focus"); there's no Tack server. Works offline and catches up; notes written
  in two places at once are both kept; deletions never come back from a device that was offline.
- Staying signed in: Microsoft limits browser apps to 24 hours per sign-in, so Tack renews quietly,
  in the background where the browser allows it, or with a moment's visit to Microsoft when you
  open the app (iPhone, Firefox). It asks only if Microsoft wants you to sign in again.
- Offered only in builds with a Microsoft app ID (`VITE_MS_CLIENT_ID`); see docs/ONEDRIVE-SETUP.md.

## 0.1.0 (unreleased)

First version, for review before tackfocus.app goes live.

- Tasks: capture from a dock at the bottom of the screen, a Now card, ticking with a grace period
  and a small celebration, swipe right to focus and left to delete (with Undo), hold and drag to
  reorder, edit in place with Focus / Move / Delete tools, a folding Done section, paste a list
  to add many, keyboard shortcuts.
- Notes: sheets titled by their first line, saved as you type, list continuation, Make task,
  timestamps, writing tools above the keyboard.
- Light and dark glass themes with six accents, the orb's progress ring, a background that warms as
  the day's tasks get done, haptics on phones, reduced motion respected throughout.
- Installable PWA that works offline; updates apply quietly the next time you leave the app.
- Data stays on the device (IndexedDB), with daily snapshots for a week, and export/import of
  `tack.json` compatible with Tack for Windows.
- A sync-ready data model (per-field clocks, tombstones, sort keys) and merge engine; see
  `docs/SYNC.md` for the bring-your-own-storage sync design.
- A privacy page at `/privacy/`, linked from Settings → About: what stays on your devices, how
  the optional OneDrive sync works, the Microsoft permissions it asks for, and how to delete your
  data. It opens as itself (offline too) rather than as the app.
