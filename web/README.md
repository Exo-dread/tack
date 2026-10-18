# Tack Focus

Tack's tasks and notes as a web app for phones, tablets and browsers, made for **tackfocus.app**.
Install it to your home screen and it opens like an app, works offline, and keeps everything on
your device. It's the pocket companion to [Tack for Windows](../README.md): same orb, same "Now",
same notes, same `tack.json`.

## What it does

- **Capture in seconds.** The task box sits at the bottom, under your thumb. Type, press return,
  close the app: the task is already saved (even a half-typed one survives). Paste a list to add
  many. Ctrl+Enter (or ⌘+Enter) adds a task straight into focus.
- **One thing in focus.** Swipe a task right (or tap it, then *Focus*) to make it *Now*: it glides
  into the card at the top. The orb's ring fills as today's tasks get done, and the page's light
  warms a little with it.
- **Tick, with a moment to change your mind.** The circle fills, the tick draws itself, the text is
  struck through line by line, a few sparks fly; tap again within the grace period to undo.
  Finishing the last task of the day gets a single soft bloom of light. No streaks, no points.
- **Delete with Undo, never "Are you sure?"** Swipe left, or use the row's tools.
- **Reorder** by holding a task and dragging (or dragging with a mouse, or Alt+↑/↓).
- **Notes are scrap paper.** Plain text, titled by the first line, saved as you type. Lists continue
  themselves; *Make task* turns the current line or selection into tasks; *Time* stamps the date.
- **Light and dark**, six accents (each with a deeper shade for light mode), haptic ticks on phones,
  and full respect for *reduce motion*.
- **Phones get a dock, wide screens get both.** Above 900 px, Tasks and Notes sit side by side.
- **Your data stays yours.** IndexedDB on the device, a daily snapshot for the last seven days,
  export to (and import from) `tack.json`, which Tack for Windows reads.
- **Sync, if you want it** (switched on once the Microsoft app registration exists; see
  [../docs/SYNC.md](../docs/SYNC.md)). Settings → Sync → *Turn on sync*, sign in with Microsoft
  once, and Tack Focus and Tack for Windows keep each other up to date through a folder in your own
  OneDrive. No Tack server, nothing to press.

Everything has a non-gesture route: tapping a task opens it with Focus / Move / Delete tools, and the
keyboard covers the rest (see Settings → Keyboard shortcuts).

## Design notes

- **Not the desktop panel, shrunk.** The desktop puts its add box at the top of a small floating
  panel. On a phone the top of the screen is the hardest place to reach, so the task box lives in a
  dock at the bottom that rides above the keyboard, and the Tasks/Notes switch sits next to it.
  When you type, the switch steps aside and the box takes the whole width.
- **The dock changes with what you're doing:** the task box on Tasks, the sheet tabs on Notes, and
  writing tools (*Make task*, *Time*, *Done*) above the keyboard while you write.
- **Motion has a job.** Lists animate with FLIP, so a task glides from the list into focus, or down
  into Done, and a new task lifts from the box into place. Springs settle in about half a second.
  Nothing loops; with reduce motion, everything becomes a short fade or an instant change.
- **First run teaches by doing**, as on the desktop: the starter tasks are the instructions.

## Architecture

TypeScript and the web platform; **no runtime dependencies**. The whole app is about 35 KB of
JavaScript and CSS, gzipped.

