# Deploying Tack Focus to Cloudflare Pages

Nothing has been deployed. These are the steps for when you're ready.

Tack Focus is a static site: HTML, one JS file, one CSS file, icons, a manifest and a service
worker. It needs **no Functions, Workers, KV, D1 or any other paid feature**. On the Pages free plan,
static requests and bandwidth are unlimited, so cost doesn't grow with users. (Builds: 500 a month
on the free plan.)

## What the build produces

`npm run build` writes `web/dist/`:

| File | Notes |
|---|---|
| `index.html` | App shell (always revalidated) |
| `assets/index-<hash>.js`, `assets/style-<hash>.css` | Fingerprinted, cached for a year |
| `sw.js` | Service worker with the exact precache list and a content version |
| `manifest.webmanifest`, `icons/*` | Install metadata and icons |
| `_headers` | Cloudflare headers: CSP (with the boot script's hash), security headers, caching |

## Option A: connect the GitHub repository (recommended)

1. Cloudflare dashboard → **Workers & Pages** → **Create** → **Pages** → **Connect to Git** → choose
   the `tack` repository.
2. Build settings:
   - Framework preset: **None**
   - Root directory: **`web`**
   - Build command: **`npm ci && npm run build`**
   - Build output directory: **`dist`**
   - Environment variable: `NODE_VERSION` = `22` (also pinned in `web/.node-version`)
3. Production branch: `main` (or a dedicated `web` branch if you prefer to release by merging).
4. **Custom domain:** project → *Custom domains* → add `tackfocus.app` (and optionally
   `www.tackfocus.app`, redirected to the apex with a Redirect Rule). With the domain already on
   Cloudflare, DNS and the certificate are set up automatically.
5. Leave **Web Analytics off** (Tack's privacy promise).

Every other branch gets a preview at `<branch>.<project>.pages.dev`. Those are public; protect them
with Cloudflare Access (free for small teams) if you'd rather they weren't.

The first build on Cloudflare's Linux image installs Linux builds of TypeScript 7 and Rolldown;
`package-lock.json` lists them, but check that first build's log.

## Option B: upload a build yourself

```bash
cd web
npm ci && npm run build
npx wrangler pages deploy dist --project-name tack-focus
```

(Wrangler isn't a dependency; `npx` fetches it when needed and asks you to log in.)

## Sync (OneDrive)

Sync is offered only in builds that know the Microsoft app ID: set `VITE_MS_CLIENT_ID` in the Pages
project's variables (Production, and Preview if wanted) and redeploy. The app registration it comes
from belongs to the Tack project; [../docs/ONEDRIVE-SETUP.md](../docs/ONEDRIVE-SETUP.md) has every
step. With the ID set, the build also publishes `/.well-known/microsoft-identity-association.json`
for Microsoft's publisher-domain check. Without it, Tack Focus works exactly as before and shows no
Sync section.

Sync needs no Functions or Workers either: the browser talks to Microsoft directly.

## After the first deploy

- Open https://tackfocus.app, then **Settings → About** shows the version.
- Check the headers: `curl -sI https://tackfocus.app/ | grep -i content-security` should show the
  policy with a `sha256-…` hash. If the boot script changes, the build updates the hash; never edit
  `dist/_headers` by hand.
- In Chrome DevTools → Application: the manifest has no errors, the service worker is activated,
  and Cache Storage has `tack-<version>`.
- Install it on a phone (see TESTING.md for the checklist).

## Releasing updates

Push to the production branch (or run Option B again). Installed apps pick the update up the next
time they're opened online: the new service worker installs in the background and the app reloads
itself, after saving, the next time you leave it. Nobody is interrupted mid-task.

The `.app` top-level domain is on the HSTS preload list, so the site is HTTPS-only by default;
the `Strict-Transport-Security` header in `_headers` says the same.

## Rolling back

Pages keeps every deployment: project → *Deployments* → pick a previous one → **Rollback**.
Because data lives on each device, a rollback never touches anyone's tasks. The data format is
versioned, and newer fields are ignored by older code.
