# Contributing to Tack

Thanks for helping. Tack is deliberately small, so the most valuable contributions make it *better at
what it already does*: smoother, clearer, lighter, more reliable. Please read [docs/DESIGN.md](docs/DESIGN.md)
before proposing a new feature; if an idea adds a setting, a screen or a dependency, open an issue first.

## Getting started

You need Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
Visual Studio 2022, Rider and VS Code all work; open `Tack.sln`.

```powershell
dotnet run --project src/Tack
```

For Tack Focus (`web/`): Node 22, then `cd web; npm install; npm run dev` (see [web/README.md](web/README.md)).

**Keep your real data out of the way while developing:** create an empty `TackData` folder next to the
built exe (for example `src/Tack/bin/Debug/net8.0-windows/TackData`). Tack then stores everything there
(portable mode) instead of in `%LOCALAPPDATA%\Tack`. Delete the folder's contents to see the first-run
experience again.

Only one Tack runs per user. Launching it again just opens the running one, so quit your installed copy
(right-click the orb → Quit) before debugging, or set `TACK_INSTANCE=dev` (any value) for the debug copy
so both can run side by side (with its own `TackData` folder, as above).

## Where things live

| Path | What |
|---|---|
| `src/Tack/AppShell.cs` | Coordinates the orb and panel: expand/collapse, placement, shortcut, full-screen handling |
| `src/Tack/Model/` | `Workspace` (all user state and every mutation), `TaskItem`, `NoteSheet`, `Settings`, `Search` (what Ctrl+F matches: the same rule as Tack Focus) |
| `src/Tack/Sync/` | The data model shared with Tack Focus: clocks, sort keys, merges, the `tackFocus` section of tack.json, device files and the sync engine. A port of `web/src/model` and `web/src/sync` that must give exactly the same results |
| `src/Tack/Services/` | `StateStore` (JSON persistence), `StateFile` (tack.json and records), `FolderSync` and `LocalSyncFolder` (sync through a folder), Import, accent colour, startup registration, log, single instance |
| `src/Tack/Interop/` | Win32 and DWM: glass, monitors/DPI, hotkey, shell watcher. All P/Invoke is in `NativeMethods.cs` |
| `src/Tack/Windows/` | The three windows: orb, peek bubble, panel |
| `src/Tack/Launcher/` | The optional radial launcher (off by default): `Core/` is platform-neutral and unit-tested; the rest is its window, settings card and Windows launching/placement. See [docs/LAUNCHER.md](docs/LAUNCHER.md) |
| `src/Tack/Views/` | Tasks, Notes and Settings pages shown inside the panel, and search's results |
| `src/Tack/Controls/` | Custom-drawn orb, animation helpers, converters |
| `src/Tack/Themes/` | `Theme.xaml` (fonts and every control style), `Dark.xaml` / `Light.xaml` (colour palettes) |
| `tests/Tack.Tests/` | Tests for the sync model, tack.json, spaces, hostile sync files and the launcher (its logic, plus its window and settings card off screen). A plain console app: no test packages |
| `tests/shared/` | Cases both apps' tests read: `search.json`, what search matches (folding, where a match is, a workspace searched) |
| `tests/vectors/` | Shared test vectors: `sync.json` (model) and `sync-engine.json` (sync cycles, device files) from Tack Focus, `desktop-export.json` and `desktop-device.json` from Tack for Windows, `legacy-1.1.json` written by Tack 1.1 |
| `tools/make_icon.py` | Regenerates `Assets/Tack.ico` and `docs/icon.png` (needs Pillow) |
| `build.ps1` | Builds the release zips into `artifacts/` |
| `web/` | Tack Focus, the web and mobile app (TypeScript, no runtime dependencies). See [web/README.md](web/README.md) |
| `docs/` | `DESIGN.md` (why Tack is the way it is), `SYNC.md` (how sync works, live since 1.3.0 / 0.2.0), `LAUNCHER.md` (the radial launcher), `ONEDRIVE-SETUP.md` (the Microsoft app registration) |
| `.github/workflows/` | `ci.yml` (build and tests), `release.yml` (Tack downloads on a tag on `main`), `web.yml` (Tack Focus tests and deploy to tackfocus.app) |
| `CLAUDE.md` | The security rules on top of these house rules (untrusted input, limits, calling Microsoft, CI/CD), for coding sessions and people alike |

## House rules

- **No new dependencies** without discussion. Tack references nothing beyond .NET and WPF.
- **No network access**, no telemetry, no accounts. Ever. Sync goes through a folder on disk that the
  OneDrive app keeps in step; Tack for Windows itself never opens a connection.
- **Nothing runs while idle.** No polling timers or looping animations; react to events instead.
- **Animations are short** (≤ 260 ms), eased, and never block input.
- **Place windows in physical pixels** via `ScreenGeometry`, not WPF's `Left`/`Top`.
- **Colours come from the palettes.** Reference brushes with `DynamicResource` (never `StaticResource`
  or a hex literal), and add any new key to both `Themes/Dark.xaml` and `Themes/Light.xaml`.
- **Every user-visible mutation goes through `Workspace`**, so saving and the orb stay in sync.
- **Destructive actions get Undo**, not confirmation dialogs.
- **Icon-only buttons need `AutomationProperties.Name`** so screen readers can announce them.
- Match the surrounding code: file-scoped namespaces, `_camelCase` fields, small focused classes.

## Testing

```powershell
dotnet run --project tests/Tack.Tests -c Release            # sync model, tack.json, spaces, launcher
cd web; npm test                                            # Tack Focus, including the shared vectors
```

The two apps must merge and sync identically, so Tack for Windows' `Sync` code is checked against
vectors computed by Tack Focus's own code. If you change `web/src/model` or `web/src/sync`, regenerate
them and port the change: `npm run vectors` (in `web/`) rewrites `tests/vectors/sync.json` and
`sync-engine.json`, then `dotnet run --project tests/Tack.Tests -c Release -- --update` rewrites
`desktop-export.json` and `desktop-device.json` (what the desktop writes, which the web tests read back).
`web/tests/e2e/interop.spec.ts` (Windows only) runs Tack Focus in a browser against Tack for Windows'
own sync code through a folder on disk; build `tests/Tack.Tests` first.

## Before you open a pull request

- `dotnet build src/Tack -c Release -warnaserror` passes, and so does `dotnet run --project tests/Tack.Tests -c Release`.
- You've tried the change at 100% and at least one other display scale (150% is a good test), and on a
  second monitor if it touches placement.
- You've tried it in both the **Light** and **Dark** themes, with **Frosted glass** on and off.
- If behaviour changed, `README.md` (and `docs/DESIGN.md` if relevant) say so.

## Releasing

Bump `<Version>` in `src/Tack/Tack.csproj`, add the version's section to `CHANGELOG.md` (as
`## 1.2.0 (date)`), merge that pull request, then push a tag such as `v1.2.0` on `main`. The
[release workflow](.github/workflows/release.yml) builds the downloads with `build.ps1` and publishes them
to a GitHub Release whose text is that CHANGELOG section (`tools/release-notes.ps1`). It refuses a tag on
any commit that isn't already on `main`, before building anything, so always tag after the merge.
Until a version is released, its changes collect under `## Unreleased` at the top of `CHANGELOG.md`
(and `web/CHANGELOG.md`); rename that heading to the version when you bump it.

**Tack Focus** has no tags: bump `version` in `web/package.json`, add a section to `web/CHANGELOG.md`,
and merging to `main` deploys it to tackfocus.app ([web/DEPLOY.md](web/DEPLOY.md)).
