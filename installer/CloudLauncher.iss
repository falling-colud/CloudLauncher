; ============================================================================
;  CloudLauncher installer (Inno Setup 6)
;
;  Per-user install to %LocalAppData%\Programs\CloudLauncher: the self-updater
;  (UpdateService) overwrites files in the install directory without elevating,
;  so a Program Files install would break self-updates. It also means there is no
;  UAC prompt on install.
;
;  Build this with build-installer.ps1, which publishes the client and passes the
;  version + source paths via /D defines. It can also be opened directly in the
;  Inno Setup IDE once a publish exists at the default SourceDir below.
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
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
#define MyAppPublisher "falling_colud"
#define MyAppURL "https://cloudlauncher.co"

[Setup]
; AppId identifies the app for upgrades and uninstall. Never change it once shipped.
AppId={{36177AA6-424D-4067-BC72-75848D92B481}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
AppCopyright=Copyright (c) 2026 {#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright=Copyright (c) 2026 {#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}

; --- per-user install, no elevation ---
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; --- appearance / metadata ---
WizardStyle=modern
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
ShowLanguageDialog=no
LicenseFile=license-terms.txt
MinVersion=10.0.17763

; --- output ---
OutputDir={#OutputDir}
OutputBaseFilename=CloudLauncher-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes

; --- 64-bit app; block install on x86-only Windows ---
ArchitecturesAllowed=x64compatible

; Close a running CloudLauncher (via the Restart Manager) so its files aren't locked
; during install/upgrade, then don't auto-restart it: [Run] handles relaunch.
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
; and %LocalAppData%\CloudLauncher, not here, so this does not touch it.
Type: filesandordirs; Name: "{app}"

[Code]
// Always forget the sign-in on this PC; ask before removing instances and settings.
procedure DeleteSignIns(DataDir: String);
var
  Rec: TFindRec;
begin
  if FindFirst(DataDir + '\*', Rec) then
  begin
    try
      repeat
        if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and (Rec.Name <> '.') and (Rec.Name <> '..') then
        begin
          DelTree(DataDir + '\' + Rec.Name + '\secrets', True, True, True);
          DeleteFile(DataDir + '\' + Rec.Name + '\minecraft-microsoft-accounts.json');
        end;
      until not FindNext(Rec);
    finally
      FindClose(Rec);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, LocalDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{userappdata}\CloudLauncher');
  LocalDir := ExpandConstant('{localappdata}\CloudLauncher');
  DelTree(GetEnv('TEMP') + '\CloudLauncherUpdate', True, True, True);
  if not DirExists(DataDir) and not DirExists(LocalDir) then Exit;
  if SuppressibleMsgBox('Also delete your CloudLauncher data on this PC?' + #13#10#13#10 +
       'This removes your local instances, worlds, settings and cached files. ' +
       'Anything stored in your CloudLauncher account stays there.',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
  begin
    DelTree(DataDir, True, True, True);
    DelTree(LocalDir, True, True, True);
  end
  else
    DeleteSignIns(DataDir);
end;
