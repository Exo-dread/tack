<#
.SYNOPSIS
    Writes the winget manifests for a released version of Tack, ready to submit to
    microsoft/winget-pkgs. It submits nothing and goes online for nothing.

.DESCRIPTION
    Writes three files in winget-pkgs' layout, under <OutDir>/manifests/e/Exo-dread/Tack/<version>/:

      Exo-dread.Tack.yaml               the version manifest
      Exo-dread.Tack.installer.yaml     the installer: the self-contained Tack-<version>-win-x64.zip
                                        from the GitHub release, a zip holding one portable Tack.exe,
                                        which winget installs with the command alias "tack"
      Exo-dread.Tack.locale.en-US.yaml  the default locale: name, publisher, licence, descriptions

    The zip's SHA256 comes from the SHA256SUMS.txt attached to that GitHub release. Use that file,
    not one from a local ./build.ps1: zips aren't byte-for-byte reproducible, so a rebuild of the
    same version hashes differently and winget would refuse the download. The release date comes
    from the version's CHANGELOG.md heading. See docs/WINGET.md for submitting and updating.

.PARAMETER Version
    The released version, e.g. 1.6.1 (or v1.6.1).

.PARAMETER Sums
    The SHA256SUMS.txt downloaded from that version's GitHub release.

.PARAMETER Zip
    Optional: the Tack-<version>-win-x64.zip downloaded from the release, checked against Sums.

.PARAMETER OutDir
    Where to write (default ./artifacts/winget, which git ignores).

.PARAMETER ManifestVersion
    The winget manifest schema version to write (default 1.10.0).

.EXAMPLE
    gh release download v1.6.1 --repo Exo-dread/tack --pattern SHA256SUMS.txt --dir artifacts/release
    ./tools/winget-manifest.ps1 -Version 1.6.1 -Sums artifacts/release/SHA256SUMS.txt
    winget validate --manifest artifacts/winget/manifests/e/Exo-dread/Tack/1.6.1
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Sums,
    [string]$Zip,
    [string]$OutDir = (Join-Path $PSScriptRoot "..\artifacts\winget"),
    [string]$ManifestVersion = "1.10.0"
)

$ErrorActionPreference = "Stop"

$Identifier = "Exo-dread.Tack"
$Repo = "https://github.com/Exo-dread/tack"

$Version = $Version.Trim().TrimStart("v")
if ($Version -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}$') { throw "Version must look like 1.6.1, not '$Version'." }
if ($ManifestVersion -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}$') { throw "ManifestVersion must look like 1.10.0, not '$ManifestVersion'." }

$zipName = "Tack-$Version-win-x64.zip"
$tag = "v$Version"

# ---- the zip's SHA256, from the release's SHA256SUMS.txt ("<sha256>  <file name>" per line)
if (-not (Test-Path -LiteralPath $Sums -PathType Leaf)) { throw "No such file: $Sums" }
if ((Get-Item -LiteralPath $Sums).Length -gt 64KB) { throw "$Sums is too big to be a SHA256SUMS.txt." }
$found = @(
    Get-Content -LiteralPath $Sums -Encoding UTF8 | ForEach-Object {
        if ($_ -match '^([0-9A-Fa-f]{64}) [ *](.+?)\s*$' -and $Matches[2] -ceq $zipName) { $Matches[1] }
    }
)
if ($found.Count -eq 0) { throw "$Sums has no line for $zipName. Is it the SHA256SUMS.txt of release $tag?" }
if (@($found | Sort-Object -Unique).Count -ne 1) { throw "$Sums gives $zipName more than one hash." }
$sha256 = $found[0].ToUpperInvariant()

if ($Zip) {
    if ((Split-Path -Leaf $Zip) -cne $zipName) { throw "Expected the release's $zipName, not $(Split-Path -Leaf $Zip)." }
    $actual = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $sha256) { throw "$Zip hashes to $actual, but $Sums says $sha256." }
}

