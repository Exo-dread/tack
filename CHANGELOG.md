# Changelog

## Unreleased

- **Ready to sync with Tack Focus.** tack.json gains the `tackFocus` section Tack Focus writes: a clock
  for every field, sort keys for the list, and deletions remembered for 90 days. The top level is
  unchanged, so Tack 1.1 still opens the file; a file saved by 1.1, or edited by hand, is taken as it
  is. Merging is a port of Tack Focus's engine, checked against the same test vectors.
- **Settings → Import.** Merges a tack.json from Tack Focus, another PC or a backup: new things are
  added, the newer edit wins, a note changed in both places is kept twice. Undo puts it all back.

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
