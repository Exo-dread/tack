# Setting up OneDrive sync (for the Tack project)

Sync needs one thing the code can't provide: a **Microsoft app registration**, which tells Microsoft
"Tack Focus is allowed to ask people for access to its own OneDrive folder". It belongs to the
Tack project, not to a person, and it's free. Until it exists, Tack Focus simply doesn't offer sync
(the Settings section is hidden in builds without an app ID). Tack for Windows needs nothing from
here: it syncs through the OneDrive app on the PC.

Allow about 30 minutes. Everything below is done in a browser, signed in as the project.

## 1. A Microsoft account for the project

Create a Microsoft account used only for Tack (for example with an address at tackfocus.app, set up
through Cloudflare Email Routing, or a new outlook.com address). Turn on two-step verification and
keep its recovery details with the project's other credentials (Cloudflare, GitHub).

## 2. A Microsoft Entra tenant

App registrations live in a Microsoft Entra directory ("tenant"). Sign in to
https://entra.microsoft.com with the project account. If it says there's no directory, create a
free Azure account with the same project account (https://azure.microsoft.com/free); that gives it
a tenant. App registrations cost nothing; you don't need to create any Azure resources.

## 3. Register the app

Entra admin center → **Identity → Applications → App registrations → New registration**:

| Setting | Value |
|---|---|
| Name | **Tack Focus**. This is also the name of the folder in people's OneDrive ("Apps › Tack Focus"); renaming the app later doesn't rename existing folders. |
| Supported account types | **Accounts in any organizational directory and personal Microsoft accounts** |
| Redirect URI | Platform **Single-page application (SPA)**: `https://tackfocus.app/` |

Then, in the new app:

1. **Authentication** → under *Single-page application*, add a second redirect URI:
   `https://tackfocus.app/auth/` (where the quiet renewal lands). For trying sync locally, also
   add `http://localhost:5173/` and `http://localhost:5173/auth/` (and `:4173` for `npm run preview`).
   Leave *implicit grant* unticked and *Allow public client flows* at **No**.
2. **API permissions** → *Add a permission* → **Microsoft Graph** → **Delegated**: tick
   `Files.ReadWrite.AppFolder`, `offline_access`, `openid`, `profile`. **Remove** `User.Read`
   (added by default; Tack doesn't need it). No admin consent is needed.
3. **Branding & properties**:
   - Home page URL: `https://tackfocus.app/`
   - Privacy statement URL: `https://tackfocus.app/privacy/`
   - Logo: `web/public/icons/icon-512.png` (optional)
   - **Publisher domain** → `tackfocus.app`. Microsoft checks
     `https://tackfocus.app/.well-known/microsoft-identity-association.json`, which the Tack Focus
     build generates from the app ID (step 4), so do this after the first deploy with the ID.
4. **Overview** → copy the **Application (client) ID** (a GUID). It's public, not a secret.

There's no client secret and no certificate: Tack Focus is a public client using PKCE.

## 4. Give the app ID to the build

GitHub builds Tack Focus and uploads it to Cloudflare ([../web/DEPLOY.md](../web/DEPLOY.md)), so
the ID goes to GitHub: repository → **Settings → Secrets and variables → Actions → Variables** →
**New repository variable**:

| Name | Value |
|---|---|
| `VITE_MS_CLIENT_ID` | the Application (client) ID |

Optional: `VITE_MS_AUTHORITY` (default `https://login.microsoftonline.com/common`). Use
`…/consumers` to allow personal accounts only.

The next deploy picks it up (merge something, or re-run the latest `Tack Focus (web)` run on
`main`). Settings → Sync appears in Tack Focus.

To check that a build got it, open `/.well-known/microsoft-identity-association.json` on that
build's address (for example `https://<branch>.<project>.pages.dev/`): the build writes it only
when `VITE_MS_CLIENT_ID` is set, with the app ID inside.

If Cloudflare ever builds the site again instead, the ID goes in the Pages project: **Settings →
Variables and Secrets**, type **Text**, for Production and Preview separately (the **Production ⇕**
box at the top of Settings' right-hand column switches between them; it's hidden in a narrow
window). Variables added there don't show in `wrangler pages download config` or
`wrangler pages secret list`, so check the built file instead.

## 5. Check it

1. https://tackfocus.app/.well-known/microsoft-identity-association.json shows the app ID;
   then set the publisher domain (step 3).
2. Tack Focus → Settings → Sync → **Turn on sync**: Microsoft's page lists "Tack Focus", the
   publisher domain, and the four permissions; accept; back in Tack, "Sync is on".
3. In OneDrive on the web: `Apps/Tack Focus/` with `tack-sync.json`, `README.txt` and
   `devices/<id>.json`.
4. On the PC, once the OneDrive app has brought that folder down: Tack for Windows → Settings →
   Sync → **Turn on sync**.
5. Then the real-device checklist in [web/TESTING.md](../web/TESTING.md#sync).

## About "unverified"

Until the publisher is **verified**, Microsoft's consent page shows "unverified" next to the
publisher. Personal accounts can still accept. Some organisations block unverified apps for work
accounts. Publisher verification needs a Microsoft AI Cloud Partner Program account for the
project (free, but it verifies a business identity). Not needed to start.

## If something goes wrong

| Microsoft's page says | Fix |
|---|---|
| `AADSTS50011` redirect URI mismatch | Both URIs (`/` and `/auth/`) must be under *Single-page application*, exactly as above, including the trailing slash. |
| `AADSTS9002326` cross-origin token redemption is permitted only for SPA | The redirect URI was added under *Web* instead of *Single-page application*. |
| `AADSTS700016` application not found | `VITE_MS_CLIENT_ID` is wrong or the build predates it. |
| `AADSTS65001` / consent needed | An organisation's policy; try a personal account, or ask its admin. |
