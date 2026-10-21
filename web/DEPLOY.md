# Deploying Tack Focus to Cloudflare Pages

Tack Focus is live at https://tackfocus.app, served by the Cloudflare Pages project `tack`. GitHub
builds it and uploads it; Cloudflare only hosts it.

Tack Focus is a static site: HTML, one JS file, one CSS file, icons, a manifest and a service
worker. It needs **no Functions, Workers, KV, D1 or any other paid feature**. On the Pages free plan,
static requests and bandwidth are unlimited, so cost doesn't grow with users. GitHub Actions is free
for a public repository.

## What the build produces

`npm run build` writes `web/dist/`:

| File | Notes |
|---|---|
| `index.html` | App shell (always revalidated) |
| `assets/index-<hash>.js`, `assets/style-<hash>.css` | Fingerprinted, cached for a year |
| `sw.js` | Service worker with the exact precache list and a content version |
| `manifest.webmanifest`, `icons/*` | Install metadata and icons |
| `privacy/` | The privacy page (also the Microsoft app registration's privacy statement URL) |
| `.well-known/microsoft-identity-association.json` | Only when `VITE_MS_CLIENT_ID` is set (see Sync) |
| `_headers` | Cloudflare headers: CSP (with the boot script's hash), security headers, caching |

## How it deploys

[`.github/workflows/web.yml`](../.github/workflows/web.yml), in three jobs:

1. `test` runs on every pull request and on every push to `main` that touches the web app.
2. `build-site`, once the tests pass, builds with the real Microsoft app ID (checking the app ID
   made it into the build) and keeps `dist` as an artifact.
3. `deploy` uploads that artifact with `wrangler pages deploy`:
   - a push to `main` (a merged pull request) goes to production, https://tackfocus.app;
   - a pull request from this repository gets a preview at `<branch>.tack-6kt.pages.dev`.

The Cloudflare token reaches only `deploy`, which runs none of the repository's code: it checks out
just [`.github/cloudflare`](../.github/cloudflare), where wrangler is pinned and locked (exact
versions and integrity hashes), and installs it without install scripts. A compromised npm package
published later can't slip into a deploy, and a branch's build can't tamper with the job that
holds the token. To update wrangler: change the version in `.github/cloudflare/package.json`, then
`npm install --package-lock-only --ignore-scripts` there.

**Why GitHub and not Cloudflare's own builds:** from October 2026, the builds Cloudflare started from
Git kept stalling "Idle" in its build queue, and only a manual retry ran them. Those are paused
(project → Settings → Build → **Branch control**: automatic production deployments off, preview
deployments *None*). The repository stays connected, which Cloudflare allows alongside uploads.
Even paused, Cloudflare still queues an idle build of its own for every push; `deploy`'s last step
cancels any still waiting, so none can ever start late and put an older version back live (it
warns, and never fails the deploy, if it can't).

GitHub needs these (repository → Settings → **Secrets and variables → Actions**):

| Kind | Name | Value |
|---|---|---|
| Secret | `CLOUDFLARE_API_TOKEN` | A Cloudflare API token with **Account › Cloudflare Pages › Edit**, for this account only |
| Variable | `CLOUDFLARE_ACCOUNT_ID` | The Cloudflare account ID (in the dashboard URL, or Account home → ⋯ → Copy account ID) |
| Variable | `VITE_MS_CLIENT_ID` | The Microsoft Application (client) ID ([../docs/ONEDRIVE-SETUP.md](../docs/ONEDRIVE-SETUP.md)) |

The deploy job stops with an error if any of them is missing, rather than publish a site without
sync. If the token is ever leaked or expires: Cloudflare → My Profile → API Tokens → roll it, then
paste the new value into the secret.

## Setting it up from scratch

1. Cloudflare dashboard → **Workers & Pages** → **Create** → **Pages** → **Connect to Git** → choose
   the `tack` repository. Build settings (used only if Cloudflare builds): framework **None**, root
   directory **`web`**, build command **`npm ci && npm run build`**, output **`dist`**.
2. **Custom domain:** project → *Custom domains* → add `tackfocus.app` (and optionally
   `www.tackfocus.app`, redirected to the apex with a Redirect Rule). With the domain already on
   Cloudflare, DNS and the certificate are set up automatically.
3. Leave **Web Analytics, Rocket Loader and Zaraz off**: they inject scripts that the CSP blocks
   and that would break Tack's privacy promise.
4. Pause Cloudflare's automatic builds (Branch control, as above), and add the GitHub secret and
   variables.

Previews are public; protect them with Cloudflare Access (free for small teams) if you'd rather
they weren't.

## Uploading a build yourself

```bash
cd web
npm ci && npm run build
npx wrangler pages deploy dist --project-name tack --branch main
```

(Wrangler isn't a dependency; `npx` fetches it and asks you to log in. Set `VITE_MS_CLIENT_ID` in
the shell first, or the upload has no Sync section.)

## Sync (OneDrive)

Sync is offered only in builds that know the Microsoft app ID, which the deploy job takes from the
`VITE_MS_CLIENT_ID` repository variable. The app registration it comes from belongs to the Tack
project; [../docs/ONEDRIVE-SETUP.md](../docs/ONEDRIVE-SETUP.md) has every step. With the ID set, the
build also publishes `/.well-known/microsoft-identity-association.json` for Microsoft's
publisher-domain check. Without it, Tack Focus works exactly as before and shows no Sync section.

Sign-in only works on addresses registered with Microsoft: tackfocus.app, and the preview addresses
added to the app registration (a numbered deployment address like `1a2b3c4d.tack-6kt.pages.dev`
never is).

Sync needs no Functions or Workers either: the browser talks to Microsoft directly.

## After a deploy

- Open https://tackfocus.app, then **Settings → About** shows the version.
- Check the headers: `curl -sI https://tackfocus.app/ | grep -i content-security` should show the
  policy with a `sha256-…` hash. If the boot script changes, the build updates the hash; never edit
  `dist/_headers` by hand.
- In Chrome DevTools → Application: the manifest has no errors, the service worker is activated,
  and Cache Storage has `tack-<version>`.
- Icons are cached at Cloudflare's edge for a week: after changing them, purge the cache
  (tackfocus.app → Caching → Configuration → Purge Everything).
- Install it on a phone (see TESTING.md for the checklist).

## Releasing updates

Merge a pull request into `main`. Installed apps pick the update up the next time they're opened
online: the new service worker installs in the background and the app reloads itself, after
saving, the next time you leave it. Nobody is interrupted mid-task.

The `.app` top-level domain is on the HSTS preload list, so the site is HTTPS-only by default;
the `Strict-Transport-Security` header in `_headers` says the same.

## Rolling back

Pages keeps every deployment: project → *Deployments* → pick a previous one → **Rollback**.
Because data lives on each device, a rollback never touches anyone's tasks. The data format is
versioned, and newer fields are ignored by older code.
