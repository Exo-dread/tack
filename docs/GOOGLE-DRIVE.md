# Google Drive sync

Sync through Google Drive beside OneDrive, with the narrowest Google permission: `drive.file`
(non-sensitive, "files this app created or the user opened with it"). OneDrive's sync, its sign-in
and its rules don't change. The sync format doesn't change: the same marker, the same
`devices/<device id>.json` files, the same merge.

## Why `drive.file` needs a trick

Tack for Windows never goes online: it reads and writes a folder that Google Drive for desktop keeps
in step. Files that Drive for desktop uploads count as created by Drive for desktop, not by Tack
Focus, so `drive.file` can't see them. A PC's own device file would be invisible to every browser
and phone.

The fix is to reverse who creates the files: **Tack Focus creates every file in the folder through
the API, and Tack for Windows only ever writes into files Tack Focus created.** Access under
`drive.file` belongs to the file (its id), not to whoever changes it later.

## What was tested (2026-11-01, live, Drive for desktop 133 on Windows 11, `drive.file` only)

A test client in the "Tack Focus" Google Cloud project made a folder and files through the API;
Drive for desktop brought them to `H:\My Drive`; the PC changed them; the API checked what it could
still see.

| On the PC | Result through the API |
|---|---|
| Write a file in place | Same file id, new content: readable and writable |
| Tack's own write (temp file, then `MoveFileEx` over it, as `LocalSyncFolder` does) | Same file id, new content: readable and writable |
| Rename a file | Same id, new name |
| Delete a file | Same id, `trashed: true` |
| Create a new file | Invisible (not listed, not found by name) |
| Rename the folder | Same folder id, new name |
| Tack Focus writes a file by id | Reaches the PC within seconds |
| Rename a spare to a name nobody uses, then write it | Same id as the spare, new name and content: **works** |
| Rename a spare over the PC's existing file (one move) | Drive keeps the PC's file (invisible) and trashes the spare: **fails** |
| Delete the PC's file, then at once rename a spare to that name | Same as above: Drive merges the two into "replace": **fails** |
| Delete the PC's file, wait 90 s, then rename a spare to that name | Works |
| Trash the folder through the API while it holds PC-made files | 403 |

Not tested (needs two PCs): two PCs claiming the same spare at the same moment.

## The design

### Folder

