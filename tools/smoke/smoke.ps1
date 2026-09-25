#requires -Version 5.1
<#
.SYNOPSIS
    Starts the built launcher once on a hidden desktop and opens every page: a quick check to run
    before publishing a release.

.DESCRIPTION
    Runs artifacts\installer-build\CloudLauncher.exe (the output of publish-release.ps1) on a
    separate Windows desktop, so no window, taskbar button or focus change reaches the visible one.
    It talks to the fake server in tools\smoke\showcase ("serve" mode on 127.0.0.1:5088: a fictional
    account, mara, with six instances and hosted content). The probe in tools\smoke\desk opens every
    sidebar page by focusing its button through UI Automation and posting Space to the launcher
    window (no SendInput, no real cursor), then closes the launcher with WM_CLOSE.

    Reading the result:
      - each "page now:" line should name the page that was pressed (Settings opens in the side
        panel, so the main page stays where it was for that one);
      - "EXTRA WINDOW class=#32770" is the unhandled-exception message box: a failure;
      - "LAUNCHER EXITED", or a launcher exit code other than 0, is a failure.
    Per-page UI Automation dumps land in the output folder. Screenshots come out black (nothing
    composes a hidden desktop). Tab contents are not in the automation tree, because the app's
    TabControl template has no PART_SelectedContentHost.

    Uses a throwaway CL_PROFILE, "showcase" (deleted afterwards), a dead CurseForge base URL and a
    WebView2 folder that does not exist, so it never touches the real profile, the real server or a
    store.

.EXAMPLE
    ./publish-release.ps1 -Version 1.8.4
    ./tools/smoke/smoke.ps1
#>
[CmdletBinding()]
param(
    # Defaults to artifacts\installer-build\CloudLauncher.exe.
    [string]$Exe,
    # Defaults to %TEMP%\cloudlauncher-smoke.
    [string]$Work
)

$ErrorActionPreference = "Stop"
# Defaults are set here, not in param(): Windows PowerShell 5.1 leaves $PSScriptRoot empty there.
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot "..\..\artifacts\installer-build\CloudLauncher.exe" }
if (-not $Work) { $Work = Join-Path $env:TEMP "cloudlauncher-smoke" }
$Exe = (Resolve-Path $Exe).Path
$build = Join-Path $Work "build"
$out = Join-Path $Work "out"
$serve = Join-Path $Work "serve"

dotnet build (Join-Path $PSScriptRoot "showcase\showcase.csproj") --artifacts-path $build -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "The showcase (fake server) build failed." }
dotnet build (Join-Path $PSScriptRoot "desk\desk.csproj") -c Release --artifacts-path $build -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "The desk (probe) build failed." }

foreach ($d in $out, $serve) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Force $d | Out-Null
}

$server = Start-Process (Join-Path $build "bin\showcase\debug\ClShowcase.exe") -ArgumentList "`"$serve`"", "serve" -PassThru
try {
    $up = $false
    for ($i = 0; $i -lt 60 -and -not $up; $i++) {
        try { Invoke-WebRequest "http://127.0.0.1:5088/auth/me" -UseBasicParsing -TimeoutSec 2 | Out-Null; $up = $true }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "The fake server did not come up; its log is in $serve." }

    & (Join-Path $build "bin\desk\release\ClDesk.exe") run $Exe $out
}
finally {
    New-Item -ItemType File -Force (Join-Path $serve "stop") | Out-Null
    if (-not $server.WaitForExit(15000)) { $server.Kill() }

    # The throwaway profile, and the IE feature-control values App's static constructor writes for
    # whichever exe touches it (ClShowcase.exe here; CloudLauncher.exe's own values are left alone).
    $profileDir = Join-Path $env:APPDATA "CloudLauncher\showcase"
    if (Test-Path $profileDir) { Remove-Item $profileDir -Recurse -Force }
    foreach ($f in "FEATURE_BROWSER_EMULATION", "FEATURE_96DPI_PIXEL") {
        Remove-ItemProperty "HKCU:\Software\Microsoft\Internet Explorer\Main\FeatureControl\$f" -Name "ClShowcase.exe" -ErrorAction SilentlyContinue
    }
}

Get-Content (Join-Path $out "run.txt") -Encoding UTF8
Get-Content (Join-Path $out "probe.txt") -Encoding UTF8 | Where-Object { $_ -notmatch "EXTRA WINDOW class=(UAC|Popup)" }

$bad = @(Select-String -Path (Join-Path $out "probe.txt") -Pattern "#32770|LAUNCHER EXITED|NO MAIN WINDOW|PROBE FAILED")
$clean = Select-String -Path (Join-Path $out "run.txt") -Pattern "exited by itself, code 0 "
if ($bad.Count -gt 0 -or -not $clean) {
    Write-Host "`nSMOKE FAILED. UI Automation dumps: $out" -ForegroundColor Red
    exit 1
}
Write-Host "`nSmoke passed. UI Automation dumps: $out" -ForegroundColor Green
