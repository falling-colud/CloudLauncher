#requires -Version 5.1
<#
.SYNOPSIS
    Publish CloudLauncher and build a Windows installer (setup .exe) with Inno Setup.

.DESCRIPTION
    Publishes the WPF client as a self-contained win-x64 build, then compiles
    installer\CloudLauncher.iss into artifacts\CloudLauncher-Setup-<version>.exe.

    The installer performs a per-user install to %LocalAppData%\Programs\CloudLauncher
    (no admin prompt), which is required for the in-app self-updater to work: it
    overwrites the install directory without elevating.

    The version is read from <Version> in CloudLauncher.csproj unless -Version is passed,
    so the usual flow is: bump <Version> in the csproj, then run this script.

.EXAMPLE
    ./build-installer.ps1

.EXAMPLE
    ./build-installer.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = "win-x64",
    # Self-contained by default so target machines need no .NET runtime installed.
    [switch]$FrameworkDependent,
    # Reuse an existing publish in the staging folder instead of republishing.
    [switch]$NoPublish,
    # Explicit path to ISCC.exe if it isn't in the default install location / PATH.
    [string]$Iscc
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "CloudLauncher\CloudLauncher.csproj"
$iss  = Join-Path $root "installer\CloudLauncher.iss"
$icon = Join-Path $root "CloudLauncher\Assets\appicon.ico"
if (-not (Test-Path $proj)) { throw "Cannot find project at $proj" }
if (-not (Test-Path $iss))  { throw "Cannot find installer script at $iss" }

# 1. Locate the Inno Setup compiler.
if (-not $Iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $Iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $Iscc) {
        $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($onPath) { $Iscc = $onPath.Source }
    }
}
if (-not $Iscc -or -not (Test-Path $Iscc)) {
    throw "ISCC.exe (Inno Setup 6) not found. Install it from https://jrsoftware.org/isdl.php or pass -Iscc <path>."
}

# 2. Resolve the version (from the csproj unless overridden).
if (-not $Version) {
    [xml]$xml = Get-Content $proj
    $Version = (@($xml.Project.PropertyGroup.Version) | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { throw "No <Version> found in the csproj; pass -Version explicitly." }
}
Write-Host "Building CloudLauncher installer $Version ($Runtime)..." -ForegroundColor Cyan

$artifacts = Join-Path $root "artifacts"
$staging   = Join-Path $artifacts "installer-build"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

# 3. Publish the client into the staging folder.
if (-not $NoPublish) {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    $scArg = if ($FrameworkDependent) { "--no-self-contained" } else { "--self-contained" }
    dotnet publish $proj -c Release -r $Runtime $scArg -o $staging "/p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
}
$exe = Join-Path $staging "CloudLauncher.exe"
if (-not (Test-Path $exe)) { throw "Publish output missing $exe (run without -NoPublish)." }

# 4. Compile the installer. /D defines override the #ifndef defaults in the .iss.
$defines = @(
    "/DMyAppVersion=$Version",
    "/DSourceDir=$staging",
    "/DOutputDir=$artifacts",
    "/DAppIcon=$icon"
)
Write-Host "Compiling installer with Inno Setup..." -ForegroundColor Cyan
& $Iscc @defines $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)." }

$setup = Join-Path $artifacts "CloudLauncher-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Installer was not produced at $setup." }
$sizeMb = [Math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "Installer built: $setup ($sizeMb MB)" -ForegroundColor Green
Write-Host "Per-user install target: %LocalAppData%\Programs\CloudLauncher (no admin required)." -ForegroundColor DarkGray
