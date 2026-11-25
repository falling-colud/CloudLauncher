<#
.SYNOPSIS
    Renders the Linux launcher's pages to PNG files, without a window, against the showcase fake server.

.DESCRIPTION
    Builds tools\smoke\showcase (the fake server, 127.0.0.1:5088, fictional account "mara") and this
    harness, starts the server in "serve" mode (it seeds the CL_PROFILE=showcase profile), runs the
    harness for the given scenarios, then stops the server and deletes the profile. Everything runs at
    idle priority. Nothing touches the real profile, server or stores.

.EXAMPLE
    tools\linux-shots\shots.ps1 -Out C:\temp\shots -Scenarios shell,signin,instances

.NOTES
    Several runs can go side by side when each has its own -Out, -Port and -Profile.
#>
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Scenarios = "shell",
    [switch] $SignedOut,
    [int] $Port = 5088,
    [string] $Profile = "showcase"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $here "..\..")
$work = Join-Path $Out "_work"
$build = Join-Path $work "build"
$serve = Join-Path $work "serve"
New-Item -ItemType Directory -Force $Out, $serve | Out-Null
# The stop file of the previous run would stop this run's server at once.
Remove-Item (Join-Path $serve "stop") -Force -ErrorAction SilentlyContinue

function Invoke-Low([string] $exe, [string[]] $argv) {
    $p = Start-Process $exe -ArgumentList $argv -NoNewWindow -PassThru
    # Read the handle now: without it Windows PowerShell reports no exit code once the process is gone.
    $null = $p.Handle
    try { $p.PriorityClass = "Idle" } catch {}
    $p.WaitForExit()
    return $p.ExitCode
}

if ((Invoke-Low "dotnet" @("build", "`"$repo\tools\smoke\showcase\showcase.csproj`"", "--artifacts-path", "`"$build`"", "-nologo", "-v", "q", "-m:2", "-p:UseSharedCompilation=false")) -ne 0) {
    throw "The showcase (fake server) build failed."
}
if ((Invoke-Low "dotnet" @("build", "`"$here\linux-shots.csproj`"", "--artifacts-path", "`"$build`"", "-nologo", "-v", "q", "-m:2", "-p:UseSharedCompilation=false")) -ne 0) {
    throw "The harness build failed."
}

$env:CL_SMOKE_SIGNED_OUT = if ($SignedOut) { "1" } else { $null }
$env:CL_SHOWCASE_PORT = "$Port"
$env:CL_SHOWCASE_PROFILE = $Profile
$env:CL_CURSEFORGE_BASE = "http://127.0.0.1:9"
$server = Start-Process (Join-Path $build "bin\showcase\debug\ClShowcase.exe") -ArgumentList "`"$serve`"", "serve" -PassThru
try { $server.PriorityClass = "Idle" } catch {}
try {
    $up = $false
    for ($i = 0; $i -lt 60 -and -not $up; $i++) {
        try { Invoke-WebRequest "http://127.0.0.1:$Port/auth/me" -UseBasicParsing -TimeoutSec 2 | Out-Null; $up = $true }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "The fake server did not come up; its log is in $serve." }

    $env:CL_PROFILE = $Profile
    $code = Invoke-Low (Join-Path $build "bin\linux-shots\debug\ClLinuxShots.exe") @("`"$Out`"", $Scenarios)
    "harness exit $code"
}
finally {
    New-Item -ItemType File -Force (Join-Path $serve "stop") | Out-Null
    if (-not $server.WaitForExit(15000)) { $server.Kill() }
    Remove-Item -Recurse -Force (Join-Path $env:APPDATA "CloudLauncher\$Profile") -ErrorAction SilentlyContinue
    foreach ($key in Get-ChildItem "HKCU:\Software\Microsoft\Internet Explorer\Main\FeatureControl" -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $key.PSPath -Name "ClShowcase.exe" -ErrorAction SilentlyContinue
    }
}
