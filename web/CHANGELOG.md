# Tack Focus changelog

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
