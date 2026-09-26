; ============================================================================
;  CloudLauncher web installer (Inno Setup 6)
;
;  A small setup .exe that never changes. It downloads the current full installer
;  (CloudLauncher-Setup-<version>.exe, built from CloudLauncher.iss) from
;  cloudlauncher.co, checks it against the SHA-256 the release feed publishes, and
;  runs it silently. Windows SmartScreen keeps reputation per file hash, so a file
;  that is the same for every release keeps what earlier releases earned instead of
;  starting from zero each time the full installer is rebuilt.
;
;  Nothing in here may depend on a release: no version macro, no changelog, no file
;  from a publish. Build it once, by hand:
;    ISCC.exe installer\CloudLauncher-Web.iss
;  then upload artifacts\web\CloudLauncher-Setup.exe to the launcher root as
;  web-installer.bin (see README-web-installer.md). Rebuild only when this script
;  or license-terms.txt changes, and expect the reputation to start again.
;
;  What it does, in order, all inside PrepareToInstall so that a failure aborts
;  cleanly before anything is touched:
;    1. GET /launcher/latest and read version, installerSize and installerSha256
;       with plain string functions (there is no JSON library in Inno Setup).
;    2. Download the installer, trying the versioned route first and falling back
;       to the plain ones, with RequiredSHA256OfFile set so Inno Setup refuses a
;       file that is not the one the feed describes.
;    3. Run it with /SP- /SILENT (or /VERYSILENT when this setup is very silent)
;       /NORESTART /SUPPRESSMSGBOXES, passing through a /DIR= given to this setup.
;  The full installer does the actual install (per-user, no elevation). This setup
;  installs nothing, creates no uninstall entry and no folder of its own.
; ============================================================================

; Where the feed and the installer are fetched from. Overridable with /DBaseUrl=
; to exercise the failure path against a dead address; a release build never
; overrides it.
#ifndef BaseUrl
  #define BaseUrl "https://cloudlauncher.co"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\web"
#endif
#ifndef AppIcon
  #define AppIcon "..\CloudLauncher\Assets\appicon.ico"
#endif

#define MyAppName "CloudLauncher"
#define MyAppExeName "CloudLauncher.exe"
#define MyAppPublisher "falling_colud"
#define MyAppURL "https://cloudlauncher.co"
; The stub's own version, stamped into the file. It is not a launcher version and
; stays at 1.0 unless the stub is deliberately rebuilt.
#define StubVersion "1.0"

