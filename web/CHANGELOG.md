# Tack Focus changelog

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
