<#
.SYNOPSIS
    Builds Tack's release downloads into ./artifacts.

.DESCRIPTION
    Produces:
      Tack-<version>-<runtime>.zip         One self-contained Tack.exe. Nothing to install.
      Tack-<version>-<runtime>-net8.zip    A tiny build for PCs that already have the
                                           .NET 8 Desktop Runtime.
      Tack-<version>-setup.exe             An installer for that same self-contained Tack.exe
                                           (Tack-<version>-arm64-setup.exe for win-arm64). Needs
                                           Inno Setup 6 (installer/Tack.iss); skipped with a warning
                                           when it isn't installed, unless -RequireInstaller.
    plus SHA256SUMS.txt.

.EXAMPLE
    ./build.ps1 -Version 1.2.0
    ./build.ps1 -Version 1.2.0 -Runtime win-arm64
    ./build.ps1 -Version 1.2.0 -InstallerTest mine   # an installer that can't touch an installed Tack
#>
param(
    [string]$Version = "1.0.0",
    [ValidateSet("win-x64", "win-arm64")][string]$Runtime = "win-x64",
    # Fail instead of skipping the installer when Inno Setup's compiler can't be found (release.yml).
    [switch]$RequireInstaller,
    # Path to ISCC.exe; otherwise $env:ISCC, PATH, then Inno Setup 6's usual install folders.
    [string]$Iscc,
    # Build a test installer instead (its own AppId and uninstall entry: see installer/Tack.iss).
    [ValidatePattern('^[A-Za-z0-9-]{1,32}$')][string]$InstallerTest
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

# The installer wraps the same self-contained Tack.exe as the portable zip.
function Find-Iscc {
    foreach ($candidate in @(
            $Iscc,
            $env:ISCC,
            (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
            (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
            (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"))) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    return $null
}

$compiler = Find-Iscc
if ($compiler) {
    Write-Host "Building installer with $compiler..."
    $isccArgs = @(
        "/Q",
        "/DVersion=$Version",
        "/DRuntime=$Runtime",
        "/DSourceExe=$(Join-Path $portableDir 'Tack.exe')",
        "/DOutputDir=$out"
    )
    if ($InstallerTest) { $isccArgs += "/DTestBuild=$InstallerTest" }
    & $compiler @isccArgs (Join-Path $root "installer/Tack.iss")
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
} elseif ($RequireInstaller) {
    throw "Inno Setup 6's ISCC.exe wasn't found. Install Inno Setup 6, or pass -Iscc <path to ISCC.exe>."
} else {
    Write-Warning "Inno Setup 6 isn't installed, so there's no installer this time (see installer/Tack.iss)."
}

$sums = Get-ChildItem $out -File |
    Where-Object { $_.Name -like "Tack-$Version-*.zip" -or $_.Name -like "Tack-$Version-*setup*.exe" } |
    Sort-Object Name | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
$sums | Set-Content (Join-Path $out "SHA256SUMS.txt") -Encoding ascii

Get-ChildItem $out -File | ForEach-Object { "{0,-36} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
