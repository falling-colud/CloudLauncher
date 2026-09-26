# Releasing CloudLauncher

Every launcher from 0.9.0 on installs an update only when it is signed: the release names a manifest
(version, SHA-256 and size of the package) signed with ECDSA P-256, and the launcher checks the
signature against the public keys compiled into `CloudLauncher/Services/UpdateVerifier.cs`, then
checks the download against the signed manifest. An unsigned or altered release is never offered;
the launcher says it could not be verified and points to the website instead. So every release from
now on must go through `publish-client.py`, which signs it.

The signing tool is `tools/release/signer` (`cl-signer`). Run it with
`dotnet run --project tools/release/signer -- <command>`.

## One time: the keys

```
dotnet run --project tools/release/signer -- keygen
```

This creates two key pairs in `%USERPROFILE%\.cloudlauncher-release` and never overwrites existing
ones:

- **k1**, the release key: `update-key-k1.dpapi`, encrypted with DPAPI for this Windows account.
  Only this account on this PC can use it.
- **k2**, the backup key: `backup\update-key-k2.pem`, a plain PEM file.

It prints both public keys (also saved in `public-keys.txt`); they belong in
`UpdateVerifier.TrustedKeys`. Keys never go in the repository.

## Back up k2 (do it now)

k1 is lost if Windows is reinstalled or the account is gone. k2 is the way back, so:

1. Copy `backup\update-key-k2.pem` to offline storage: a USB stick kept somewhere safe, a password
   manager entry, or both.
2. Check the copy opens, then delete the file from the PC and empty the Recycle Bin.

Anyone with k2 can sign an update every launcher accepts. Treat it like a master password.

## Publishing a release

1. Bump `<Version>` in `CloudLauncher/CloudLauncher.csproj`.
2. Build: `./publish-release.ps1`. It writes `artifacts\CloudLauncher-<version>.zip` (the update
   package) and `artifacts\CloudLauncher-Setup-<version>.exe`. It uploads nothing.
3. Launch the build once: `./tools/smoke/smoke.ps1`.
4. Put the release notes in a text file.
5. Publish:

   ```
   python tools/release/publish-client.py <version> artifacts\CloudLauncher-<version>.zip artifacts\CloudLauncher-Setup-<version>.exe notes.txt
   ```

   It signs the zip with k1 and checks the signature with the launcher's own verifier, uploads the
   package as `packages/<sha256>.bin` and `package.bin` and the installer as `installer.bin` (each
   checked by size and SHA-256 on the server before it is moved into place), writes
   `releases.json`, then `latest.json` last, keeps the newest 3 files in `packages/`, and finally
   checks the live server: the signature in the served `latest.json`, and the downloaded package.

`publish-client.py` reads its settings from environment variables, or from
`tools/release/release.local.json` (not in git; copy `release.example.json`). A variable wins over
the file.

| Variable | JSON key | Meaning |
| --- | --- | --- |
| `CL_RELEASE_SSH_HOST` | `sshHost` | `user@host` of the server |
| `CL_RELEASE_SSH_KEY` | `sshKey` | SSH private key file |
| `CL_RELEASE_REMOTE_DIR` | `remoteDir` | the server's launcher data folder |
| `CL_RELEASE_PUBLIC_URL` | `publicUrl` | the server's public `https://` address |
| `CL_RELEASE_SIGN_KEY` | `signKey` | key id to sign with (default `k1`) |
| `CL_RELEASE_SIGN_PEM` | `signPem` | PEM file, for a key not kept with DPAPI |

To check a package by hand:
`dotnet run --project tools/release/signer -- verify --zip <zip> --json <latest.json or sign output>`.

## Authenticode (Windows SmartScreen)

Update signing above is the launcher checking its own updates. Authenticode is what Windows checks,
and without it SmartScreen warns about the installer. `publish-release.ps1` and `build-installer.ps1`
sign `CloudLauncher.exe` and the installer when one of these is configured:

- `CL_SIGN_THUMBPRINT`, or `CL_SIGN_PFX` + `CL_SIGN_PFX_PASSWORD`: a classic code-signing certificate.
- Azure Trusted Signing (the portal calls it Artifact Signing): `CL_SIGN_TS_ENDPOINT` (the account's
  region endpoint, e.g. `https://neu.codesigning.azure.net/`), `CL_SIGN_TS_ACCOUNT` and
  `CL_SIGN_TS_PROFILE` (the certificate profile), or the same three under `trustedSigning` in
  `release.local.json`. It needs signtool from the Windows SDK, the `Microsoft.Trusted.Signing.Client`
  NuGet package unzipped to `%USERPROFILE%\.cloudlauncher-release\trusted-signing\pkg` (or
  `CL_SIGN_TS_DLIB` pointing at its `bin\x64\Azure.CodeSigning.Dlib.dll`), and an Azure CLI
  sign-in (`az login`) by someone with the "Certificate Profile Signer" role on the account. The
  timestamp comes from `http://timestamp.acs.microsoft.com`; the certificates themselves live only a
  few days, so the timestamp is what keeps a signature valid.

## If k1 is lost

Launchers in the field trust k1 and k2, so a release signed with k2 still reaches them:

1. Get `update-key-k2.pem` back from offline storage.
2. Make a new backup key: `dotnet run --project tools/release/signer -- keygen --backup k3`. It
   prints the public key.
3. In `UpdateVerifier.TrustedKeys`, keep k2, add k3, and remove k1.
4. Build that release, and publish it signed with k2:
   `CL_RELEASE_SIGN_KEY=k2` and `CL_RELEASE_SIGN_PEM=<path to update-key-k2.pem>`.
5. From then on k2 is the release key (sign with the same two settings) and k3 is the backup: move
   `backup\update-key-k3.pem` offline and delete it from the PC, as with k2.

If k1 was stolen rather than lost, do the same straight away: the release that drops k1 is what
stops launchers from accepting anything signed with it.

## Optional: Authenticode

`build-installer.ps1` and `publish-release.ps1` sign `CloudLauncher.exe` before it is zipped and the
setup `.exe` after it is built, when one of these is set:

- `CL_SIGN_THUMBPRINT`: thumbprint of a code-signing certificate in the current user's store.
- `CL_SIGN_PFX` and `CL_SIGN_PFX_PASSWORD`: a `.pfx` file and its password.

`CL_SIGN_TIMESTAMP_URL` picks the timestamp server (default `http://timestamp.digicert.com`).
`signtool.exe` comes from the newest Windows 10/11 SDK (x64). With none of them set, the scripts
print one line and build unsigned.

## Deploying the server

Deploy the server before publishing a client that relies on anything new on it.

```
dotnet publish CloudLauncher.Server -c Release -r linux-arm64 --self-contained -o artifacts/server-publish
CL_DEPLOY_HOST=user@host CL_DEPLOY_KEY=~/.ssh/key CL_DEPLOY_DIR=/path/to/cloudlauncher bash tools/release/deploy-server.sh
```

The script dumps the database first, uploads the publish next to the live folder, keeps the live
`appsettings*.json`, swaps the folders (the old one stays as `server.prev-<time>`), relabels the files
for SELinux and restarts the service. Rolling back is moving `server.prev-<time>` back.
