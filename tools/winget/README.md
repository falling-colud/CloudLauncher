# CloudLauncher on winget

`manifests/` holds the manifests submitted to the Windows Package Manager community repository
([microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)), in the layout that repository
uses: `manifests/f/fallingcolud/CloudLauncher/<version>/`. A version is three files: the version
manifest, the installer manifest and the `en-US` locale manifest. The package identifier is
`fallingcolud.CloudLauncher` and the moniker `cloudlauncher`, so once a version is published,
`winget install fallingcolud.CloudLauncher` (or `winget install cloudlauncher`) installs it and
`winget upgrade` picks up later versions.

## What the manifest says

- The installer is the Inno Setup `CloudLauncher-Setup-<version>.exe` attached to the GitHub
  release for that version: per user, x64, Windows 10 1809 (10.0.17763) or later, no elevation.
- The Apps & Features product code is the Inno `AppId` from `installer/CloudLauncher.iss` followed
  by `_is1`: `{36177AA6-424D-4067-BC72-75848D92B481}_is1`. It never changes (the `.iss` says so),
  and it is how winget finds the installed copy. If the AppId ever changed, the manifest would
  have to change with it.
- Silent install runs `/VERYSILENT /NORESTART`, silent with progress `/SILENT /NORESTART`. The
  `[Run]` entry in the `.iss` has `skipifsilent`, so a winget install does not start the launcher
  afterwards (the repository's pipeline fails a package that does).
- `ManifestVersion` is 1.28.0, the newest frozen schema in
  [microsoft/winget-cli](https://github.com/microsoft/winget-cli/tree/master/schemas/JSON/manifests)
  when this was written. Use the newest one the repository accepts; `wingetcreate` picks it by
  itself.
- Every string in the manifests is plain ASCII; keep it that way.

## The installer URL must not change

The repository downloads the installer and compares its SHA-256 with the manifest, when the pull
request is opened and again later. A URL therefore has to serve the same bytes for as long as the
version exists. `https://cloudlauncher.co/...` serves whatever is current, so it cannot be used.
The manifests point at the GitHub release asset,
`https://github.com/falling-colud/CloudLauncher/releases/download/v<version>/CloudLauncher-Setup-<version>.exe`,
one file per tag. So every release that goes to winget needs a GitHub release `v<version>` with
the same setup `.exe` that `publish-client.py` put on the website attached to it.

Never replace or delete a release asset after it has been submitted. If the file was wrong, ship
a new version.

## Checking a manifest locally

```
winget validate --manifest tools\winget\manifests\f\fallingcolud\CloudLauncher\0.9.2
```

This checks the schema and the hash format; it does not download anything. To test the install
itself, winget has to be allowed to use local manifests. That is an administrator setting, off by
default. From an elevated prompt:

```
winget settings --enable LocalManifestFiles
winget install --manifest tools\winget\manifests\f\fallingcolud\CloudLauncher\0.9.2
winget uninstall --id fallingcolud.CloudLauncher
winget settings --disable LocalManifestFiles
```

The install must finish without showing a window, and Settings > Apps must then list
"CloudLauncher" by "falling_colud" with the right version. That is what the repository's pipeline
checks in a clean VM, so it is worth doing once per release.

## First submission

A new package is a pull request to microsoft/winget-pkgs that a moderator approves by hand,
usually within a few days. Two ways to open it:

1. With `wingetcreate` (`winget install Microsoft.WingetCreate`). It needs a GitHub token with
   the `public_repo` scope; `wingetcreate token --store` signs in and keeps it in the Windows
   credential manager. It makes the fork under your account if there is none.

   ```
   wingetcreate submit tools\winget\manifests\f\fallingcolud\CloudLauncher\0.9.2
   ```

   It names the pull request itself; `--prtitle` overrides that.

2. By hand: fork microsoft/winget-pkgs, make a branch, copy the folder to
   `manifests/f/fallingcolud/CloudLauncher/0.9.2/`, commit, and open the pull request titled
   `New package: fallingcolud.CloudLauncher version 0.9.2`.

What the reviewers and the pipeline expect:

- The pull request template's boxes ticked: the Contributor License Agreement signed (a bot asks
  on the first PR; answer it with the comment it tells you), no other open PR for the same
  package, only one manifest (one package version) in the PR, `winget validate` and
  `winget install --manifest` run locally, the manifest on a 1.x schema.
- Nothing in the PR except the three manifest files. No README, no other versions.
- The installer URL and SHA-256 match (the pipeline downloads and hashes the file), the URL is
  permanent (see above), and the file is the one the GitHub release page shows.
- The pipeline installs the package silently in a VM and compares the Apps & Features entry with
  the manifest: product code, display name, publisher, version. The Inno installer writes
  `CloudLauncher`, `falling_colud` and the `AppVersion`, which match.
- Windows Defender scans the installer. An Authenticode signature is not required, but an
  unsigned installer is more likely to trip a false positive and SmartScreen; see the Authenticode
  section in `tools/release/README.md`.
- Publisher, package name, license, URLs and tags are checked by hand. `License: Proprietary` with
  the `LicenseUrl` is the customary form for source-available software; every URL must answer;
  tags are lowercase; the moniker must not clash with another package's.
- It helps that the PR comes from the `falling-colud` GitHub account, which owns the source
  repository the manifest points at.

The PR gets labels as it goes (`Azure-Pipeline-Passed`, `Validation-Completed`,
`Moderator-Approved`, `Publish-Pipeline-Succeeded`). A `Needs-Author-Feedback` or
`Validation-...-Error` label means a comment from the bot or a moderator says what to fix; push
the fix to the same branch.

## Later versions

Once `fallingcolud.CloudLauncher` exists in the repository, a new version is one command:

```
wingetcreate update fallingcolud.CloudLauncher --urls https://github.com/falling-colud/CloudLauncher/releases/download/v<version>/CloudLauncher-Setup-<version>.exe --version <version> --release-date <yyyy-mm-dd> --submit
```

It takes the previous version's manifests from the repository, swaps in the new URL, hash,
version and release date, keeps the locale text, and opens the pull request (`New version:
fallingcolud.CloudLauncher version <version>`). To look at the files before they go, run it with
`--out tools\winget\manifests` instead of `--submit`; it writes the new version folder next to the
old ones here, and `wingetcreate submit <that folder>` sends it afterwards. Either way, this folder
should end up holding what was submitted. Locale text that changes (description, URLs) is edited
here first and then in the generated manifest. Do not put release notes in the manifest; the
`ReleaseNotesUrl` points at `CHANGELOG.md`.

The manual route works too: same fork, a branch, the new folder next to the old ones, a PR.

Version notes:

- Version numbers went from 1.8.3 back to 0.8.4 (see `CHANGELOG.md`). 0.9.2 is the first version
  on winget. Never submit a 1.x; the next major version is 2.0.
- The launcher updates itself and does not rewrite the version in Apps & Features, so after a
  self-update winget still sees the version the setup wrote. `winget upgrade` will then offer the
  newest manifest even when the launcher is already there; installing it just reinstalls the same
  build. A PC that installed a 1.8.x setup shows 1.8.x to winget, which is "newer" than every 0.x,
  so winget offers nothing there until 2.0.
