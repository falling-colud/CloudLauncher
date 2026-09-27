# Changelog

Version numbers went from 1.8.3 back to 0.8.4. Launchers on 1.x are offered the 0.x releases
as 1.x so they keep updating; after 0.x the next major version is 2.0.

## 0.9.5 - 2026-09-27

- Memory: the Settings default ("Maximum RAM allocated to new instances") is used again. It was saved
  but ignored, so every instance without its own value got 16384 MB on a PC with 32 GB or more. An
  instance's own memory slider now saves however it is moved.
- The Graph and Planning views stay smooth when zoomed far out. Dependency lines, links and the
  planning grid are drawn once per zoom instead of on every frame, and mod names show at every zoom
  again.

## 0.9.4 - 2026-09-27

- NeoForge versions show again when creating or editing an instance. NeoForge's own version list
  broke today and listed only its two newest betas; the launcher now falls back to another index
  when that happens and keeps the last full list on this PC.
- NeoForge builds for the year-numbered Minecraft versions (26.1.2, 26.3, ...) are matched to the
  right Minecraft version.

## 0.9.3 - 2026-09-27

- Instances can be duplicated: right-click one and pick Duplicate. The copy gets the same Minecraft
  version, loader, mods, configs and settings, and its worlds if you want them.
- Signing in is optional. The launcher opens straight to Instances, and everything that needs no
  account works without one: making, importing and playing instances, browsing and installing
  mods and modpacks, worlds, resource packs, shaders, servers and configs. An account is for
  sharing, syncing and hosting.
- Instances made without an account stay on this PC. Once signed in, right-click one (or open its
  Options) and pick Add to my account to share it; it keeps its settings and files. Deleting one
  sends its folder to the Recycle Bin.
- A new install no longer downloads Create Ultimate Selection 2 by itself, and opening a shared
  instance that was never downloaded offers a Download button instead of starting straight away.
- Deleting an instance while it downloads stops the download first and removes what it fetched.
- Update checks can run at up to 100 per second (was 20). Server: per-user store limits raised to
  match, and launchers that are not signed in can use the store proxy.

## 0.9.2 - 2026-09-26

- CurseForge and Modrinth: when a store, or the launcher server in front of it, asks for a
  pause, the launcher waits and tries again by itself and says "CurseForge is busy, trying
  again..." instead of failing. "API key" only ever comes up when it is about your own key.
- Files whose authors turned off third-party downloads are named, with a link to the project
  page. A CurseForge import lists them in manual-downloads.txt in the instance folder.
- Update checks answer more mods in bulk. The checks-per-second setting now covers both stores.
- Server: higher per-user limits, cached answers no longer count against them, and a short
  database hiccup no longer turns store requests away.
- Google sign-in is only offered when the server has it set up.
- The contact address is contact@cloudlauncher.co.
- The website was rewritten, with the source on GitHub linked from it.

## 0.9.1 - 2026-09-26

- The launcher and website moved to cloudlauncher.co. Existing installs switch to the new
  address by themselves once their DNS resolver knows it; the old address keeps working.
- Mod, pack and world icons are no longer cropped when they aren't square.
- Planning notes shrunk to their title keep the resize grip clear of the text.
- Discord link in the sidebar, in Settings > About and on the website.

## 0.9.0 - 2026-09-25

- New app icon.
- Instances can be shown as cards or as a compact list (toggle at the right of the folder strip).
- Updates are signed. The launcher checks the signature, size and hash of a download before
  installing it and refuses anything that doesn't match.
- Sign-in tokens are stored encrypted for your Windows account instead of in settings.json.
- Account page: change your username, download your data, delete your account.
- Privacy policy and terms, linked from sign-up and Settings > About.
- Settings > About: version, licenses, links, "Report a problem" and "Open logs". The launcher
  now keeps a log file.
- Offline accounts are called offline accounts, and adding one needs a Microsoft account that
  owns Minecraft.