`My Drive/Tack Focus/` created by Tack Focus, found again by `appProperties` (not by name, so a
rename doesn't lose it), with the same layout as OneDrive's: `tack-sync.json` (marker), `README.txt`,
`devices/`. In `devices/`, besides device files, Tack Focus keeps **spares**: empty files named
`spare-<random>.json`, which no reader takes for a device file (names are matched against the
device-id pattern). It keeps three; whenever it sees one claimed, it makes another.

### Tack for Windows with Google Drive

- **Finding the folder:** Drive for desktop's mounts (`<letter>:\My Drive`), looking for
  `Tack Focus` with the marker. Like OneDrive's app folder, that one folder is adopted by itself;
  any other marked folder is only suggested.
- **Never creates anything there.** If the folder or `devices/` is missing, it waits ("Open Tack
  Focus once to set up Google Drive"); a recreated folder would be invisible to Tack Focus.
- **Its own file:** if `devices/<id>.json` doesn't exist, it renames a spare (a random one of those
  there) to that name, then writes as today. With no spare, it waits.
- **A PC that already synced through this folder before the claim existed** (or whose file was
  made some other way): it takes a new device id (the existing "two devices with the same id" path:
  nothing is lost, everything merges) and claims a spare under the new name, so no name is reused
  and no timing matters. Its old file is renamed `retired-<old id>.json` once the new one is written.
- **Two PCs on one spare:** each checks on its next cycle that its file is still its own (step 2 of
  a sync cycle already does this); the loser claims another spare.
- **Noticing changes:** Google Drive for desktop's drive (streaming mode) tells no file system watcher
  about files that change in the cloud (found in the live test: a peer's file changed on `H:` with no
  event at all). So a Google Drive folder (claim mode, or any folder on a drive labelled "Google Drive")
  is also looked at every minute: the `devices` listing only (names, sizes, times), nothing opened or
  downloaded, and a cycle if it changed. OneDrive and folders on a disk keep the watcher alone.
- Everything else (reading peers, merging, limits, hostile-file rules) is unchanged.

### Tack Focus with Google Drive

- Connect Google Drive once in Settings, beside OneDrive (one provider at a time per device;
  devices on different providers don't see each other, as with two OneDrive accounts).
- Reads `devices/` by listing the folder: only files it created are listed, which is exactly the
  spares, claimed spares (device files of PCs) and its own file. Same caps as OneDrive (`MAX_PAGES`,
  20 MB per file, throttling, backoff).
- Calls only `https://www.googleapis.com/` (Drive v3) from the page: a new `connect-src` host.

### Signing in: a minimal Worker

Google gives a website a refresh token only with a client secret, and the refresh token must not
sit where page scripts can read it. So a Cloudflare Worker on `tackfocus.app/api/google/*`:

- `start`: redirects to Google (authorization code + PKCE, scope `openid email drive.file`,
  `access_type=offline`), with `state` bound to a short-lived HttpOnly cookie.
- `callback`: exchanges the code using the client secret (a Worker secret, never in the repo),
  stores the refresh token **encrypted** (AES-GCM, key in a Worker secret) in a cookie that is
  `HttpOnly; Secure; SameSite=Strict; Path=/api/google`, and returns to the app. No database.
- `token` (POST, same-origin only, with a custom header): turns the cookie into a one-hour access
  token for the page, kept in memory only.
- `signout`: clears this browser's cookie (the only copy of its refresh token). Not revoked at Google:
  a revoke ends the whole grant and would sign out your other browsers too; removing Tack's access
  everywhere is myaccount.google.com/permissions.
- Revoked access or an expired refresh token: the Worker clears the cookie and says so; the app
  shows "Reconnect Google Drive" and keeps working offline.
- Drive content never passes through the Worker: the page talks to `www.googleapis.com` itself.
- Per-IP and per-path rate limits answering 429 with `Retry-After`, as CLAUDE.md asks of any server.

### Verification

`drive.file` is non-sensitive: Google needs **brand verification** only (app name, logo,
tackfocus.app ownership, privacy policy, a few days), not the restricted-scope review or a security
assessment. Until then the app stays in testing mode: at most 100 named test users, and Google
expires refresh tokens after 7 days, so testers sign in again weekly.

## Contracts (what each part relies on)

### The folder in Drive (made only by Tack Focus)

| Item | Name | Content | `appProperties` |
|---|---|---|---|
| Folder | `Tack Focus` in My Drive's root | | `tack=folder` |
| Marker | `tack-sync.json` | `{"format":"tack-sync","version":1,"spares":true}` | `tack=marker` |
| README | `README.txt` | what the folder is (Google wording) | `tack=readme` |
| Folder | `devices` | | `tack=devices` |
| Spare | `devices/spare-<16 lowercase hex>.json` | empty (0 bytes) | `tack=spare` |
| Device file | `devices/<device id>.json` | a device file, as today | `tack=device` |

- Tack Focus finds its folder with `appProperties has { key='tack' and value='folder' } and 'me' in owners and trashed=false`
  (yours only: a Tack folder someone shared with you is never taken for your own).
  Two browsers connecting at once may make two: the one created first (`createdTime`, then id) is
  used by every device, so all agree.
- `"spares": true` in the marker is what puts Tack for Windows in **claim mode**, wherever the folder
  is. Readers ignore unknown fields, so older versions read the marker as before.
- Tack Focus keeps **3** unclaimed spares: after each listing of `devices`, if fewer than 3 names
  match `^spare-[0-9a-f]{16}\.json$`, it makes up to that many (never more than 3 in one cycle, and
  none if the listing already holds more than 20 spare names or was cut short).
- Its own device file: created (multipart upload, parents = `devices`) the first time, then updated
  by id (media upload). It never writes to a spare or to another device's file.
- A spare or claimed file is never trusted for anything but its bytes: names are matched against the
  device-id or the spare pattern, nothing else; a spare's content is ignored.

### Tack for Windows in claim mode

- **Only on Google Drive:** claim mode needs the marker's `"spares": true` *and* the folder on Google
  Drive (its drive root has `.shortcut-targets-by-id`, or its letter is a `mount_point_path` in
  `HKCU\Software\Google\DriveFS` → `PerAccountPreferences`, or it's inside `%USERPROFILE%\My Drive`;
  Windows cuts Drive's volume labels to "…- Googl…", so the label alone can't tell), or a claim record
  naming this folder and a device. Elsewhere (a OneDrive folder someone put that marker in) the marker
  is ignored and the folder syncs as a plain folder: one hostile marker can't stall sync.
- Never creates the folder, the marker, the README or `devices` (anything it made would be invisible
  to Tack Focus). If `devices` is missing, it waits and says to open Tack Focus once.
- Writing its own file when `devices/<id>.json` doesn't exist: pick a spare (names matching the
  spare pattern, sorted; index = a stable hash of the device id modulo their count, so two PCs
  rarely pick the same one), `File.Move(spare, <id>.json, overwrite: false)`, then write exactly as
  today (temp file, then move over). No spare: wait ("Open Tack Focus once to finish setting up
  Google Drive"), retry on the next folder change or in a few minutes.
- A claim record, `sync-claim.json` beside `sync.json` (local only): `{ "folder": <key>, "device": <id> }`,
  written straight after the rename (before the content is written, so a write that fails then can
  never lead to a needless migration). During a migration it also says `"retire": <old id>` (with
  `device` null until a spare is claimed), so a PC restarted mid-migration still retires its old file;
  a record with no device pins nothing. If its own file is in `devices` but the claim record
  doesn't name this folder and id (written by an older Tack, or by hand), the file may be invisible
  to Tack Focus: **migrate**, once a spare is there to claim (until then it writes in place, as
  before): copy tack.json to `tack.before-google.json` (local, kept for rollback; never overwritten),
  take a new device id (the same path as a device-id collision: nothing is lost, everything merges),
  claim a spare under the new id, write it, write the claim record, then rename the old file to
  `devices/retired-<old id>.json` (never deleted, so it can be put back; no reader takes it for a
  device file).
- **Its claimed file gone** (another PC won the same spare, you deleted it, a Drive hiccup): a new
  device id and another spare, never the same name again (Drive turns "delete, then rename a spare to
  that name" into a replace that keeps an invisible file: see the tests above). Once per loss.
- Two PCs claiming one spare at once: Drive keeps one rename. The loser finds its file gone and
  claims another spare under a new id (above); the winner may find the loser's bytes in its file,
  reads it as damaged (the `device` field doesn't match the name) and writes it again. No records are
  lost, only a cycle or two of delay.

### The sign-in Worker (`tackfocus.app/api/google/*`)

A Cloudflare Worker (not Pages Functions) on the route `tackfocus.app/api/google/*`, in `web/worker/`.
Settings: `GOOGLE_CLIENT_ID`, `SITE_ORIGIN` (vars), `GOOGLE_CLIENT_SECRET`, `SESSION_KEY` (32 random
bytes, base64) as Worker secrets, and a `RATE_LIMITER` rate-limit binding.

| Request | Does | Answers |
|---|---|---|
| `GET /api/google/start?return=/` | PKCE verifier + state into a flow cookie (`__Secure-tack-gflow`: AES-GCM, HttpOnly, Secure, SameSite=Lax, Path=/api/google/, 10 min); `return` must be a same-site path (`/…`, not `//`) | 302 to Google: scope `openid email https://www.googleapis.com/auth/drive.file`, `access_type=offline`, `prompt=consent`, `code_challenge_method=S256` |
| `GET /api/google/callback` | checks state against the flow cookie, exchanges the code (secret + verifier), checks the granted scopes are within `openid email profile drive.file` and include `drive.file`, needs a refresh token; takes `email` and `sub` from the id_token | sets the session cookie, clears the flow cookie, 302 to `<return>?google=connected`, or `/?google=error&reason=<denied\|scope\|state\|exchange\|config>` |
| `POST /api/google/token` | CSRF checks; decrypts the session; refreshes at Google | 200 `{ access_token, expires_in, email }`; 401 `{ error: "signed_out" }` (no, bad or expired session); 401 `{ error: "revoked" }` (Google said `invalid_grant`; cookie cleared); 503 + `Retry-After` when Google throttles or fails; 429 + `Retry-After` from the rate limit |
| `POST /api/google/signout` | CSRF checks; clears the cookie (no revoke: that would sign out every browser) | 204, cookie cleared |

- **Session cookie** `__Secure-tack-g`: `HttpOnly; Secure; SameSite=Strict; Path=/api/google/;
  Max-Age=15552000` (180 days), value = AES-GCM(`SESSION_KEY`, `{ v:1, rt, email, sub, iat }`), with
  the cookie's name as additional data. Older than 180 days → `signed_out`. A new refresh token
  from Google replaces the old one in the cookie.
- **CSRF**: `token` and `signout` are POST only, need `X-Tack: 1` (a custom header, so a cross-site
  page can't send it without a preflight, and the Worker answers no preflight), and an `Origin` equal
  to `SITE_ORIGIN` (or, without `Origin`, `Sec-Fetch-Site: same-origin`). `start` and `callback` are
  protected by state + PKCE.
- Every answer: `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
  no CORS headers. Other methods: 405. Other paths: 404.
- Google calls time out after 10 s. Nothing is logged but error codes: never a token, code or cookie.
- Rate limit: per IP and path, 30 a minute (`token` is called about once an hour per device).
- It never calls the Drive API. Drive contents go between the page and `www.googleapis.com` only.

### Tack Focus

- `web/src/sync/gauth.ts`: `connect()` goes to `/api/google/start?return=/`; `token(force)` posts to
  `/api/google/token` (same-origin, `X-Tack: 1`, 30 s timeout) and keeps the access token **in memory
  only** until 2 minutes before it runs out; `signOut()`; the `?google=` return is read and taken out
  of the address bar before anything shows.
- `web/src/sync/googledrive.ts`: a `SyncFolder` on Drive v3 (`https://www.googleapis.com/drive/v3`,
  uploads on `/upload/drive/v3`), with OneDrive's limits: 30 s timeouts, `MAX_PAGES`, 20 MB per file
  (checked before and while downloading), 429/503 (and 403 `rateLimitExceeded`/`userRateLimitExceeded`)
  as throttled with `Retry-After`, the folder made again if it's gone. Tags are Drive's `version`.
- The controller keeps one provider per device: `provider: 'onedrive' | 'google'` in its record
  (absent = OneDrive, as every record written so far). Turning sync off with Google signs out at the Worker.
- New `connect-src`: `https://www.googleapis.com`. The service worker never handles `/api/`.

## Changes to the rules (CLAUDE.md), Google only

- tackfocus.app gets one server: the sign-in Worker above.
- Tack Focus keeps a Google refresh token, but only encrypted in an HttpOnly cookie the page can't
  read; OneDrive's 24-hour rule and its token storage stay as they are.
- New `connect-src`: `https://www.googleapis.com` (and the Worker's own origin, which is the site).
- Tack for Windows adopts `<Google Drive>\My Drive\Tack Focus` by itself, like OneDrive's folder,
  and renames spares in it.

## Setting it up (the owner, once)

Done already (2026-11-01): the Google Cloud project **Tack Focus** (`tack-focus`) with the Drive API
on, its OAuth consent screen in **Testing** (External), you as a test user, and the web client
**Tack Focus web** whose id is in `web/worker/wrangler.toml` (public, like Microsoft's).

### Google Cloud Console (console.cloud.google.com, project Tack Focus)

1. **Google Auth Platform → Branding:** App home page `https://tackfocus.app/`, Privacy policy
   `https://tackfocus.app/privacy/`, Authorized domain `tackfocus.app`, developer contact email. No logo
   (a logo makes Google review the branding before it shows).
2. **Data Access → Add or remove scopes:** `openid`, `.../auth/userinfo.email`, `.../auth/drive.file`.
   Save. Nothing sensitive or restricted, so no scope review.
3. **Clients → Tack Focus web:** redirect URIs `https://tackfocus.app/api/google/callback` (keep) and
   `http://localhost:5173/api/google/callback` (for local tests only: remove it before publishing).
4. **Audience:** add your colleagues' Google addresses as test users (up to 100). In Testing, Google
   signs testers out after 7 days. To lift that: **Publish app**, verify `tackfocus.app` in Google
   Search Console (a DNS TXT record in Cloudflare), and submit brand verification (a few days).
5. **Clean up:** delete the client **drive.file test (delete after)**, and delete both
   `client_secret_941190850400-*.json` files from Downloads (and the Recycle Bin) once the secret is
   set in step 2 below. Never commit or share them.

### Cloudflare (the Worker; from `web/worker`, wrangler from `.github/cloudflare`)

1. `..\..\.github\cloudflare\node_modules\.bin\wrangler login` (opens Cloudflare in the browser).
2. `wrangler secret put GOOGLE_CLIENT_SECRET` and paste the client secret from the web client's JSON.
3. `wrangler secret put SESSION_KEY` and paste 32 random bytes in base64, made with:
   `$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)`
   (keep no copy: losing it only signs every browser out).
4. `wrangler deploy`: the Worker goes on the route `tackfocus.app/api/google/*` (Workers routes run in
   front of Pages, so only that path changes). Check: `https://tackfocus.app/api/google/nothing` answers 404 JSON.
5. Security → WAF → Rate limiting rules (optional, belt and braces): `/api/google/` path, 30 requests
   a minute per IP, block for a minute.

### GitHub and the site

1. Repository → Settings → Secrets and variables → Actions → Variables: `VITE_GOOGLE_SYNC` = `1`
   (only after the Worker answers; without it the site doesn't offer Google Drive).
2. Merge the release pull request: the site deploys with Google Drive offered; tag `v1.10.0` for Tack
   for Windows.
