<#
.SYNOPSIS
    Prints the CHANGELOG.md section for a version (e.g. 1.2.0 or v1.2.0), without its heading.
    Used by .github/workflows/release.yml for the GitHub Release text. Exits 1 if there is none.
#>
param([Parameter(Mandatory)][string]$Version)

$Version = $Version.TrimStart("v")
$lines = Get-Content (Join-Path $PSScriptRoot "..\CHANGELOG.md") -Encoding UTF8
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^## $([regex]::Escape($Version))(\s|$)") { $start = $i + 1; break }
}
# Not Write-Error: GitHub runs pwsh with $ErrorActionPreference = 'Stop', which would end the step
# instead of letting the workflow fall back to GitHub's generated notes.
if ($start -lt 0) { [Console]::Error.WriteLine("No CHANGELOG.md section for $Version"); exit 1 }
$end = $lines.Count
for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^## ") { $end = $i; break }
}
($lines[$start..($end - 1)] -join "`n").Trim()
exit 0
