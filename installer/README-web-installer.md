# The web installer

`CloudLauncher-Setup.exe`, built from `CloudLauncher-Web.iss`, is a 2 MB setup that installs
nothing itself. It reads `https://cloudlauncher.co/launcher/latest`, downloads the current full
installer, checks the download against the feed's `installerSha256` (Inno Setup's
`RequiredSHA256OfFile`, everything over HTTPS) and runs it with
`/SP- /SILENT /NORESTART /SUPPRESSMSGBOXES`; a `/DIR=` given to the stub is passed through. The
website's Download button serves it. The full installer stays at `/download/full` and
`/launcher/installer`, and the website links it under the SmartScreen note.

## Built once, never rebuilt

Windows SmartScreen keeps reputation per file hash (and per signing certificate, which we do not
have yet). A full installer is a new file every release, so it starts from "Windows protected your
PC" each time. The stub is the same bytes for every release, so whatever reputation it earns stays.
It contains nothing that changes per release: no version macro, no notes, no file from a publish.

Rebuild it only when `CloudLauncher-Web.iss` or `license-terms.txt` (which it embeds) changes,
and expect the reputation to start again when you do:

    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\CloudLauncher-Web.iss

writes `artifacts\web\CloudLauncher-Setup.exe`. `artifacts/` is not in git, so keep a copy of the
built file outside the repo (next to the release keys in `%USERPROFILE%\.cloudlauncher-release\`
is a good place). The copy on the server is the one that counts; `curl -o CloudLauncher-Setup.exe
https://cloudlauncher.co/download` gets it back, and its hash must be the one below.

## The current build

| | |
| --- | --- |
| Built | 2026-09-26 with Inno Setup 6.7.0, unsigned |
| Size | 2,187,875 bytes |
| SHA-256 | `8704335d48dfe7de8052a9f739bd7944c6a4ce8e58074b06f7fec8f5badaa684` |

## Uploading it

`tools/release/publish-client.py` never touches it. Copy it to the launcher root (the folder that
holds `latest.json` and `installer.bin`: `remoteDir` in `tools/release/release.local.json`) as
`web-installer.bin`, staged and checked like the publish script does:

    scp -i <key> artifacts/web/CloudLauncher-Setup.exe <user@host>:<remoteDir>/web-installer.bin.new
    ssh -i <key> <user@host> "sha256sum <remoteDir>/web-installer.bin.new && mv <remoteDir>/web-installer.bin.new <remoteDir>/web-installer.bin"

While the file is there, `GET /download` serves it as `CloudLauncher-Setup.exe` with
`Cache-Control: no-store`; remove the file and `/download` serves the full installer again. No
restart is needed either way. To check what the button hands out:

    curl -s -o /dev/null -D - https://cloudlauncher.co/download | grep -i content-disposition

Deploy the server with these routes before uploading the stub: it needs nothing new to work
(see below), but the website's `/download/full` link and the `/download` switch live there.

## Where the stub downloads from, in order

1. `/launcher/installer/<version>`: versioned and immutable (`Cache-Control: immutable` for a
   year). It answers only for the current release's version: a publish replaces `installer.bin`
   and keeps no older installer, so any other version is a 404 rather than a different file.
2. `/launcher/installer` and 3. `/download/full`: the current full installer.
4. `/download`: the website button. The only one of the four on a server from before these routes
   existed; once the server serves the stub there the checksum does not match and the attempt
   fails, which is fine as the last resort.

Every attempt carries the feed's SHA-256, so a wrong or altered file is refused whichever route
served it. On any failure the stub shows one message with the full-installer address
(`https://cloudlauncher.co/launcher/installer`) and exits with code 7 (Inno Setup's "preparing to
install failed"). The full installer's own log is left at `%TEMP%\CloudLauncher-Setup-<version>.log`.

## Testing it on a PC that has CloudLauncher installed

    artifacts\web\CloudLauncher-Setup.exe /VERYSILENT /DIR="<a scratch folder>" /LOG=stub.log

installs the current release into that folder instead of `%LocalAppData%\Programs\CloudLauncher`.
The full installer still registers the scratch folder as the CloudLauncher install (the
`{36177AA6-424D-4067-BC72-75848D92B481}_is1` uninstall key under HKCU and the Start Menu
shortcuts), so afterwards delete that key and put the Start Menu shortcuts back. Do not run the
scratch copy's `unins000.exe`: its uninstall step also forgets the sign-in of the real launcher.