- Links from descriptions and store pages only ever open in the browser.
- Safer handling of files from modpacks, shared instances and downloads.
- New address: https://cloudlauncher.co. Existing installs move over by themselves.
- Server: fixed sign-in rate limiting behind the proxy, account lockout after repeated wrong
  passwords, storage limits now cover every kind of upload, unused files are cleaned up, and a
  long list of smaller hardening fixes.
- Bundles the latest .NET 10 runtime.

## 0.8.4 - 2026-09-25

- CurseForge exports list mods, resource packs and shaders that CurseForge has instead of
  packing them into the zip, including mods built separately per store.
- Files whose authors disabled third-party downloads are named when the export finishes.
- Importing a CurseForge pack puts its resource packs and shaders in the right folders.
- Files tab: removed the "Recently changed" row. Logs tab: count and level filter stay on the
  right.

## 1.8.3 - 2026-09-25

- Sharing is one Overview of everything shared by you or with you, with filters and search.
- Redeem a link: paste a share link or team code, see what it gives you, then accept.
- Share links open a page in the browser that explains how to use them.
- Collaborators can upload versions and manage people on hosted mods, worlds, resource packs
  and shader packs, not just on instances.
- Planning boards snap to a 24 px grid, with a checkbox to turn it off.
- Rust is the default accent.
- Pages no longer draw twice when you open them.
- Export can include or leave out individual files inside folders; CurseForge zips include
  modlist.html.

## 1.8.2 - 2026-09-24

- Fixed the CurseForge key being cleared whenever you left the Settings page.
- With your own CurseForge key, requests can go straight to CurseForge.
- Descriptions no longer draw over dialogs.
- Accent-coloured text stays readable with dark accents.
- Files tab: share and unshare by dragging between panes.

## 1.8.1 - 2026-09-24

- In narrow windows, side pages cover the list instead of squeezing it.
- Action bars wrap instead of running under the search box.
- Instance page buttons rearranged to fit smaller widths.

## 1.8.0 - 2026-09-23

- New Slate look (the style of the Slate mods), with a Classic option and a Vanilla skin.
  Accent, corners, motion, pixel fonts and icons, shadows and sounds are all adjustable.
- The CurseForge key is stored encrypted, with a Test key button.
- Only one launcher window per profile.
- Right-click any version to read its changelog.
- Much faster update checks on big packs.
- Export instances as CurseForge .zip or Modrinth .mrpack.
- Your own public instances show up in the browser.

## 1.7.12 - 2026-09-22

- The dependency graph is far faster and can show up to 3000 mods.
- Fixed: enabling one world enabled every world with the same name.
- "Share an instance" opens a picker.
- Cloud storage tab shows totals, shared space and quota more clearly.

## 1.7.11 - 2026-09-22

- Storage is its own page and works without an account.
- Teams moved into Sharing.
- Sharing has a Cloud storage tab showing what you use on the server.

## 1.7.10 - 2026-09-22

- Resource packs and shaders always open their page when clicked.
- Clearer wording about which instances get something.

## 1.7.9 - 2026-09-22

- Worlds, Resource packs and Shader packs work the same way: one row per item, one switch,
  folders for organising, and a page per item with per-instance choices.
- Resource pack conflicts are shown for the selected instance.

## 1.7.8 - 2026-09-22

- Resource packs are listed in the selected instance's real load order.
- Worlds lists your saves, organised in folders.
- Planning boards are managed from a side rail.

## 1.7.7 - 2026-09-22

- Simpler handling of shared worlds, resource packs and shaders.
- Folder settings moved into a side panel.
- Worlds are managed from their own page.
- New Storage tab showing real disk use.

## 1.7.6 - 2026-09-22

- The Mods page filters apply to every tab, including the graph and the store.
- Browsing mods follows the selected instance's version and loader.
- Fixed the layout of the right-hand panel on browse pages.

## 1.7.5 - 2026-09-22

