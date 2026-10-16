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

**Keep your real data out of the way while developing:** create an empty `TackData` folder next to the
built exe (for example `src/Tack/bin/Debug/net8.0-windows/TackData`). Tack then stores everything there
(portable mode) instead of in `%LOCALAPPDATA%\Tack`. Delete the folder's contents to see the first-run
experience again.

Only one Tack runs per user. Launching it again just opens the running one, so quit your installed copy
(right-click the orb → Quit) before debugging.

## Where things live

| Path | What |
|---|---|
| `src/Tack/AppShell.cs` | Coordinates the orb and panel: expand/collapse, placement, shortcut, full-screen handling |
| `src/Tack/Model/` | `Workspace` (all user state and every mutation), `TaskItem`, `NoteSheet`, `Settings` |
| `src/Tack/Services/` | `StateStore` (JSON persistence), accent colour, startup registration, log, single instance |
| `src/Tack/Interop/` | Win32 and DWM: glass, monitors/DPI, hotkey, shell watcher. All P/Invoke is in `NativeMethods.cs` |
| `src/Tack/Windows/` | The three windows: orb, peek bubble, panel |
| `src/Tack/Views/` | Tasks, Notes and Settings pages shown inside the panel |
| `src/Tack/Controls/` | Custom-drawn orb, animation helpers, converters |
| `src/Tack/Themes/` | `Theme.xaml` (fonts and every control style), `Dark.xaml` / `Light.xaml` (colour palettes) |
| `tools/make_icon.py` | Regenerates `Assets/Tack.ico` and `docs/icon.png` (needs Pillow) |
| `build.ps1` | Builds the release zips into `artifacts/` |

## House rules

- **No new dependencies** without discussion. Tack references nothing beyond .NET and WPF.
- **No network access**, no telemetry, no accounts. Ever.
- **Nothing runs while idle.** No polling timers or looping animations; react to events instead.
- **Animations are short** (≤ 260 ms), eased, and never block input.
- **Place windows in physical pixels** via `ScreenGeometry`, not WPF's `Left`/`Top`.
- **Colours come from the palettes.** Reference brushes with `DynamicResource` (never `StaticResource`
  or a hex literal), and add any new key to both `Themes/Dark.xaml` and `Themes/Light.xaml`.
- **Every user-visible mutation goes through `Workspace`**, so saving and the orb stay in sync.
- **Destructive actions get Undo**, not confirmation dialogs.
- **Icon-only buttons need `AutomationProperties.Name`** so screen readers can announce them.
- Match the surrounding code: file-scoped namespaces, `_camelCase` fields, small focused classes.

## Before you open a pull request

- `dotnet build src/Tack -c Release -warnaserror` passes.
- You've tried the change at 100% and at least one other display scale (150% is a good test), and on a
  second monitor if it touches placement.
- You've tried it in both the **Light** and **Dark** themes, with **Frosted glass** on and off.
- If behaviour changed, `README.md` (and `docs/DESIGN.md` if relevant) say so.

## Releasing

Update `CHANGELOG.md`, then push a tag such as `v1.1.0`. The
[release workflow](.github/workflows/release.yml) builds the downloads with `build.ps1` and publishes them
to a GitHub Release.
