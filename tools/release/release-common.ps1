# Shared by build-installer.ps1 and publish-release.ps1 (dot-source it).
#
# Invoke-CodeSign signs a file with Authenticode when one of these is set, and otherwise prints one
# info line per run and does nothing:
#   CL_SIGN_THUMBPRINT                   thumbprint of a code-signing certificate in the current
#                                        user's certificate store (Cert:\CurrentUser\My)
#   CL_SIGN_PFX + CL_SIGN_PFX_PASSWORD   a .pfx file and its password
#   CL_SIGN_TS_ENDPOINT + CL_SIGN_TS_ACCOUNT + CL_SIGN_TS_PROFILE
#                                        an Azure Trusted Signing (Artifact Signing) account: the
#                                        region endpoint, the account name and the certificate
#                                        profile. Signing then goes through Microsoft's signtool
#                                        dlib (CL_SIGN_TS_DLIB, default
#                                        %USERPROFILE%\.cloudlauncher-release\trusted-signing\pkg\bin\x64\Azure.CodeSigning.Dlib.dll,
#                                        the Microsoft.Trusted.Signing.Client NuGet package unzipped)
#                                        and authenticates with the Azure CLI's sign-in (az login).
#                                        The same three values can live under "trustedSigning" in
#                                        tools\release\release.local.json instead of the environment.
#   CL_SIGN_TIMESTAMP_URL                RFC 3161 timestamp server (default http://timestamp.digicert.com,
#                                        or Microsoft's http://timestamp.acs.microsoft.com for Trusted Signing)
# signtool.exe is taken from the newest Windows Kits 10 SDK (x64), else from PATH.

$script:CodeSignNoticeShown = $false

function Find-Iscc([string]$Iscc) {
    if ($Iscc) {
        if (-not (Test-Path $Iscc)) { throw "ISCC.exe not found at $Iscc." }
        return $Iscc
    }
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        # winget's JRSoftware.InnoSetup installs per-user by default, which lands here and in
        # neither Program Files.
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $found) {
        $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($onPath) { $found = $onPath.Source }
    }
    if (-not $found) {
        throw "ISCC.exe (Inno Setup 6) not found. Install it from https://jrsoftware.org/isdl.php or pass -Iscc <path>."
    }
    return $found
}

function Find-SignTool {
    foreach ($kits in @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")) {
        if (-not (Test-Path $kits)) { continue }
        $found = Get-ChildItem -Path $kits -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($found) { return $found }
        if (Test-Path (Join-Path $kits "x64\signtool.exe")) { return (Join-Path $kits "x64\signtool.exe") }
    }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

# The Trusted Signing settings, from the environment or from release.local.json next to this
# script, or $null when neither names an account.
function Get-TrustedSigningConfig {
    $cfg = @{
        Endpoint = $env:CL_SIGN_TS_ENDPOINT
        Account  = $env:CL_SIGN_TS_ACCOUNT
        Profile  = $env:CL_SIGN_TS_PROFILE
        Dlib     = $env:CL_SIGN_TS_DLIB
    }
    $local = Join-Path $PSScriptRoot "release.local.json"
    if (Test-Path $local) {
        $json = Get-Content $local -Raw | ConvertFrom-Json
        $ts = $json.trustedSigning
        if ($ts) {
            if (-not $cfg.Endpoint) { $cfg.Endpoint = $ts.endpoint }
            if (-not $cfg.Account)  { $cfg.Account  = $ts.account }
            if (-not $cfg.Profile)  { $cfg.Profile  = $ts.profile }
            if (-not $cfg.Dlib)     { $cfg.Dlib     = $ts.dlib }
        }
    }
    if (-not $cfg.Endpoint -and -not $cfg.Account -and -not $cfg.Profile) { return $null }
    foreach ($k in "Endpoint", "Account", "Profile") {
        if (-not $cfg[$k]) { throw "Trusted Signing is half configured: $k is missing (CL_SIGN_TS_* or trustedSigning in release.local.json)." }
    }
    if (-not $cfg.Dlib) {
        $cfg.Dlib = Join-Path $env:USERPROFILE ".cloudlauncher-release\trusted-signing\pkg\bin\x64\Azure.CodeSigning.Dlib.dll"
    }
    if (-not (Test-Path $cfg.Dlib)) {
        throw "The Trusted Signing dlib is not at $($cfg.Dlib). Unzip the Microsoft.Trusted.Signing.Client NuGet package there, or set CL_SIGN_TS_DLIB."
    }
    # The dlib signs with the Azure CLI's sign-in, which it finds through PATH. A shell started
    # before the CLI was installed does not have it yet, so the usual install folder is added here
    # rather than falling through to a surprise browser sign-in.
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        foreach ($bin in @("$env:ProgramFiles\Microsoft SDKs\Azure\CLI2\wbin", "${env:ProgramFiles(x86)}\Microsoft SDKs\Azure\CLI2\wbin")) {
            if (Test-Path (Join-Path $bin "az.cmd")) { $env:Path = "$bin;$env:Path"; break }
        }
        if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
            throw "Trusted Signing needs the Azure CLI (az) on PATH, signed in with az login."
        }
    }
    return $cfg
}