- The Defaults chip works like a folder.
- Small fixes to the New folder button and Hide unchecked.

## 1.7.4 - 2026-09-22

- Folders show everything, with members ticked.
- Downloading a world, resource pack or shader asks which instances it goes to.
- Categories follow the Mods page filters.

## 1.7.3 - 2026-09-22

- Installing a mod is much faster.
- Drag to reorder works properly.
- The folder strip can no longer take a page down with it.

## 1.7.2 - 2026-09-22

- Fixed the Instances page failing to load in 1.7.1.

## 1.7.1 - 2026-09-22

- Folder rules for versions, loaders and instances.
- Drag to set the load order.
- Resource packs and shaders list what every instance has.
- Double-click an instance to open its modpack management.

## 1.7.0 - 2026-09-22

- Worlds, resource packs, shaders and mods can be set as defaults for every compatible
  instance.
- The Mods page manages mods across all instances.
- One Import button everywhere.
- Pages keep their scroll position and search when you come back to them.
- Dates and times follow your Windows settings.

## 1.6.1 - 2026-09-21

- Every page lays out its buttons the same way.
- Host worlds, mods and shader packs as well as resource packs.

## 1.6.0 - 2026-09-21

- Invitations and share links, and a Sharing page.
- Host shader packs, config sets, KubeJS scripts and data packs.
- Files page, server hosting, team roles and an activity feed.
- Lists keep working while offline.

## 1.4.2 - 2026-09-21

- Pause and stop downloads.
- New description editor.
- Smoother scrolling.

## 1.4.0 - 2026-09-21

- Servers page: every server across your instances with live status, join from the launcher,
  and an RCON console.
- Config & scripts: search, compare and copy configs and KubeJS scripts across instances.
- Hosted mods: rename, delete, version changelogs, icons and a proper upload dialog.
- Teams: rename, transfer and leave. Account: change password, sign out everywhere, storage
  use.
- Worlds show seed, game mode and more, with backup, restore, duplicate and zip export/import.
- Resource packs can be turned on and ordered; shaders gained version choice and folders.
- Crash reports, screenshot menus and file management inside an instance.
- The graph view no longer freezes on big packs.

## 1.3.8 - 2026-09-20

- A pack shared with you waits under "Shared with me" instead of joining your list.
- Removing a shared pack from your list keeps your access.

## 1.3.7 - 2026-09-20

- "What's new" in Settings shows the changelog.
- Update all shows every changelog between your version and the new one.
- Shift-click to select a range in the list view.

## 1.3.6 - 2026-09-20

- The mod list no longer jumps when it refreshes.

## 1.3.5 - 2026-09-20

- Sort the mod list by category, with a heading per category.

## 1.3.4 - 2026-09-20

- Right-click menu on the Categories screen.

## 1.3.3 - 2026-09-20

- Categories fold in the graph view.

## 1.3.2 - 2026-09-20

- Theme colours apply to descriptions, tags and highlights.

## 1.3.1 - 2026-09-20

- Import categories from another instance.

## 1.3.0 - 2026-09-20

- Shaders page.
- Edit configs and KubeJS scripts inside the launcher.
- Parallel mod downloads with per-mod progress.
- Custom colours and colour-coded logs.
- Use your own CurseForge key.
- The launcher uses HTTPS.

## 1.2.0 - 2026-09-16

- Mods link to the store they came from.
- Filter mods by store, per-mod update channel, Update all with changelogs, Java version
  picker.
- Start server installs the matching NeoForge, Forge or Fabric server.

## 1.1.12 - 2026-09-10

- Notes: checkboxes, progress bars, task lists and folding.

## 1.1.9 - 2026-09-05

- Files marked as private are never uploaded with a pack.

## 1.1.7 - 2026-09-03

- Fixed a crash on startup after an incomplete browser component download.

## 1.1.4 - 2026-08-31

- Low mode adjusts distant-terrain settings only.