[Setup]
; Its own AppId, distinct from the launcher's (CloudLauncher.iss), so nothing here
; is ever mistaken for the real install.
AppId={{5D0C2E4B-8F0A-4B6E-9C2B-3A1F7E6D9B41}
AppName={#MyAppName}
AppVersion={#StubVersion}
; The wizard title and welcome text carry no version: what gets installed is
; whatever the feed says today.
AppVerName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
AppCopyright=Copyright (c) 2026 {#MyAppPublisher}
VersionInfoVersion=1.0.0.0
VersionInfoProductVersion=1.0.0.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright=Copyright (c) 2026 {#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup (web installer)
VersionInfoProductName={#MyAppName}

; --- installs nothing itself: no folder, no uninstaller, no Start Menu group ---
PrivilegesRequired=lowest
CreateAppDir=no
Uninstallable=no
DisableProgramGroupPage=yes

; --- appearance / metadata, as in CloudLauncher.iss ---
WizardStyle=modern
SetupIconFile={#AppIcon}
ShowLanguageDialog=no
LicenseFile=license-terms.txt
MinVersion=10.0.17763

; --- output: no version in the name, the file is the same for every release ---
OutputDir={#OutputDir}
OutputBaseFilename=CloudLauncher-Setup
Compression=lzma2/max
SolidCompression=yes

; --- 64-bit app; block on x86-only Windows, as the full installer does ---
ArchitecturesAllowed=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Run]
; Offered on the finish page only when the full installer left the exe where it says it did.
Filename: "{code:InstalledExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent; Check: InstalledExeExists

[Code]
const
  FeedUrl = '{#BaseUrl}/launcher/latest';
  FullInstallerUrl = '{#BaseUrl}/launcher/installer';
  // The launcher's own AppId (CloudLauncher.iss): the full installer records where
  // it installed under this key.
  LauncherUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{36177AA6-424D-4067-BC72-75848D92B481}_is1';

var
  DownloadPage: TDownloadWizardPage;

// ---------------------------------------------------------------- JSON reading
//
// The feed is one flat JSON object with double-quoted keys. A key is matched only
// when its opening quote is not escaped and a colon follows, so a key name inside
// the notes text (which would be "\"version\"") is skipped.

// The 1-based position of the opening quote of an unescaped "Key", searching from
// StartAt, or 0.
function FindKey(const Json, Key: String; StartAt: Integer): Integer;
var
  Needle: String;
  Offset, P: Integer;
begin
  Result := 0;
  Needle := '"' + Key + '"';
  Offset := StartAt;
  while Offset <= Length(Json) do
  begin
    P := Pos(Needle, Copy(Json, Offset, Length(Json) - Offset + 1));
    if P = 0 then Exit;
    P := P + Offset - 1;
    if (P = 1) or (Json[P - 1] <> '\') then
    begin
      Result := P;
      Exit;
    end;
    Offset := P + 1;
  end;
end;

function IsSpace(C: Char): Boolean;
begin
  Result := (C = ' ') or (C = #9) or (C = #13) or (C = #10);
end;

// The raw text of Key's value: the contents of a plain string (one without escape
// sequences, which the values read here never have), or the digits of a number.
function JsonValue(const Json, Key: String; var Value: String): Boolean;
var
  P, Start: Integer;
begin
  Result := False;
  Value := '';
  P := FindKey(Json, Key, 1);
  while P > 0 do
  begin
    P := P + Length(Key) + 2;
    while (P <= Length(Json)) and IsSpace(Json[P]) do P := P + 1;
    if (P <= Length(Json)) and (Json[P] = ':') then
    begin
      P := P + 1;
      while (P <= Length(Json)) and IsSpace(Json[P]) do P := P + 1;
      if P > Length(Json) then Exit;
      if Json[P] = '"' then
      begin
        Start := P + 1;
        P := Start;
        while (P <= Length(Json)) and (Json[P] <> '"') do
        begin
          if Json[P] = '\' then Exit;
          P := P + 1;
        end;
        if P > Length(Json) then Exit;
        Value := Copy(Json, Start, P - Start);
        Result := True;
      end
      else
      begin
        Start := P;
        while (P <= Length(Json)) and (Json[P] >= '0') and (Json[P] <= '9') do P := P + 1;
        Value := Copy(Json, Start, P - Start);
        Result := Value <> '';
      end;
      Exit;
    end;
    // A quoted "Key" with no colon after it was a string value, not a key.
    P := FindKey(Json, Key, P);
  end;
end;

// ---------------------------------------------------------------- checks

// Digits and dots in the shape 1.2, 1.2.3 or 1.2.3.4: the only form that goes
// into a URL or on screen.
function IsVersion(const S: String): Boolean;
var
  I, Dots: Integer;
  AfterDot: Boolean;
begin
  Result := False;
  if (S = '') or (S[1] = '.') or (S[Length(S)] = '.') then Exit;
  Dots := 0;
  AfterDot := False;
  for I := 1 to Length(S) do
  begin
    if S[I] = '.' then
    begin
      if AfterDot then Exit;
      Dots := Dots + 1;
      AfterDot := True;
    end
    else if (S[I] >= '0') and (S[I] <= '9') then
      AfterDot := False
    else
      Exit;
  end;
  Result := (Dots >= 1) and (Dots <= 3);
end;

function IsSha256Hex(const S: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  if Length(S) <> 64 then Exit;
  for I := 1 to 64 do
    if not (((S[I] >= '0') and (S[I] <= '9')) or ((S[I] >= 'a') and (S[I] <= 'f')) or ((S[I] >= 'A') and (S[I] <= 'F'))) then
      Exit;
  Result := True;
end;

// "52.6 MB", with integer arithmetic.
function SizeText(Bytes: Int64): String;
var
  Tenths: Int64;
begin
  Tenths := (Bytes * 10 + 524288) div 1048576;
  Result := IntToStr(Tenths div 10) + '.' + IntToStr(Tenths mod 10) + ' MB';
end;

function IsVerySilent: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/VERYSILENT') = 0 then
      Result := True;
end;

// ---------------------------------------------------------------- the steps

// Reads version, installer size and installer SHA-256 from the feed.
function ReadFeed(var Version, Sha256: String; var Size: Int64; var Error: String): Boolean;
var
  Raw: AnsiString;
  Json, SizeValue: String;
begin
  Result := False;
  try
    DownloadTemporaryFile(FeedUrl, 'latest.json', '', nil);
  except
    Error := 'The release information could not be fetched from ' + FeedUrl + ' (' + GetExceptionMessage + ').';
    Exit;
  end;
  if not LoadStringFromFile(ExpandConstant('{tmp}\latest.json'), Raw) then
  begin
    Error := 'The release information could not be read.';
    Exit;
  end;
  Json := String(Raw);
  if not JsonValue(Json, 'version', Version) or not IsVersion(Version) then
  begin
    Error := 'The release information has no usable version number.';
    Exit;
  end;
  if not JsonValue(Json, 'installerSha256', Sha256) or not IsSha256Hex(Sha256) then
  begin
    Error := 'The release information has no installer checksum.';
    Exit;
  end;
  Sha256 := Lowercase(Sha256);
  if not JsonValue(Json, 'installerSize', SizeValue) then
    SizeValue := '0';
  Size := StrToInt64Def(SizeValue, 0);
  if Size <= 0 then
  begin
    Error := 'The release information has no installer size.';
    Exit;
  end;
  Result := True;
end;

// Downloads the installer to {tmp}, trying each address in turn until one gives
// the file with the published SHA-256. Error is 'cancelled' when the user did that.
function DownloadInstaller(const Version, Sha256: String; Size: Int64; var Path, Error: String): Boolean;
var
  Urls: array of String;
  BaseName: String;
  I: Integer;
begin
  Result := False;
  BaseName := 'CloudLauncher-Setup-' + Version + '.exe';
  Path := ExpandConstant('{tmp}\') + BaseName;
  SetArrayLength(Urls, 4);
  // Versioned and immutable: either this version's installer or a 404.
  Urls[0] := FullInstallerUrl + '/' + Version;
  Urls[1] := FullInstallerUrl;
  Urls[2] := '{#BaseUrl}/download/full';
  // The website's button. Once the server serves this stub there the checksum will
  // not match and the attempt fails, which is fine: it is the last resort.
  Urls[3] := '{#BaseUrl}/download';

  DownloadPage.Caption := 'Downloading CloudLauncher ' + Version + ' (' + SizeText(Size) + ')';
  DownloadPage.Description := 'The file is checked against the published SHA-256 before it runs.';
  for I := 0 to GetArrayLength(Urls) - 1 do
  begin
    DownloadPage.Clear;
    DownloadPage.Add(Urls[I], BaseName, Sha256);
    try
      DownloadPage.Download;
      Log('Downloaded ' + Urls[I]);
      Result := True;
      Exit;
    except
      if DownloadPage.AbortedByUser then
      begin
        Log('Download cancelled by the user.');
        Error := 'cancelled';
        Exit;
      end;
      Log('Download from ' + Urls[I] + ' failed: ' + GetExceptionMessage);
      Error := 'The installer could not be downloaded (' + GetExceptionMessage + ').';
    end;
  end;
end;

// Logs the reason, tells the user in a message box, and returns the text the
// Preparing page shows while Setup stops.
function Failed(const Reason: String): String;
begin
  Log('Web installer failed: ' + Reason);
  Result := Reason + #13#10#13#10 + 'You can download the full installer instead from ' + FullInstallerUrl;
  SuppressibleMsgBox('CloudLauncher could not be installed.' + #13#10#13#10 + Result, mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Version, Sha256, Error, Installer, Params, Dir, LogPath: String;
  Size: Int64;
  ResultCode: Integer;
begin
  Result := '';
  DownloadPage.Show;
  try
    if not ReadFeed(Version, Sha256, Size, Error) then
    begin
      Result := Failed(Error);
      Exit;
    end;
    Log('Current release ' + Version + ', installer ' + IntToStr(Size) + ' bytes, sha256 ' + Sha256);

    if not DownloadInstaller(Version, Sha256, Size, Installer, Error) then
    begin
      if Error = 'cancelled' then
        Result := 'The download was cancelled.'
      else
        Result := Failed(Error);
      Exit;
    end;

    DownloadPage.Caption := 'Installing CloudLauncher ' + Version;
    DownloadPage.Description := 'The installer is running. This takes a moment.';
    DownloadPage.SetText('Installing CloudLauncher ' + Version + '...', '');
    DownloadPage.ProgressBar.Style := npbstMarquee;
    DownloadPage.ProgressBar.Visible := True;

    // Silent to the same degree as this setup, so a very silent run shows nothing.
    if IsVerySilent then
      Params := '/SP- /VERYSILENT /NORESTART /SUPPRESSMSGBOXES'
    else
      Params := '/SP- /SILENT /NORESTART /SUPPRESSMSGBOXES';
    Dir := ExpandConstant('{param:DIR|}');
    if Dir <> '' then
      Params := Params + ' /DIR=' + AddQuotes(Dir);
    // Its log outlives {tmp}, which is removed when this setup exits.
    LogPath := GetTempDir + 'CloudLauncher-Setup-' + Version + '.log';
    Params := Params + ' /LOG=' + AddQuotes(LogPath);
    Log('Running ' + Installer + ' ' + Params);

    if not Exec(Installer, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      Result := Failed('The installer could not be started (' + SysErrorMessage(ResultCode) + ').');
      Exit;
    end;
    if ResultCode <> 0 then
    begin
      Result := Failed('The installer stopped with exit code ' + IntToStr(ResultCode) + '. Its log is at ' + LogPath + '.');
      Exit;
    end;
    Log('Installed CloudLauncher ' + Version);
  finally
    DownloadPage.Hide;
  end;
end;

// ---------------------------------------------------------------- wizard

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Downloading CloudLauncher',
    'Please wait while Setup fetches the current release from cloudlauncher.co.', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := 'Setup will download the current CloudLauncher release from cloudlauncher.co and install it for your user account, with no admin prompt.' + NewLine + NewLine +
    'The download is checked against the release''s published SHA-256 before it runs.';
end;

// Where the full installer put CloudLauncher: what it recorded, else the /DIR
// passed through to it, else its default per-user folder.
function InstalledExe(Param: String): String;
var
  Dir: String;
begin
  if not RegQueryStringValue(HKCU, LauncherUninstallKey, 'InstallLocation', Dir) or (Dir = '') then
  begin
    Dir := ExpandConstant('{param:DIR|}');
    if Dir = '' then
      Dir := ExpandConstant('{localappdata}\Programs\{#MyAppName}');
  end;
  Result := AddBackslash(Dir) + '{#MyAppExeName}';
end;

function InstalledExeExists: Boolean;
begin
  Result := FileExists(InstalledExe(''));
end;
