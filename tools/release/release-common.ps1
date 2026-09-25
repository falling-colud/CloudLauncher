# Shared by build-installer.ps1 and publish-release.ps1 (dot-source it).
#
# Invoke-CodeSign signs a file with Authenticode when one of these is set, and otherwise prints one
# info line per run and does nothing:
#   CL_SIGN_THUMBPRINT                   thumbprint of a code-signing certificate in the current
#                                        user's certificate store (Cert:\CurrentUser\My)
#   CL_SIGN_PFX + CL_SIGN_PFX_PASSWORD   a .pfx file and its password
#   CL_SIGN_TIMESTAMP_URL                RFC 3161 timestamp server (default http://timestamp.digicert.com)
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

function Invoke-CodeSign([string]$Path) {
    $thumbprint = $env:CL_SIGN_THUMBPRINT
    $pfx = $env:CL_SIGN_PFX
    if (-not $thumbprint -and -not $pfx) {
        if (-not $script:CodeSignNoticeShown) {
            Write-Host "Authenticode signing is not configured (CL_SIGN_THUMBPRINT or CL_SIGN_PFX); files are left unsigned." -ForegroundColor DarkGray
            $script:CodeSignNoticeShown = $true
        }
        return
    }
    if (-not (Test-Path $Path)) { throw "Nothing to sign at $Path." }

    $signtool = Find-SignTool
    if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK signing tools." }

    $timestamp = if ($env:CL_SIGN_TIMESTAMP_URL) { $env:CL_SIGN_TIMESTAMP_URL } else { "http://timestamp.digicert.com" }
    $signArgs = @("sign", "/fd", "sha256", "/tr", $timestamp, "/td", "sha256")
    if ($thumbprint) {
        $signArgs += @("/s", "My", "/sha1", ($thumbprint -replace '[^0-9A-Fa-f]', ''))
    } else {
        if (-not (Test-Path $pfx)) { throw "CL_SIGN_PFX points at $pfx, which does not exist." }
        $signArgs += @("/f", $pfx)
        if ($env:CL_SIGN_PFX_PASSWORD) { $signArgs += @("/p", $env:CL_SIGN_PFX_PASSWORD) }
    }
    $signArgs += $Path

    Write-Host "Signing $Path ..." -ForegroundColor Cyan
    & $signtool @signArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE) for $Path." }
}