# ---- the release date, from CHANGELOG.md ("## 1.6.1 (2026-10-24)")
$releaseDate = $null
$changelog = Join-Path $PSScriptRoot "..\CHANGELOG.md"
foreach ($line in Get-Content -LiteralPath $changelog -Encoding UTF8) {
    if ($line -match "^## $([regex]::Escape($Version)) \((\d{4}-\d{2}-\d{2})\)\s*$") {
        $date = [datetime]::MinValue
        if ([datetime]::TryParseExact($Matches[1], "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$date)) {
            $releaseDate = $date.ToString("yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)
        }
        break
    }
}
if (-not $releaseDate) { Write-Warning "CHANGELOG.md has no '## $Version (yyyy-MM-dd)' heading: ReleaseDate is left out." }

# ---- YAML: a value is written plain only when it can't be read as anything but that string
function Format-Scalar([string]$Value) {
    $plain = $Value -match '^[A-Za-z0-9][A-Za-z0-9 ._/+(),:=?&%~-]*$' -and
        $Value -notmatch ': |:$| $' -and
        $Value -notmatch '^(?i:[0-9._+-]+|true|false|yes|no|on|off|y|n|null)$'
    if ($plain) { return $Value }
    return "'" + $Value.Replace("'", "''") + "'"
}

function Format-Manifest([string]$Type, [System.Collections.Specialized.OrderedDictionary]$Fields) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Created with tools/winget-manifest.ps1 (github.com/Exo-dread/tack)")
    $lines.Add("# yaml-language-server: `$schema=https://aka.ms/winget-manifest.$Type.$ManifestVersion.schema.json")
    $lines.Add("")
    foreach ($key in $Fields.Keys) {
        $value = $Fields[$key]
        if ($null -eq $value) { continue }
        if ($value -is [string]) {
            $lines.Add("${key}: $(Format-Scalar $value)")
        } elseif ($value -is [array] -and ($value | Where-Object { $_ -is [string] }).Count -eq $value.Count) {
            $lines.Add("${key}:")
            foreach ($item in $value) { $lines.Add("- $(Format-Scalar $item)") }
        } else {
            # A list of mappings.
            $lines.Add("${key}:")
            foreach ($map in $value) {
                $first = $true
                foreach ($k in $map.Keys) {
                    $lines.Add("$(if ($first) { '- ' } else { '  ' })${k}: $(Format-Scalar $map[$k])")
                    $first = $false
                }
            }
        }
    }
    $lines.Add("ManifestType: $Type")
    $lines.Add("ManifestVersion: $ManifestVersion")
    return ($lines -join "`n") + "`n"
}

$versionFields = [ordered]@{
    PackageIdentifier = $Identifier
    PackageVersion    = $Version
    DefaultLocale     = "en-US"
}

$installerFields = [ordered]@{
    PackageIdentifier    = $Identifier
    PackageVersion       = $Version
    MinimumOSVersion     = "10.0.17763.0"
    InstallerType        = "zip"
    NestedInstallerType  = "portable"
    NestedInstallerFiles = @([ordered]@{ RelativeFilePath = "Tack.exe"; PortableCommandAlias = "tack" })
    ReleaseDate          = $releaseDate
    Installers           = @([ordered]@{
        Architecture    = "x64"
        InstallerUrl    = "$Repo/releases/download/$tag/$zipName"
        InstallerSha256 = $sha256
    })
}

$localeFields = [ordered]@{
    PackageIdentifier   = $Identifier
    PackageVersion      = $Version
    PackageLocale       = "en-US"
    Publisher           = "Exo-dread"
    PublisherUrl        = "https://github.com/Exo-dread"
    PublisherSupportUrl = "$Repo/issues"
    PrivacyUrl          = "https://tackfocus.app/privacy/"
    Author              = "Tack contributors"
    PackageName         = "Tack"
    PackageUrl          = $Repo
    License             = "MIT"
    LicenseUrl          = "$Repo/blob/main/LICENSE"
    Copyright           = "Copyright (c) 2026 Tack contributors"
    ShortDescription    = "A tiny, always-on-top home for what you're doing right now: tasks and quick notes in a panel that folds into an orb."
    Description         = "Tack keeps your tasks and quick notes in a frosted-glass panel that folds into a small orb, always on top of your other windows. Open it with a click or Ctrl+Alt+Space, type a task, press Enter, then Esc, and you're back at work. One task can be in focus, spaces keep work and personal tasks apart, and Tack Focus (tackfocus.app) puts the same tasks on your phone, kept in step through your own OneDrive folder. No account, no telemetry, and Tack itself never connects to anything."
    Moniker             = "tack"
    Tags                = @("tasks", "todo", "notes", "focus", "productivity", "always-on-top", "portable")
    ReleaseNotesUrl     = "$Repo/releases/tag/$tag"
}

# Within the schema's limits (manifest.defaultLocale.1.10.0.json), so a mistake shows here, not in review.
if ($localeFields.ShortDescription.Length -gt 256) { throw "ShortDescription is over 256 characters." }
if ($localeFields.Description.Length -gt 10000) { throw "Description is over 10,000 characters." }
if ($localeFields.Tags.Count -gt 16 -or ($localeFields.Tags | Where-Object { $_.Length -gt 40 })) { throw "At most 16 tags of up to 40 characters." }

$dir = Join-Path $OutDir "manifests\e\Exo-dread\Tack\$Version"
New-Item -ItemType Directory -Force $dir | Out-Null
# UTF-8 without a byte order mark, LF line ends: what winget-pkgs holds.
$utf8 = New-Object System.Text.UTF8Encoding($false)
$files = [ordered]@{
    "$Identifier.yaml"                 = Format-Manifest "version" $versionFields
    "$Identifier.installer.yaml"       = Format-Manifest "installer" $installerFields
    "$Identifier.locale.en-US.yaml"    = Format-Manifest "defaultLocale" $localeFields
}
foreach ($name in $files.Keys) {
    $path = Join-Path $dir $name
    [System.IO.File]::WriteAllText($path, $files[$name], $utf8)
    Write-Host "Wrote $path"
}
Write-Host ""
Write-Host "Check them with:  winget validate --manifest `"$dir`""
Write-Host "Try them with:    winget install --manifest `"$dir`"   (needs LocalManifestFiles enabled; see docs/WINGET.md)"
