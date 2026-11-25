<#
.SYNOPSIS
    Builds the Linux launcher release: artifacts\CloudLauncher-<version>-linux-x64.tar.gz.

.DESCRIPTION
    Publishes CloudLauncher.Linux self-contained as one file for linux-x64 (Release), then packs it
    with install.sh, the icon and the licence files (tools\release\pack-linux.py). Uploads nothing:
    publish-linux.py signs and publishes the tarball. The version is the one in
    CloudLauncher.Linux.csproj. Builds at idle priority.

.EXAMPLE
    tools\release\build-linux.ps1
#>
param(
    [string] $Rid = "linux-x64"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$project = Join-Path $repo "CloudLauncher.Linux\CloudLauncher.Linux.csproj"
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project." }

$artifacts = Join-Path $repo "artifacts"
$publish = Join-Path $artifacts "linux\publish-$Rid"
$tarball = Join-Path $artifacts "CloudLauncher-$version-$Rid.tar.gz"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

$argv = @("publish", "`"$project`"", "-c", "Release", "-r", $Rid, "--self-contained", "true",
          "-p:PublishSingleFile=true", "-o", "`"$publish`"", "-nologo", "-v", "q", "-m:2",
          "-p:UseSharedCompilation=false", "--artifacts-path", "`"$(Join-Path $artifacts 'linux\build')`"")
$p = Start-Process dotnet -ArgumentList $argv -NoNewWindow -PassThru
$null = $p.Handle
try { $p.PriorityClass = "Idle" } catch {}
$p.WaitForExit()
if ($p.ExitCode -ne 0) { throw "dotnet publish failed ($($p.ExitCode))." }

python (Join-Path $PSScriptRoot "pack-linux.py") $publish $version $Rid $tarball
if ($LASTEXITCODE -ne 0) { throw "Packing failed." }
$hash = (Get-FileHash $tarball -Algorithm SHA256).Hash.ToLowerInvariant()
"version $version"
"tarball $tarball ($((Get-Item $tarball).Length) bytes, sha256 $hash)"
