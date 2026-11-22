#ifndef Payload
  #define Payload "..\artifacts\publish"
#endif
#ifndef Artifacts
  #define Artifacts "..\artifacts"
#endif

[Setup]
AppId={{38428A31-ACFF-4C28-9A4F-427613E1F137}
AppName=VPN Remote Desktop
AppVersion=1.0.24
DefaultDirName={localappdata}\Programs\VpnRdpTray
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#Artifacts}
OutputBaseFilename=VpnRdpTray-Setup-x64
SetupIconFile=..\src\VpnRdpTray\app.ico
UninstallDisplayIcon={app}\VpnRdpTray.exe
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
UsePreviousTasks=no

[Tasks]
Name: "autostart"; Description: "Start VPN Remote Desktop at Windows sign-in"

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\VPN Remote Desktop"; Filename: "{app}\VpnRdpTray.exe"; Parameters: "--status"

[Registry]
Root: HKCU; Subkey: "Software\VpnRdpTray"; Flags: uninsdeletekey; ValueType: none
Root: HKCU; Subkey: "Software\VpnRdpTray"; ValueType: dword; ValueName: "AutoStart"; ValueData: "{code:AutoStartValue}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VpnRdpTray"; ValueData: """{app}\VpnRdpTray.exe"""; Flags: uninsdeletevalue; Check: InstallAutoStart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "VpnRdpTray"; Flags: deletevalue uninsdeletevalue; Check: not InstallAutoStart

[Run]
Filename: "{app}\VpnRdpTray.exe"; Description: "Start VPN Remote Desktop"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\shareable-setup.exe"
Type: files; Name: "{localappdata}\VpnRdpTray\setup.dpapi"
Type: files; Name: "{localappdata}\VpnRdpTray\entra.rdp"
Type: files; Name: "{localappdata}\VpnRdpTray\*.tmp"
Type: files; Name: "{localappdata}\VpnRdpTray\OpenVPN\openvpn-*.msi"
Type: dirifempty; Name: "{localappdata}\VpnRdpTray\OpenVPN"
Type: dirifempty; Name: "{localappdata}\VpnRdpTray"

[Code]
var
  AutoStartTaskInitialized: Boolean;

function SavedAutoStart: Boolean;
var
  Value: Cardinal;
begin
  Result := True;
  if RegQueryDWordValue(HKCU, 'Software\VpnRdpTray', 'AutoStart', Value) then
    Result := Value <> 0;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectTasks) and not WizardSilent and not AutoStartTaskInitialized then begin
    AutoStartTaskInitialized := True;
    if SavedAutoStart then
      WizardSelectTasks('autostart')
    else
      WizardSelectTasks('!autostart');
  end;
end;

function InstallAutoStart: Boolean;
begin
  if WizardSilent then
    Result := SavedAutoStart
  else
    Result := WizardIsTaskSelected('autostart');
end;

function AutoStartValue(Param: String): String;
begin
  if InstallAutoStart then
    Result := '1'
  else
    Result := '0';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AppExe: String;
  ExitCode: Integer;
begin
  Result := '';
  AppExe := ExpandConstant('{app}\VpnRdpTray.exe');
  if FileExists(AppExe) then begin
    if not Exec(AppExe, '--shutdown', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
      Result := 'Exit VPN Remote Desktop from the tray, then try installing again.';
  end;
end;

function InitializeUninstall: Boolean;
var
  AppExe: String;
  ExitCode: Integer;
begin
  AppExe := ExpandConstant('{app}\VpnRdpTray.exe');
  Result := not FileExists(AppExe);
  if not Result then
    Result := Exec(AppExe, '--uninstall', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) and (ExitCode = 0);
  if not Result then
    SuppressibleMsgBox('VPN Remote Desktop could not finish cleanup. Try uninstalling again after approving any administrator prompt.',
      mbError, MB_OK, IDOK);
end;
