#requires -Version 5.1
<#
.SYNOPSIS
    Ship a CloudLauncher release: build the update package AND the installer from one
    publish, then upload both to the server so the website download and the in-app
    self-updater always serve the same version.

.DESCRIPTION
    Steps:
      1. Resolve the version from <Version> in CloudLauncher.csproj (unless -Version given).
      2. Publish the WPF client once (win-x64, self-contained by default).
      3. Zip the publish output -> the self-update package.
      4. Compile the Inno Setup installer from the same publish -> setup .exe.
      5. Log in as an admin and POST both files to /launcher/upload.

    After this runs:
      - The website's "Download" button (/download) serves the new installer.
      - Existing launchers see the new version via /launcher/latest and self-update.

    This supersedes publish-update.ps1 (which uploads only the self-update zip). Use this
    one for normal releases so first-time downloaders and existing users stay in sync.

.EXAMPLE
    ./publish-release.ps1 -Username colud -Password hunter2

.EXAMPLE
    ./publish-release.ps1 -Username colud -Password hunter2 -Version 1.2.0 -Notes "Adds team sync"
#>
[CmdletBinding()]
param(
    [string]$ServerUrl = "http://130.61.131.193:5000",
    # Required to upload; optional for a -NoUpload dry run (build only).
    [string]$Username,
    [string]$Password,
    [string]$Version,
    [string]$Notes = "",
    [string]$Runtime = "win-x64",
    # Self-contained by default so target machines need no .NET runtime installed.
    [switch]$FrameworkDependent,
    # Build the package + installer but skip the upload (dry run).
    [switch]$NoUpload,
    [string]$Iscc
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "CloudLauncher\CloudLauncher.csproj"
$iss  = Join-Path $root "installer\CloudLauncher.iss"
$icon = Join-Path $root "CloudLauncher\Assets\appicon.ico"
if (-not (Test-Path $proj)) { throw "Cannot find project at $proj" }
if (-not (Test-Path $iss))  { throw "Cannot find installer script at $iss" }

# --- locate Inno Setup ---
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

# --- resolve version ---
if (-not $Version) {
    [xml]$xml = Get-Content $proj
    $Version = (@($xml.Project.PropertyGroup.Version) | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { throw "No <Version> found in the csproj; pass -Version explicitly." }
}
Write-Host "Releasing CloudLauncher $Version ($Runtime)" -ForegroundColor Cyan

$artifacts = Join-Path $root "artifacts"
$staging   = Join-Path $artifacts "installer-build"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

# --- 1. publish once ---
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$scArg = if ($FrameworkDependent) { "--no-self-contained" } else { "--self-contained" }
dotnet publish $proj -c Release -r $Runtime $scArg -o $staging "/p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# --- 2. zip -> self-update package ---
$zip = Join-Path $artifacts "CloudLauncher-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Write-Host "Update package -> $zip" -ForegroundColor Green

# --- 3. installer ---
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
Write-Host "Installer -> $setup" -ForegroundColor Green

if ($NoUpload) {
    Write-Host "`n-NoUpload set: built package + installer, skipping upload." -ForegroundColor Yellow
    return
}
if (-not $Username -or -not $Password) {
    throw "Uploading requires -Username and -Password (or pass -NoUpload to just build)."
}

# --- 4. authenticate ---
$ServerUrl = $ServerUrl.TrimEnd('/')
$loginBody = @{ username = $Username; password = $Password } | ConvertTo-Json
$tokens = Invoke-RestMethod -Uri "$ServerUrl/auth/login" -Method Post -Body $loginBody -ContentType "application/json"
$token = $tokens.accessToken
if (-not $token) { throw "Login succeeded but no access token was returned." }

# --- 5. upload package + installer (streamed multipart) ---
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMinutes(30)
$client.DefaultRequestHeaders.Authorization =
    [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $token)

$content = [System.Net.Http.MultipartFormDataContent]::new()
$zipFs = [System.IO.File]::OpenRead($zip)
$exeFs = [System.IO.File]::OpenRead($setup)
try {
    $zipContent = [System.Net.Http.StreamContent]::new($zipFs)
    $zipContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/octet-stream")
    $content.Add($zipContent, "file", [System.IO.Path]::GetFileName($zip))

    $exeContent = [System.Net.Http.StreamContent]::new($exeFs)
    $exeContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/octet-stream")
    $content.Add($exeContent, "installer", [System.IO.Path]::GetFileName($setup))

    $content.Add([System.Net.Http.StringContent]::new($Version), "version")
    if ($Notes) { $content.Add([System.Net.Http.StringContent]::new($Notes), "notes") }

    Write-Host "Uploading package + installer to $ServerUrl/launcher/upload ..." -ForegroundColor Cyan
    $resp = $client.PostAsync("$ServerUrl/launcher/upload", $content).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $resp.IsSuccessStatusCode) { throw "Upload failed ($([int]$resp.StatusCode)): $body" }

    Write-Host "`nRelease $Version published." -ForegroundColor Green
    Write-Host "  Website download: $ServerUrl/download" -ForegroundColor Gray
    Write-Host "  Self-update feed: $ServerUrl/launcher/latest" -ForegroundColor Gray
    Write-Host $body
}
finally {
    $zipFs.Dispose(); $exeFs.Dispose()
    $content.Dispose(); $client.Dispose()
}
