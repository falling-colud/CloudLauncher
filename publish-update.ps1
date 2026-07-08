#requires -Version 5.1
<#
.SYNOPSIS
    Build, package, and publish a CloudLauncher update to the server in one step.

.DESCRIPTION
    Publishes the WPF client, zips the output, logs in as an admin, and uploads the
    package to the server's /launcher/upload endpoint. Every launcher will then see
    the new version on its next launch (and from Settings -> Check for updates).

    The version is read from <Version> in CloudLauncher.csproj unless -Version is passed,
    so the usual flow is: bump <Version> in the csproj, then run this script.

.EXAMPLE
    ./publish-update.ps1 -Username colud -Password hunter2

.EXAMPLE
    ./publish-update.ps1 -Username colud -Password hunter2 -Version 1.2.0 -Notes "Adds team sync"
#>
[CmdletBinding()]
param(
    [string]$ServerUrl = "http://130.61.131.193:5000",
    [Parameter(Mandatory = $true)][string]$Username,
    [Parameter(Mandatory = $true)][string]$Password,
    [string]$Version,
    [string]$Notes = "",
    [string]$Runtime = "win-x64",
    # By default we publish self-contained so target machines don't need the .NET runtime.
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "CloudLauncher\CloudLauncher.csproj"
if (-not (Test-Path $proj)) { throw "Cannot find project at $proj" }

# 1. Resolve the version (from the csproj unless overridden).
if (-not $Version) {
    [xml]$xml = Get-Content $proj
    $Version = (@($xml.Project.PropertyGroup.Version) | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { throw "No <Version> found in the csproj; pass -Version explicitly." }
}
Write-Host "Publishing CloudLauncher $Version ($Runtime)..." -ForegroundColor Cyan

# 2. Publish the client.
$outDir = Join-Path $root "artifacts\update\$Version"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
$scArg = if ($FrameworkDependent) { "--no-self-contained" } else { "--self-contained" }
dotnet publish $proj -c Release -r $Runtime $scArg -o $outDir "/p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# 3. Package the publish output into a single zip.
$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$zip = Join-Path $artifacts "CloudLauncher-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip
Write-Host "Packaged -> $zip" -ForegroundColor Green

# 4. Authenticate.
$ServerUrl = $ServerUrl.TrimEnd('/')
$loginBody = @{ username = $Username; password = $Password } | ConvertTo-Json
$tokens = Invoke-RestMethod -Uri "$ServerUrl/auth/login" -Method Post -Body $loginBody -ContentType "application/json"
$token = $tokens.accessToken
if (-not $token) { throw "Login succeeded but no access token was returned." }

# 5. Upload (streamed multipart so large self-contained builds don't blow up memory).
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMinutes(30)
$client.DefaultRequestHeaders.Authorization =
    [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $token)

$content = [System.Net.Http.MultipartFormDataContent]::new()
$fs = [System.IO.File]::OpenRead($zip)
try {
    $fileContent = [System.Net.Http.StreamContent]::new($fs)
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/octet-stream")
    $content.Add($fileContent, "file", [System.IO.Path]::GetFileName($zip))
    $content.Add([System.Net.Http.StringContent]::new($Version), "version")
    if ($Notes) { $content.Add([System.Net.Http.StringContent]::new($Notes), "notes") }

    Write-Host "Uploading to $ServerUrl/launcher/upload ..." -ForegroundColor Cyan
    $resp = $client.PostAsync("$ServerUrl/launcher/upload", $content).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $resp.IsSuccessStatusCode) { throw "Upload failed ($([int]$resp.StatusCode)): $body" }

    Write-Host "Update $Version published successfully." -ForegroundColor Green
    Write-Host $body
}
finally {
    $fs.Dispose()
    $content.Dispose()
    $client.Dispose()
}
