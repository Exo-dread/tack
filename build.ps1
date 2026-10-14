<#
.SYNOPSIS
    Builds Tack's release downloads into ./artifacts.

.DESCRIPTION
    Produces two zips:
      Tack-<version>-<runtime>.zip         One self-contained Tack.exe. Nothing to install.
      Tack-<version>-<runtime>-net8.zip    A tiny build for PCs that already have the
                                           .NET 8 Desktop Runtime.
    plus SHA256SUMS.txt.

.EXAMPLE
    ./build.ps1 -Version 1.2.0
    ./build.ps1 -Version 1.2.0 -Runtime win-arm64
#>
param(
    [string]$Version = "1.0.0",
    [ValidateSet("win-x64", "win-arm64")][string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$Version = $Version.TrimStart("v")
$root = $PSScriptRoot
$project = Join-Path $root "src/Tack/Tack.csproj"
$out = Join-Path $root "artifacts"
$portableDir = Join-Path $out "portable-$Runtime"
$fxDir = Join-Path $out "net8-$Runtime"

foreach ($dir in $portableDir, $fxDir) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "Publishing portable build ($Runtime)..."
dotnet publish $project -c Release -r $Runtime -p:Portable=true -p:Version=$Version -o $portableDir --nologo
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed." }

Write-Host "Publishing framework-dependent build ($Runtime)..."
dotnet publish $project -c Release -r $Runtime --self-contained false -p:Version=$Version -o $fxDir --nologo
if ($LASTEXITCODE -ne 0) { throw "Framework-dependent publish failed." }

$portableZip = Join-Path $out "Tack-$Version-$Runtime.zip"
$fxZip = Join-Path $out "Tack-$Version-$Runtime-net8.zip"
Compress-Archive -Path (Join-Path $portableDir "Tack.exe") -DestinationPath $portableZip -CompressionLevel Optimal -Force
Compress-Archive -Path (Join-Path $fxDir "*") -DestinationPath $fxZip -CompressionLevel Optimal -Force

$sums = Get-ChildItem $out -Filter "Tack-$Version-*.zip" | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$sums | Set-Content (Join-Path $out "SHA256SUMS.txt") -Encoding ascii

Get-ChildItem $out -File | ForEach-Object { "{0,-36} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
