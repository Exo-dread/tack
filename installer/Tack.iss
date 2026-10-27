; Tack for Windows: the installer (Inno Setup 6, https://jrsoftware.org/isinfo.php).
;
; Built by build.ps1 from the same self-contained Tack.exe as the portable zip, so the installed
; Tack is that exe, nothing more. It keeps its data in %LOCALAPPDATA%\Tack exactly like the zip
; (there's no TackData folder next to an installed Tack.exe), so switching between the zip and the
; installer, upgrading or uninstalling never touches anyone's tasks.
;
;   ISCC installer\Tack.iss /DVersion=1.8.0 /DRuntime=win-x64 /DSourceExe=<publish>\Tack.exe /DOutputDir=artifacts
;
; /DTestBuild=<name> builds a copy for trying the installer on a PC that already runs Tack: its
; own AppId, name and uninstall entry, so it can never upgrade or uninstall the real install. (Closing
; a running Tack is safe either way: it only ever closes the Tack.exe inside the install folder.)
;
; Install modes: Program Files for everyone (administrator), or just for me in
; %LOCALAPPDATA%\Programs\Tack, chosen in the first dialog or with /ALLUSERS or /CURRENTUSER.

#ifndef Version
  #error Pass the version: /DVersion=x.y.z
#endif
#ifndef Runtime
  #define Runtime "win-x64"
#endif
#ifndef SourceExe
  #error Pass the published Tack.exe: /DSourceExe=<path>
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#if Runtime == "win-x64"
  #define Arch "x64compatible"
  #define OutputName "Tack-" + Version + "-setup"
#elif Runtime == "win-arm64"
  #define Arch "arm64"
  #define OutputName "Tack-" + Version + "-arm64-setup"
#else
  #error Runtime must be win-x64 or win-arm64
#endif

#ifdef TestBuild
  #define AppIdValue "{{125AD659-8D26-4921-B390-2D4E48F99750}-test-" + TestBuild
  #define AppNameValue "Tack (installer test " + TestBuild + ")"
  #define OutputName OutputName + "-test"
#else
  ; Never change this: it's how an upgrade finds the installed Tack.
  #define AppIdValue "{{125AD659-8D26-4921-B390-2D4E48F99750}"
  #define AppNameValue "Tack"
#endif

[Setup]
AppId={#AppIdValue}
AppName={#AppNameValue}
AppVersion={#Version}
AppVerName={#AppNameValue} {#Version}
AppPublisher=Exo-dread
AppPublisherURL=https://github.com/Exo-dread/tack
AppSupportURL=https://github.com/Exo-dread/tack/issues
AppUpdatesURL=https://github.com/Exo-dread/tack/releases
AppCopyright=Copyright (c) 2026 Tack contributors. MIT License.
VersionInfoVersion={#Version}
VersionInfoDescription=Tack setup

; Program Files by default; "Install for me only" puts it in %LOCALAPPDATA%\Programs\Tack instead.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\Tack
DisableProgramGroupPage=yes
DisableReadyPage=yes
UsePreviousAppDir=yes
ArchitecturesAllowed={#Arch}
ArchitecturesInstallIn64BitMode={#Arch}
MinVersion=10.0

; An upgrade closes the installed Tack through Restart Manager, which only ever looks at files in
; the install folder: a Tack running from anywhere else (a portable copy) is left alone. Tack saves
; when Windows asks it to close. Restart Manager can't restart it (Tack doesn't register for that),
; so [Run] below starts it again.
CloseApplications=yes
RestartApplications=no

SetupIconFile=..\src\Tack\Assets\Tack.ico
UninstallDisplayIcon={app}\Tack.exe
UninstallDisplayName={#AppNameValue}
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "Tack.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppNameValue}"; Filename: "{app}\Tack.exe"
Name: "{autodesktop}\{#AppNameValue}"; Filename: "{app}\Tack.exe"; Tasks: desktopicon

[Run]
; The finish page's "Start Tack" (as the signed-in user, never as administrator).
Filename: "{app}\Tack.exe"; Description: "{cm:LaunchProgram,Tack}"; Flags: nowait postinstall skipifsilent
; A silent upgrade (winget, say) starts Tack again only if it closed a running one.
Filename: "{app}\Tack.exe"; Flags: nowait runasoriginaluser; Check: RelaunchAfterSilentUpgrade

[Code]
const
  GENERIC_WRITE = $40000000;
  OPEN_EXISTING = 3;
  INVALID_HANDLE = $FFFFFFFF;
  ERROR_SHARING_VIOLATION = 32;
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  TackWasRunning: Boolean;

function CreateFile(FileName: String; DesiredAccess, ShareMode, SecurityAttributes, CreationDisposition,
  FlagsAndAttributes, TemplateFile: Cardinal): Cardinal;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: Cardinal): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

{ Restart Manager, for the uninstaller (Setup has its own through CloseApplications). }
function RmStartSession(out SessionHandle: Cardinal; SessionFlags: Cardinal; SessionKey: String): Cardinal;
  external 'RmStartSession@rstrtmgr.dll stdcall delayload uninstallonly';
{ FileNames is a one-element array of strings: a String variable passed by reference is exactly that. }
function RmRegisterResources(SessionHandle: Cardinal; FileCount: Cardinal; var FileNames: String;
  AppCount: Cardinal; Apps: Cardinal; ServiceCount: Cardinal; Services: Cardinal): Cardinal;
  external 'RmRegisterResources@rstrtmgr.dll stdcall delayload uninstallonly';
function RmShutdown(SessionHandle: Cardinal; ActionFlags: Cardinal; StatusCallback: Cardinal): Cardinal;
  external 'RmShutdown@rstrtmgr.dll stdcall delayload uninstallonly';
function RmEndSession(SessionHandle: Cardinal): Cardinal;
  external 'RmEndSession@rstrtmgr.dll stdcall delayload uninstallonly';

{ True while a process has this exe open: Windows won't let anyone open a running image for writing. }
function IsFileInUse(const Path: String): Boolean;
var
  Handle: Cardinal;
begin
  Result := False;
  if not FileExists(Path) then Exit;
  Handle := CreateFile(Path, GENERIC_WRITE, 0, 0, OPEN_EXISTING, 0, 0);
  if Handle = INVALID_HANDLE then
    Result := DLLGetLastError = ERROR_SHARING_VIOLATION
  else
    CloseHandle(Handle);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Called before Restart Manager closes anything: remember whether this install's Tack was running. }
  TackWasRunning := IsFileInUse(ExpandConstant('{app}\Tack.exe'));
  Result := '';
end;

function RelaunchAfterSilentUpgrade: Boolean;
begin
  Result := WizardSilent and TackWasRunning;
end;

{ Asks the Tack.exe in this install folder (and nothing else) to close, the way Windows does at sign-out. }
function CloseInstalledTack(const Exe: String): Boolean;
var
  Session, Status: Cardinal;
  Key, FileName: String;
begin
  Result := True;
  if not IsFileInUse(Exe) then Exit;
  Key := StringOfChar(' ', 64);
  if RmStartSession(Session, 0, Key) <> 0 then begin
    Result := False;
    Exit;
  end;
  try
    FileName := Exe;
    Status := RmRegisterResources(Session, 1, FileName, 0, 0, 0, 0);
    if Status = 0 then Status := RmShutdown(Session, 0, 0);
    Log(Format('Restart Manager closing %s: %d', [Exe, Status]));
  finally
    RmEndSession(Session);
  end;
  Result := not IsFileInUse(Exe);
end;

{ The per-user startup entry Tack writes is "<path to Tack.exe>" --startup. }
function RunValuePointsAt(const Value, Exe: String): Boolean;
var
  Path: String;
  Closing: Integer;
begin
  Path := Trim(Value);
  if (Length(Path) > 0) and (Path[1] = '"') then begin
    Delete(Path, 1, 1);
    Closing := Pos('"', Path);
    if Closing = 0 then begin
      Result := False;
      Exit;
    end;
    Path := Copy(Path, 1, Closing - 1);
  end;
  Result := CompareText(Path, Exe) = 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Exe, Value: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Exe := ExpandConstant('{app}\Tack.exe');

  while not CloseInstalledTack(Exe) do begin
    if UninstallSilent then Break; { Windows deletes the exe after the next restart instead. }
    if MsgBox('Tack is still running. Quit it (right-click the orb, then Quit) and click OK.',
      mbInformation, MB_OKCANCEL) = IDCANCEL then Break;
  end;

  { "Start with Windows": remove it only if it starts this Tack, never a portable copy's. }
  if RegQueryStringValue(HKCU, RunKey, 'Tack', Value) and RunValuePointsAt(Value, Exe) then
    RegDeleteValue(HKCU, RunKey, 'Tack');
  { %LOCALAPPDATA%\Tack (tasks, notes, settings) stays: reinstalling picks up where you left off. }
end;
