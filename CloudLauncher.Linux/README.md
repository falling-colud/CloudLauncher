# CloudLauncher for Linux

An Avalonia 11 front end over the **same services as the Windows launcher**. Sync, launching, mods,
modpack imports, downloads and accounts are the Windows code, compiled from `../CloudLauncher/Services`
(see the `<Compile Include>` items in `CloudLauncher.Linux.csproj`); only the UI is new.

## How the sharing works

- The service files are linked, not copied. Code that only works on Windows sits behind `#if WINDOWS`
  in those files (the SDK defines `WINDOWS` for the `net10.0-windows` WPF project only), with a Linux
  branch in `#else`, or behind `OperatingSystem.IsWindows()` at run time. The WPF project fails to build
  if `WINDOWS` is ever missing (`App.xaml.cs`).
- `Platform/WpfThreadingCompat.cs` provides WPF's `Application.Current.Dispatcher`, `DispatcherTimer` and
  `DispatcherPriority` on top of Avalonia's UI thread, under WPF's own names, so the services that hop to
  the UI thread compile unchanged. Linux views never use those types: they use `Avalonia.Threading`.
- `UiThread` (`Services/UiThread.cs`) is the neutral way for shared code to reach the UI thread.
- Image decoding in `PackAssetService`/`ResourcePackService` uses Skia through Avalonia on Linux:
  `BitmapImage` there is an alias for `Avalonia.Media.Imaging.Bitmap`.
- Data lives in `$XDG_DATA_HOME/CloudLauncher` (`~/.local/share/CloudLauncher`); secrets are 0600 files
  (no DPAPI); the recycle bin is the freedesktop trash (`Services/XdgTrash.cs`); folders open with
  `xdg-open` (`SafeLaunch`); Microsoft sign-in is the device-code flow (`Platform/DeviceCodeSignIn.cs`).

**Do not change files under `../CloudLauncher` from Linux work** unless the change is additive and
keeps the Windows behaviour byte for byte (an `#if WINDOWS` around existing code with a Linux `#else`).
When a shared service needs more than that, say so instead of editing it.

## UI conventions

- A page is a `UserControl` implementing `IShellPage` (`Nav` says which rail item lights up;
  `OnShown()` runs each time it comes on screen). Navigate with `MainWindow.Current.Navigate(page)`;
  `GoBack()`; the rail items clear the history.
- Popups are cards over the page: `await MainWindow.Current.ShowCardAsync(card, tcs.Task, cancel)`.
  `MessageCard.ConfirmAsync/AlertAsync` (or `MainWindow.Current.ConfirmAsync`) for questions.
  `MainWindow.Current.Toast(text)` for a short line on the status bar.
- Services: `App.State.<Service>` exactly as the Windows views use them. Read the Windows view that does
  the same job (`../CloudLauncher/Views/*.xaml.cs`) and follow its calls, checks and wording.
- Service events (ProgressHub, ManualDownloads, MinecraftAccounts, ConnectivityChanged ...) arrive on the
  UI thread. Unsubscribe when the page goes away (`DetachedFromVisualTree`).
- Slow work (disk scans, hashing) goes through `Task.Run`; never block the UI thread.
- Errors: show a sentence in the page's status text (`TextBlock` with class `danger`), log with
  `AppLog.LogError("<area>", ex)`. `ApiClient.ServerSentence(ex)` turns a server error into words;
  `StoreRequestException.PlainFor(ex)` does the same for the mod stores.
- Compiled bindings are on by default: a `DataTemplate` needs `x:DataType`.
- Files and folders: `TopLevel.GetTopLevel(this)!.StorageProvider` for pickers,
  `SafeLaunch.OpenFolder/OpenUrl/OpenFile` to hand things to the desktop, `TopLevel.Clipboard` to copy.

### Words

Like the Windows launcher and the Slate menus: buttons and labels are 1-3 words in Title Case
("Add Mods", "Max Memory"); at most one short line of explanation where it is really needed; no
"Click here to"... Status lines are plain sentences ("Downloaded 3 mods.").

### Look (Themes/Slate.axaml)

- Text: `h1` (pixel font, 27), `h2` (pixel font, 18), `h3`, `muted`, `caption`, `danger`, `success`,
  `icon` (Slate pixel icons, Segoe MDL2 codepoints).
- Surfaces: `Border.card`, `Border.well` (inset list area), `Border.dialog` (cards), `Border.pill`
  (+ `warning`, `success`, `accent`), `Rectangle.divider`.
- Buttons: `primary` (the accent; one per screen), `destructive`, `danger` (red text), `ghost`, `link`,
  `icon` (32x32, put a codepoint in Content), `nav` (rail).
- Brushes: `Surface0..4Brush`, `SurfaceHoverBrush`, `BorderSubtleBrush`, `BorderBrush`,
  `BorderStrongBrush`, `TextPrimary/Secondary/Tertiary/DisabledBrush`, `AccentBrush`, `AccentSoftBrush`,
  `AccentTextBrush` (accent readable as text), `DangerBrush`, `SuccessBrush`, `WarningBrush`, `InfoBrush`.
- Icons in `SlateIcons.ttf` (codepoint: meaning): E700 menu, E710 add, E711 close, E712 more, E713
  settings, E716 people, E718 pin, E71C filter, E721 search, E72B back, E72C refresh, E72D share, E734
  star, E73E check, E74D delete, E74E save, E753 cloud, E756 console, E768 play, E769 pause, E76B/E76C
  chevrons, E774 globe, E77B person, E783 error, E7BA warning, E7C3 page/instance, E81C history, E838
  open folder, E895 sync, E896 download, E897 help, E898 upload, E8A5 document, E8A7 open in new, E8AC
  rename, E8B7 folder, E8B9 picture, E8BB close, E8C8 copy, E8CB sort, E8D7 lock, E8EC tag, E8F1
  library, E8F4 new folder, E8FD list, E909 world, E943 code, E946 info, EC4A puzzle (mods).

## Building and checking

Build at idle priority, with two nodes and no shared compiler server, so a build never gets in the way of anything else running:

    dotnet build CloudLauncher.Linux/CloudLauncher.Linux.csproj -nologo -v q -m:2 -p:UseSharedCompilation=false --artifacts-path <your own folder>

(start it with idle priority, e.g. through `Start-Process` and `PriorityClass = 'Idle'`).

Never run the launcher with a window and never start Minecraft. Look at pages with the headless
harness instead; it renders the real window to PNG against the showcase fake server:

    tools/linux-shots/shots.ps1 -Out <folder> -Scenarios shell,instances -Port 5091 -Profile showcase-a

Each area adds its scenarios to its own file (`tools/linux-shots/Shots*.cs`). Use your own `-Out`,
`-Port` and `-Profile` so runs can go side by side. The fake account is "mara" with six instances
(`tools/smoke/showcase/FakeWorld.cs`); the stores are not reachable from it (CurseForge points at a dead
port), so store pages show their error states unless a scenario feeds them data.

## Releases

`tools/release/build-linux.ps1` publishes a self-contained single file for linux-x64 and packs
`CloudLauncher-<version>-linux-x64.tar.gz` (the launcher, `install.sh`, the icon, the licence). The
server serves it at `/download/linux`, and the launcher updates itself from `/launcher/linux/latest`,
signed like the Windows packages (`UpdateVerifier`).
