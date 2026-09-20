; ============================================================================
;  CloudLauncher installer (Inno Setup 6)
;
;  Per-USER install to %LocalAppData%\Programs\CloudLauncher. This is deliberate:
;  the in-app self-updater (UpdateService) overwrites files in the install
;  directory WITHOUT elevating, so the app must live somewhere the user can write.
;  A Program Files install would break every self-update. This also means no UAC
;  prompt on install, matching how VS Code / Discord / Slack install themselves.
;
;  Build this with build-installer.ps1, which publishes the client and passes the
;  version + source paths via /D defines. It can also be opened directly in the
;  Inno Setup IDE once a publish exists at the default SourceDir below.
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "1.0.1"
#endif
; Folder containing the published win-x64 self-contained build (CloudLauncher.exe + deps).
#ifndef SourceDir
  #define SourceDir "..\artifacts\installer-build"
#endif
; Where the finished setup .exe is written.
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
#ifndef AppIcon
  #define AppIcon "..\CloudLauncher\Assets\appicon.ico"
#endif

#define MyAppName "CloudLauncher"
#define MyAppExeName "CloudLauncher.exe"
#define MyAppPublisher "CloudLauncher"

[Setup]
; AppId uniquely identifies the app for upgrades/uninstall. NEVER change it once shipped.
AppId={{36177AA6-424D-4067-BC72-75848D92B481}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}

; --- per-user install, no elevation ---
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; --- appearance / metadata ---
WizardStyle=modern
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
ShowLanguageDialog=no

; --- output ---
OutputDir={#OutputDir}
OutputBaseFilename=CloudLauncher-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes

; --- 64-bit app; block install on x86-only Windows ---
ArchitecturesAllowed=x64compatible

; Close a running CloudLauncher (via the Restart Manager) so its files aren't locked
; during install/upgrade, then don't auto-restart it — [Run] handles relaunch.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; ignoreversion so every file is replaced on upgrade regardless of embedded file versions
; (the self-contained runtime DLLs share versions across builds).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The self-updater merges new builds over {app} without pruning removed files, so after a
; few self-updates the folder may hold files this installer never placed. Remove the whole
; folder on uninstall. User data (packs, settings, accounts) lives under %AppData%\CloudLauncher
; and %LocalAppData%\CloudLauncher — NOT here — so this does not touch it.
Type: filesandordirs; Name: "{app}"
