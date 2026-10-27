# Tack in winget

Getting Tack for Windows into the Windows Package Manager, so `winget install Exo-dread.Tack` (or
`winget install tack`) installs it and `winget upgrade` keeps it current. **Prepared, not submitted
yet:** nothing here has gone to microsoft/winget-pkgs, and README.md's download instructions stay as
they are until the package is live.

## The package

| | |
|---|---|
| Identifier | `Exo-dread.Tack` (moniker `tack`) |
| Installer | The self-contained `Tack-x.y.z-win-x64.zip` from the GitHub release: winget's `zip` type with one `portable` file inside, `Tack.exe` |
| Command | `tack` (winget puts the alias on PATH) |
| Hash | From the release's `SHA256SUMS.txt` |
| Licence | MIT |

winget unpacks the exe into `%LOCALAPPDATA%\Microsoft\WinGet\Packages\Exo-dread.Tack_…\` and links
`tack` from `%LOCALAPPDATA%\Microsoft\WinGet\Links`. That folder has no `TackData`, so a winget copy
keeps its data in `%LOCALAPPDATA%\Tack` like any other: an upgrade or uninstall never touches it.

### Or the installer

Since the release after 1.7.0 there's also `Tack-x.y.z-setup.exe` (Inno Setup, `installer/Tack.iss`),
and winget can use that instead of, or beside, the zip. In the installer manifest that's an entry
with `InstallerType: inno` (winget then knows `/VERYSILENT`, `/LOG` and `/DIR=` by itself), its
`InstallerUrl` and the hash from `SHA256SUMS.txt`, and two entries if both scopes should be offered:
`Scope: machine` with `InstallerSwitches: { Custom: /ALLUSERS }` and `Scope: user` with
`{ Custom: /CURRENTUSER }`. `ProductCode` is `{125AD659-8D26-4921-B390-2D4E48F99750}_is1`, which
is how winget finds it in Apps & features for `upgrade` and `uninstall`; `ElevationRequirement` is
`elevatesSelf` for the machine scope. With the installer there's no `tack` command on PATH, but
`winget upgrade` closes a running Tack itself (and starts it again), and uninstalling removes *Start
with Windows* when it pointed at the installed Tack: the two notes at the end of this page are for the
zip only. `tools/winget-manifest.ps1` still writes the zip manifest; add the installer entries by hand
for now, then `winget validate` as below.

## Writing the manifests

For a version that's already released (tagged on main, with its GitHub Release published):

```powershell
gh release download v1.6.1 --repo Exo-dread/tack --pattern SHA256SUMS.txt --dir artifacts/release
./tools/winget-manifest.ps1 -Version 1.6.1 -Sums artifacts/release/SHA256SUMS.txt
winget validate --manifest artifacts/winget/manifests/e/Exo-dread/Tack/1.6.1
```

(Or download `SHA256SUMS.txt` from the release page in a browser.) The script writes the three
manifests (version, installer, en-US locale) in winget-pkgs' folder layout under `artifacts/winget/`,
taking the release date from the version's `CHANGELOG.md` heading. It goes online for nothing.
Always use the release's `SHA256SUMS.txt`, never one from a local `./build.ps1`: zips aren't
reproducible byte for byte, so a rebuild hashes differently and winget would refuse the download.
Pass `-Zip` with the downloaded zip to check it against the sums too. `-ManifestVersion` picks the
schema (1.10.0 by default; winget 1.29 validates 1.28.0 as well).

## Before the first submission

- Check the identifier and moniker are free: `winget search --id Exo-dread.Tack` and
  `winget search --moniker tack`. If `tack` is taken, drop `Moniker` from the script.
- Try the install in **a spare VM (or Windows Sandbox with winget added), not on the PC you use
  Tack on**. A copy started there shares `%LOCALAPPDATA%\Tack` with your own Tack, and if *Start with
  Windows* is on it points the startup entry at itself. There: `winget settings --enable LocalManifestFiles` (as
  administrator), then `winget install --manifest <folder>`, run `tack`, turn on *Start with Windows*
  and check the startup entry names a path that an upgrade keeps (the package folder, not a temp
  copy), then `winget uninstall Exo-dread.Tack`.

## Submitting

Either way it becomes a pull request to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs),
which their pipeline validates (schema, download, hash, a malware scan) and a moderator merges; it
reaches `winget` a few hours after. Tack isn't code-signed, which is allowed, though the scan may ask
for a manual review.

- **wingetcreate** ([microsoft/winget-create](https://github.com/microsoft/winget-create)):
  `wingetcreate submit --token <GitHub token> artifacts/winget/manifests/e/Exo-dread/Tack/1.6.1`
  forks winget-pkgs under your account and opens the pull request. The token needs `public_repo`;
  keep it out of the repo and out of any script.
- **By hand:** fork microsoft/winget-pkgs, copy the version folder to the same path,
  `manifests/e/Exo-dread/Tack/1.6.1/`, commit, and open a pull request, filling in its checklist.

## Each release after that

Once the release workflow has published `vX.Y.Z`, write that version's manifests as above and submit
them the same way (a new folder beside the old ones), or let wingetcreate do it from the last version
in winget: `wingetcreate update Exo-dread.Tack --version X.Y.Z --urls https://github.com/Exo-dread/tack/releases/download/vX.Y.Z/Tack-X.Y.Z-win-x64.zip --submit --token <token>`
(it downloads the zip and hashes it itself). Doing this from `release.yml` would need a token as a
secret and a change to the release job's permissions, so it's for a discussion first (CLAUDE.md).

For people using the winget copy: quit Tack (right-click the orb → Quit) before `winget upgrade`, as
Windows won't replace a running exe; and turn off *Start with Windows* before uninstalling, or its
startup entry is left pointing at nothing.
