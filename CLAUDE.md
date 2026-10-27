# Notes for coding sessions

Read [CONTRIBUTING.md](CONTRIBUTING.md) first: its house rules apply. This file adds the security rules that keep
Tack what it promises to be, and how to work on this repo without touching anyone's real data.

## What Tack is, and must stay

- **No server, no accounts, no telemetry.** tackfocus.app is static files on Cloudflare Pages: no Functions,
  Workers, `_worker.js` or `_redirects`. If one is ever added (discuss first), it needs per-IP and per-path rate
  limits that answer 429 with `Retry-After`.
- **Tack for Windows never goes online.** No `HttpClient`, no sockets, no NuGet packages. Sync is plain file I/O in
  the local OneDrive folder; the OneDrive app does the uploading.
- **No secrets in the repo, and none in `VITE_*` variables:** every `VITE_` value ships to every browser. The
  Microsoft client ID is public (it's also served at `/.well-known/microsoft-identity-association.json`). It is not a
  secret, and the app stays a public SPA client: auth code + PKCE, no client secret, certificate or implicit grant.
- **Scope stays `openid profile offline_access Files.ReadWrite.AppFolder`.** Anything wider needs a discussion first.
- **Tokens** live only in the IndexedDB meta record `auth`. The refresh token goes only to the authority's
  `/oauth2/v2.0/token`, and the access token only to `https://graph.microsoft.com/`. OneDrive download links are
  fetched without it. Tokens are never logged, put in a URL, exported or written to a device file. The refresh token
  is never extended past 24 hours from sign-in.
- **Imports and device files carry records only** (tasks, sheets, spaces, focus). They never change settings, the
  device id, the sync folder, launch at startup, the hotkey, the launcher, attachments or snippets.
- **Local files stay local:** launcher.json, attachments.json with the attachments folder, and snippets.json with the
  snippets folder are never synced, imported or exported, and never written into tack.json. launcher.json is written
  only from Settings → Launcher, attachments only by a drop onto the orb, snippets only by a paste in Notes. Launches
  (shortcuts, a task's paperclip, recent files) go through `ShortcutRules` and `ShellExecute` with arguments as their
  own field, never through `cmd` or a built command line.
- **The installer changes where Tack.exe lives, never where its data lives.** An installed Tack, like the zip,
  keeps its data in `%LOCALAPPDATA%\Tack` (no `TackData` beside it); setup and uninstall never touch that folder,
  close only the Tack.exe in the install folder (Restart Manager, never by force), and remove the Run value
  "Tack" only when it points at the installed exe. "Start Tack" after setup runs as the user, never elevated.
- **Tack for Windows adopts only `<OneDrive>\Apps\Tack Focus` by itself.** Any other folder with Tack's marker is
  only suggested; the user picks it with Choose folder.

## Untrusted input

Where it comes from, riskiest first: **device files in the sync folder** (anyone who can write the user's app
folder), **tack.json imports** (from anywhere), then paste, the URL query and hash, drags onto the orb, pasted images,
the share target's query (Tack Focus), launcher.json, attachments.json, snippets.json and browser storage.

- Read it with the existing validators, never by hand: `parseDeviceFile` / `DeviceFile.Parse`, `readTaskRecord`,
  `readSheetRecord`, `readSpaceRecord` / `TackFile.ReadTaskRecord`…, `isFileDate` / `IsFileDate`, `isDeviceId`,
  `cleanSpaceName` / `SpaceField.CleanName`, and `JSON.parse(text, wellFormedValues)`.
- **Ignore unknown fields, validate known ones strictly, bound everything.** Don't reject unknown fields: older
  versions must read newer files (docs/SYNC.md), and `version` is what gates an incompatible change.
- **One rule per field.** A value one entry point accepts (an import, say) must be one the sync reader accepts too.
  Store the normalised form, never the raw string.
- **Limits** live in `web/src/model/limits.ts` and `src/Tack/Sync/DeviceFile.cs`, and they must match. A device
  must never hold or write more than its peers accept: `MAX_RECORDS` (200,000, deletions included) per file. Sync
  intake (`overBudget` / `OverBudget`) sets a file aside as `full` unless it fits: one adding up to `MAX_TRICKLE`
  (2,000) new records may fill a device to `MAX_RECORDS`, one adding more only to `MAX_TAKEN` (180,000). Spaces
  count as records: there is no intake cap on spaces (any cap that depends on what the reader holds can split
  honest devices). Screens list at most `MAX_SPACES_LISTED` / `SpaceView.MaxListed` (1,000) at a time, with
  More spaces. **Take it in anyway** lifts `MAX_TAKEN` for one version of one file. Imports stop at `MAX_TAKEN`,
  a paste at `MAX_PASTE_LINES`; writers trim `seen` to `MAX_SEEN`. The write guard counts records and bytes and says
  so in Settings on every cycle. Changing a limit is a format change: both apps, readers and writers, in one release.
- Keep the size caps: 20 MB for imports and device files (checked before reading, and while streaming), 16 MB for
  attachments.json, 256 KB for launcher.json and snippets.json. `JsonDocument` keeps its default `MaxDepth` (64).
- **Peer data never becomes** a URL, a file path, HTML or something to launch. Peer file names are only matched
  against the device-id pattern.
- There is no window `message` or `storage` listener. Adding one needs origin and source checks. `BroadcastChannel('tack')`
  messages only trigger a reload from IndexedDB.
- **Both apps behave identically.** A change to `web/src/model` or `web/src/sync` is ported to `src/Tack/Sync` in the
  same pull request. Regenerate the vectors (`npm run vectors`, then `dotnet run --project tests/Tack.Tests -c Release -- --update`);
  they should change only when the format is meant to.
- **Every new limit or input path gets a hostile case** in `web/tests/unit/synchostile.test.ts` and
  `tests/Tack.Tests/HostileFileTests.cs`, written to fail without the fix. The rule a bad file must never break:
  it can't stop sync, swell everyone's files for good, or hide a device's changes.

## The web page

- The CSP lives in `web/deploy/_headers`. No `unsafe-inline` or `unsafe-eval`. The only inline script is the theme
  boot script in `index.html`, hashed at build by `vite.config.ts`: keep it tiny. No inline styles or event handlers.
- A new `connect-src` or `frame-src` host needs a reason, in the `_headers` comment and in the pull request.
- **No `innerHTML`, `insertAdjacentHTML`, `document.write`, `eval` or `new Function`.** Data goes through
  `h({ text })`, `textContent` or `setAttribute`. `svg()` takes static icon constants only. No `href` from data, and
  selectors built from ids use `CSS.escape`.
- No third-party scripts, fonts or analytics. Rocket Loader, Web Analytics and Zaraz stay off for the zone.
- The service worker handles same-origin GETs only, so tokens never pass through it.

## Calling Microsoft

- Every request has a timeout (30 s; the reachability probe 5 s; the hidden frame 10 s).
- **429, 503 (and 509 from Graph) mean "throttled".** Honour `Retry-After` (30 s if missing, 6 h at most). Nothing
  syncs inside that window, whatever asked for it: an edit, a return to the app, hiding it, `online`, or Sync now.
  Every sync goes through `schedule()`, which respects `notBefore` (the last exchange on hiding checks it itself).
- **Back off, never loop.** Failures back off from 5 s to 30 min. A folder listing stops at `MAX_PAGES`. The hidden
  renewal frame backs off after timeouts and hands over to the visit path. Only one tab syncs (Web Locks), and
  polling happens only while the app is visible.
- A new Microsoft call gets a test with a fake that answers 429.

## CI/CD

- Workflows run on `push` (main, `v*` tags) and `pull_request` only. Never `pull_request_target`, `workflow_run` or `issue_comment`.
- Permissions are `contents: read` by default. Only `release.yml` has `contents: write`.
- `CLOUDFLARE_API_TOKEN` appears only as step `env` in web.yml's deploy job (the upload and cleanup steps): never in
  build or test jobs, and never echoed. `CLOUDFLARE_ACCOUNT_ID` and `VITE_MS_CLIENT_ID` are variables, because
  they're public. Fork pull requests never reach build-site or deploy.
- Branch names, tags and PR text reach scripts only through `env:`, never `${{ }}` inside `run:`.
- Install from lockfiles (`npm ci`). The deploy job's wrangler is locked in `.github/cloudflare` and installed with
  `--ignore-scripts`.
- New or updated actions are pinned to a full commit SHA with the version in a comment. Tools a workflow
  downloads (Inno Setup in `release.yml`) are one exact version from the vendor's own release, checked against
  a pinned SHA-256 before they run, and installed into the runner's temp folder. Checkouts use
  `persist-credentials: false` unless a step pushes with git.
- Desktop releases come only from tags on commits already on main. `release.yml` checks this; don't remove the check.

## Working on this machine

- **Never touch the user's real Tack:** the copy that's running, `%LOCALAPPDATA%\Tack`, or their OneDrive folder.
  Don't quit it, and don't read or write its data. Tests and harnesses use temp folders.
- **Test copies run portable:** create a `TackData` folder next to the test exe, set `TACK_INSTANCE`, and set
  `"launchAtStartup": false` in its tack.json before the first start. Any Tack.exe that starts with launch at
  startup on rewrites `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` "Tack" to itself. Note that value
  before, and set it back exactly afterwards. Picture and timing harnesses (`--frames`, `--screens`, `--unfold`)
  run their copy with a `uitest…` `TACK_INSTANCE`.
- **Installer tests** use `build.ps1 -InstallerTest <name>` (its own AppId and uninstall entry), installed
  `/CURRENTUSER /VERYSILENT /DIR=<temp folder>` with `TACK_INSTANCE` set, never for all users, and uninstalled after.
- Playwright: if port 4173 is busy, set `E2E_PORT` to a free one. Don't stop servers you didn't start.
- **Production:** normal page loads only. No load tests or probing against tackfocus.app, Microsoft or GitHub.
- Never print, copy or read secret values: GitHub, Cloudflare and wrangler tokens, or what's in
  `.claude/settings.local.json`. Never commit `.claude/settings.local.json`, `.claude/worktrees/` or any `.env` file.
- Security findings stay out of the public repo until they're fixed and released.

## Shipping

- Local commits are fine. **Pushing, opening or merging a pull request, tagging, releasing and deploying each need
  the user's explicit yes**, every time. A merge to main deploys tackfocus.app.
- Before a pull request: `dotnet build src/Tack -c Release -warnaserror`, `dotnet run --project tests/Tack.Tests -c Release`,
  and `cd web; npm run typecheck; npm test`. Run Playwright (`npm run e2e`) for UI or sync changes.
- If a change touches both apps, bump both versions and update both changelogs: `src/Tack/Tack.csproj` and
  `CHANGELOG.md` for Tack for Windows, and `web/package.json` and `web/CHANGELOG.md` for Tack Focus. If sync
  behaviour or a limit changes, update docs/SYNC.md too.
- Desktop releases: merge the release pull request, then tag main (`v1.x.y`). Tack Focus has no tags: the merge deploys it.
