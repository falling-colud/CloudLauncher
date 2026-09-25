#requires -Version 5.1
<#
.SYNOPSIS
    Build a CloudLauncher release: the self-update package and the installer, from one publish.

.DESCRIPTION
    Steps:
      1. Resolve the version from <Version> in CloudLauncher.csproj (unless -Version given).
      2. Publish the WPF client once (win-x64, self-contained by default) into
         artifacts\installer-build.
      3. Sign CloudLauncher.exe with Authenticode, when signing is configured (below).
      4. Zip the publish output -> artifacts\CloudLauncher-<version>.zip, the self-update package.
      5. Compile the Inno Setup installer from the same publish ->
         artifacts\CloudLauncher-Setup-<version>.exe, and sign it the same way.

    Nothing is uploaded. tools\release\publish-client.py signs the update package with the release
    key and publishes both files; see tools\release\README.md.

    Authenticode signing is optional. It needs one of:
      CL_SIGN_THUMBPRINT                   a code-signing certificate in the current user's store
      CL_SIGN_PFX + CL_SIGN_PFX_PASSWORD   a .pfx file and its password
    and CL_SIGN_TIMESTAMP_URL picks the timestamp server (default http://timestamp.digicert.com).
    Without them the files are built unsigned.

.EXAMPLE
    ./publish-release.ps1

.EXAMPLE
    ./publish-release.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = "win-x64",
    # Self-contained by default so target machines need no .NET runtime installed.
    [switch]$FrameworkDependent,
    # Accepted so older command lines keep working; this script never uploads.
    [switch]$NoUpload,
    [string]$Iscc
)

$ErrorActionPreference = "Stop"
# Compress-Archive redraws its progress bar for every file, which floods the console and slows the zip down.
$ProgressPreference = "SilentlyContinue"
$root = $PSScriptRoot
$proj = Join-Path $root "CloudLauncher\CloudLauncher.csproj"
$iss  = Join-Path $root "installer\CloudLauncher.iss"
$icon = Join-Path $root "CloudLauncher\Assets\appicon.ico"
if (-not (Test-Path $proj)) { throw "Cannot find project at $proj" }
if (-not (Test-Path $iss))  { throw "Cannot find installer script at $iss" }
. (Join-Path $root "tools\release\release-common.ps1")

$Iscc = Find-Iscc $Iscc

# --- resolve version ---
if (-not $Version) {
    [xml]$xml = Get-Content $proj
    $Version = (@($xml.Project.PropertyGroup.Version) | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { throw "No <Version> found in the csproj; pass -Version explicitly." }
}
Write-Host "Building CloudLauncher release $Version ($Runtime)" -ForegroundColor Cyan

$artifacts = Join-Path $root "artifacts"
$staging   = Join-Path $artifacts "installer-build"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

# --- 1. publish once ---
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$scArg = if ($FrameworkDependent) { "--no-self-contained" } else { "--self-contained" }
dotnet publish $proj -c Release -r $Runtime $scArg -o $staging "/p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
$exe = Join-Path $staging "CloudLauncher.exe"
if (-not (Test-Path $exe)) { throw "Publish output missing $exe." }

# --- 2. sign the exe, then zip -> self-update package ---
Invoke-CodeSign $exe
$zip = Join-Path $artifacts "CloudLauncher-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Write-Host "Update package -> $zip" -ForegroundColor Green

# --- 3. installer, from the same publish ---
$defines = @(
    "/DMyAppVersion=$Version",
    "/DSourceDir=$staging",
    "/DOutputDir=$artifacts",
    "/DAppIcon=$icon"
)
& $Iscc @defines $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)." }
$setup = Join-Path $artifacts "CloudLauncher-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Installer was not produced at $setup." }
Invoke-CodeSign $setup
Write-Host "Installer -> $setup" -ForegroundColor Green

Write-Host ""
Write-Host "Built release $Version. To publish it:" -ForegroundColor Cyan
Write-Host "  python tools\release\publish-client.py $Version `"$zip`" `"$setup`" <notes.txt>" -ForegroundColor Gray
