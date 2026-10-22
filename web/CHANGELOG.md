# Tack Focus changelog

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
