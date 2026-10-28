# Tack Focus

Tack's tasks, notes and spaces as a web app for phones, tablets and browsers, live at
[tackfocus.app](https://tackfocus.app). Install it to your home screen and it opens like an app,
works offline, and keeps everything on your device. It's the pocket companion to [Tack for Windows](../README.md): same orb, same "Now",
same notes, same `tack.json`.

## What it does

- **Capture in seconds.** The task box sits at the bottom, under your thumb. Type, press return,
  close the app: the task is already saved (even a half-typed one survives). Paste a list to add
  many. Ctrl+Enter (or ⌘+Enter) adds a task straight into focus.
- **Share to Tack (Android).** Once Tack Focus is installed, it's in Android's share sheet: share a
  page, a link or some text from any app, tap *Add* on the card that says what came, and it's a
  task in the space on show (the page's title with its link; up to 20 lines of text, a task per
  line, as a paste), with *Undo*. Nothing is added without that tap, and nothing about it is sent
  anywhere. iPhone doesn't let web apps receive shares.
- **One thing in focus.** Swipe a task right (or tap it, then *Focus*) to make it *Now*: it glides
  into the card at the top. The orb's ring fills as today's tasks get done, and the page's light
  warms a little with it.
- **Tick, with a moment to change your mind.** The circle fills, the tick draws itself, the text is
  struck through line by line, a few sparks fly; tap again within the grace period to undo.
  Finishing the last task of the day gets a single soft bloom of light. No streaks, no points.
- **Delete with Undo, never "Are you sure?"** Swipe left, or use the row's tools.
- **Reorder** by holding a task and dragging (or dragging with a mouse, or Alt+↑/↓).
- **Spaces, if you want them.** Keep work, development and personal tasks apart: Settings →
  Spaces → *New space* (and rename Main there, say to "Work"). Once there's a second space, the
  title says which one is on show ("Work ▾"); tap it for All, each space with its open tasks, and
  *New space*. On a keyboard, Alt+1 to Alt+9 show the spaces in order and Alt+0 shows All, even
  while you type a task (the draft stays). A space shows its own tasks and sheets in the one order
  you set; new ones go into the space on show ("Add to Work"; from All, into Main). Tap a task →
  *Move* (or Notes ⋯ → *Move sheet to…*) to put it in another space, with Undo. The task in focus
  shows in other spaces as a line, "Now in Work: …", that takes you there; Done, Clear and the
  count go by space, and All shows everything with each task's space. Each device remembers which
  space it shows. Until you make a space, nothing about them is on screen.
- **Notes are scrap paper.** Plain text, titled by the first line, saved as you type. Lists continue
  themselves; *Make task* turns the current line or selection into tasks; *Time* stamps the date.
  While you read, a note shows its Markdown formatted (headings, lists, checkboxes you can tick,
  bold, code, quotes); click or type to write. *Format notes* in Settings turns it off.
- **Search.** The search button beside Settings (on a phone), or Ctrl+F (⌘F): every task, finished
  task and note in every space, as you type, whatever the case or accents, grouped Tasks, Done and
  Notes with each one's space. Tap one (or ↑ ↓ and Enter) to go there: the task lit up in its list,
  or the sheet open with the match selected. The same matching rule as Tack for Windows.
- **Light and dark**, six accents (each with a deeper shade for light mode), haptic ticks on phones,
  and full respect for *reduce motion*.
- **Phones get a dock, wide screens get both.** Above 900 px, Tasks and Notes sit side by side.
- **Updates itself.** A new version applies quietly when you open Tack Focus or come back to it,
  before you've touched anything, or while you're away. Never while you're editing a task or have a
  sheet open, and a half-typed task comes back as it was.
- **Your data stays yours.** IndexedDB on the device, a daily snapshot for the last seven days,
  export to (and import from) `tack.json`, which Tack for Windows reads.
- **Sync, if you want it.** Settings → Sync → *Turn on sync*, sign in with Microsoft once, and Tack Focus
  and Tack for Windows keep each other up to date through a folder in your own OneDrive. No Tack server,
  nothing to press. Where the browser won't renew the sign-in in the background (iPhone, Firefox), Tack
  pops over to Microsoft about once a day as it opens; if that's slow it says "Renewing your OneDrive
  sign-in…", offers *Open Tack now*, and opens by itself after 25 seconds. See [../docs/SYNC.md](../docs/SYNC.md).

Everything has a non-gesture route: tapping a task opens it with Focus / Move / Delete tools, and the
keyboard covers the rest (also listed in Settings → Keyboard shortcuts):

| Keys | Does |
|---|---|
| N or / | New task (jumps to the task box) |
| Ctrl+F (⌘F) | Search tasks and notes; ↑ ↓ and Enter go to a result, Esc closes |
| Enter / Ctrl+Enter (⌘+Enter) | Add a task / add it as your focus |
| 1 / 2 / , | Tasks / Notes / Settings |
| ↑ ↓, Alt+↑ / Alt+↓ | Move between tasks; move a task |
| Ctrl+Enter, Ctrl+D, Delete | Focus on a task, mark it done, delete it |
| Ctrl+Z | Undo |
| Ctrl+Enter, F5 (Notes) | Make a task from the line; insert the date and time |
| Alt+1 … Alt+9 / Alt+0 | The 1st … 9th space / All, once there are spaces (on a Mac, outside text fields) |

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

TypeScript and the web platform; **no runtime dependencies**. The whole app is about 56 KB of
JavaScript and CSS, gzipped (48 KB of it JavaScript).

```
web/
├── index.html              Shell + a tiny theme script (hashed in the CSP) to avoid a flash
├── src/
│   ├── main.ts             Boot: open storage, load, seed the first run, start the app
│   ├── model/              Pure, tested, no DOM
│   │   ├── workspace.ts    Every mutation; views (now/open/done, per space); batched change events
│   │   ├── spaces.ts       Spaces: names, ids, where a task or sheet shows
│   │   ├── search.ts       What search matches (the same rule as Tack for Windows)
│   │   ├── markdown.ts     How a note's Markdown reads (the same tree as Tack for Windows)
│   │   ├── merge.ts        Field-by-field merge (between tabs and between devices)
│   │   ├── hlc.ts          Hybrid logical clocks
│   │   ├── order.ts        Fractional sort keys
│   │   ├── file.ts         tack.json import/export (desktop-compatible)
│   │   ├── limits.ts       How much a device holds and passes on (the same numbers as Tack for Windows)
│   │   ├── welcome.ts      The first-run tasks, and leaving them behind when joining sync
│   │   ├── share.ts        What another app shared (Android's share sheet) as tasks
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
│   │   ├── notes.ts        Sheets, list continuation, make task, the formatted view
│   │   ├── markdownview.ts A note's Markdown tree as elements (no markup from text)
│   │   ├── spaces.ts       Spaces on screen: the switcher, Move to…, the space on show
│   │   ├── search.ts       Search: the box, the results, going to what's found
│   │   ├── settings.ts     Bottom sheet: appearance, spaces, sync, data, install, about
│   │   ├── sheet.ts        Small bottom sheets (the switcher, Move to…) and the ⋯ menu
│   │   ├── motion.ts       Springs (CSS linear()), FLIP, reduced motion
│   │   ├── viewport.ts     Keeps the app inside the visible area above the keyboard
│   │   ├── share.ts        Takes a share off the address bar before anything else, keeps it until answered
│   │   ├── renewing.ts     What shows while the OneDrive sign-in renews at Microsoft
│   │   ├── sw.ts           Registers the service worker; applies updates at a safe moment
│   │   └── orb.ts, toast.ts, haptics.ts, theme.ts, icons.ts, install.ts, dom.ts
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

The privacy page users see is [`public/privacy/`](public/privacy/index.html), served at
[tackfocus.app/privacy/](https://tackfocus.app/privacy/) and linked from Settings → About. It's also the
privacy statement URL in the Microsoft app registration, so keep it in step with what the apps do.