```
web/
├── index.html              Shell + a tiny theme script (hashed in the CSP) to avoid a flash
├── src/
│   ├── main.ts             Boot: open storage, load, seed the first run, start the app
│   ├── model/              Pure, tested, no DOM
│   │   ├── workspace.ts    Every mutation; views (now/open/done); batched change events
│   │   ├── merge.ts        Field-by-field merge (tabs today, devices tomorrow)
│   │   ├── hlc.ts          Hybrid logical clocks
│   │   ├── order.ts        Fractional sort keys
│   │   ├── file.ts         tack.json import/export (desktop-compatible)
│   │   └── text.ts, time.ts, ids.ts, types.ts
│   ├── sync/               Optional sync through your OneDrive (docs/SYNC.md)
│   │   ├── engine.ts       One sync cycle: read others' device files, merge, write ours
│   │   ├── devicefile.ts   The device file format
│   │   ├── onedrive.ts     The app folder through Microsoft Graph
│   │   ├── msauth.ts       Sign-in: authorization code + PKCE, quiet renewal
│   │   └── controller.ts   When to sync, status, retries, one tab at a time
│   ├── store/
│   │   ├── db.ts           IndexedDB (falls back to localStorage, then memory with a warning)
│   │   ├── persist.ts      Saves: tasks immediately, typing debounced, everything on hide
│   │   └── prefs.ts        Per-device preferences (localStorage)
│   ├── ui/                 Views and interaction, plain DOM
│   │   ├── app.ts          Shell: header, dock, modes, layout, shortcuts, first run
│   │   ├── tasks.ts        Keyed rendering + FLIP, ticking, editing in place
│   │   ├── gestures.ts     Swipe and long-press reorder (pointer events)
│   │   ├── notes.ts        Sheets, list continuation, make task
│   │   ├── settings.ts     Bottom sheet: appearance, data, install, about
│   │   ├── motion.ts       Springs (CSS linear()), FLIP, reduced motion
│   │   ├── viewport.ts     Keeps the app inside the visible area above the keyboard
│   │   └── orb.ts, toast.ts, haptics.ts, theme.ts, icons.ts, install.ts, sw.ts, dom.ts
│   ├── styles/main.css     Tokens (dark/light/accents), layout, components
│   └── sw.js               Service worker (precache list injected at build)
├── public/                 Manifest, icons, robots.txt, privacy/ (the privacy page), auth/ (sign-in landing page)
├── deploy/_headers         Cloudflare Pages headers (CSP etc.), copied into the build
└── tests/                  Vitest (model, sync), Playwright (app, in WebKit and Chromium), fakes/ (a stand-in Microsoft)
```

**Why no framework?** Tack's UI is two lists, a text area and a dock. The hard parts (gestures,
FLIP animation, keyboard-aware layout, offline storage) are hand-work in any framework. Plain
TypeScript keeps the download tiny, the dependency list at zero, and the code readable top to
bottom, in the spirit of Tack for Windows, which references nothing beyond .NET and WPF.

**Saving.** Every task change is written to IndexedDB straight away, in its own small
transaction. Note typing is saved after a short pause, and everything (including a tick still in
its grace period, or a task being edited) is written when the app is hidden. Writes are per
record, so two open tabs never overwrite each other; tabs tell each other about saves and merge.

**Sync.** Records carry a clock per field, deletions are tombstones, and order is a sort key, so the
merge that keeps tabs consistent is the one sync uses. Each device writes its own file to the
OneDrive app folder and merges everyone else's. See [../docs/SYNC.md](../docs/SYNC.md); the
Microsoft app registration it needs is in [../docs/ONEDRIVE-SETUP.md](../docs/ONEDRIVE-SETUP.md).

## Develop

Requires Node 22 (20.19+ works).

```bash
npm install
npm run dev          # http://localhost:5173 (no service worker in dev)
npm run build        # typecheck + production build into dist/
npm run preview      # serve dist/ at http://localhost:4173 (with the service worker)
npm test             # unit tests (model, merge, file format)
npm run e2e          # end-to-end tests (first time: npx playwright install chromium webkit)
npm run icons        # regenerate PNG icons (needs Python + Pillow)
```

See [TESTING.md](TESTING.md) for what's covered automatically and what to check on real phones,
and [DEPLOY.md](DEPLOY.md) for Cloudflare Pages.

## Privacy

No accounts, no analytics, no ads, no third-party scripts or fonts. The Content Security Policy
allows the app's own origin, plus Microsoft's sign-in, Graph and OneDrive hosts for sync. Your tasks
and notes never leave the device unless you export them, or turn on sync, which sends them only to
your own OneDrive (plain files, not end-to-end encrypted yet). See https://tackfocus.app/privacy/.