# Writes the metadata file signtool's dlib reads, once per run, and returns its path. Slow credential
# probes that never apply on a developer PC are left out so signing starts at once.
function Get-TrustedSigningMetadata($cfg) {
    if ($script:TrustedSigningMetadata -and (Test-Path $script:TrustedSigningMetadata)) { return $script:TrustedSigningMetadata }
    $dir = Join-Path $env:TEMP "cloudlauncher-sign"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir "metadata.json"
    $meta = [ordered]@{
        Endpoint               = $cfg.Endpoint
        CodeSigningAccountName = $cfg.Account
        CertificateProfileName = $cfg.Profile
        ExcludeCredentials     = @("ManagedIdentityCredential", "WorkloadIdentityCredential", "EnvironmentCredential", "VisualStudioCredential")
    }
    $meta | ConvertTo-Json | Set-Content -Path $path -Encoding ascii
    $script:TrustedSigningMetadata = $path
    return $path
}

function Invoke-CodeSign([string]$Path) {
    $thumbprint = $env:CL_SIGN_THUMBPRINT
    $pfx = $env:CL_SIGN_PFX
    $trusted = if (-not $thumbprint -and -not $pfx) { Get-TrustedSigningConfig } else { $null }
    if (-not $thumbprint -and -not $pfx -and -not $trusted) {
        if (-not $script:CodeSignNoticeShown) {
            Write-Host "Authenticode signing is not configured (CL_SIGN_THUMBPRINT, CL_SIGN_PFX or CL_SIGN_TS_*); files are left unsigned." -ForegroundColor DarkGray
            $script:CodeSignNoticeShown = $true
        }
        return
    }
    if (-not (Test-Path $Path)) { throw "Nothing to sign at $Path." }

    $signtool = Find-SignTool
    if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK signing tools." }

    $defaultTimestamp = if ($trusted) { "http://timestamp.acs.microsoft.com" } else { "http://timestamp.digicert.com" }
    $timestamp = if ($env:CL_SIGN_TIMESTAMP_URL) { $env:CL_SIGN_TIMESTAMP_URL } else { $defaultTimestamp }
    $signArgs = @("sign", "/fd", "sha256", "/tr", $timestamp, "/td", "sha256")
    if ($thumbprint) {
        $signArgs += @("/s", "My", "/sha1", ($thumbprint -replace '[^0-9A-Fa-f]', ''))
    } elseif ($pfx) {
        if (-not (Test-Path $pfx)) { throw "CL_SIGN_PFX points at $pfx, which does not exist." }
        $signArgs += @("/f", $pfx)
        if ($env:CL_SIGN_PFX_PASSWORD) { $signArgs += @("/p", $env:CL_SIGN_PFX_PASSWORD) }
    } else {
        $signArgs += @("/dlib", $trusted.Dlib, "/dmdf", (Get-TrustedSigningMetadata $trusted))
    }
    $signArgs += $Path

    Write-Host "Signing $Path ..." -ForegroundColor Cyan
    & $signtool @signArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE) for $Path." }
}
