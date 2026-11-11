; The Windows installer of Osiris (Inno Setup 6): the desktop app with a Start menu entry and the
; terminal app on PATH, from packaging/publish.sh win-x64 (artifacts\publish\win-x64).
;   iscc /DAppVersion=1.2.0 packaging\windows\osiris.iss
; Installs per user by default (no administrator needed); the dialog offers "for all users".

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define Source "..\..\artifacts\publish\win-x64"

[Setup]
AppId={{6B0F3C0E-6E0B-4B7B-9C55-0517A0D1E6A1}
AppName=Osiris
AppVersion={#AppVersion}
AppPublisher=k6labs
AppPublisherURL=https://github.com/k6labs-cz/Osiris
AppSupportURL=https://github.com/k6labs-cz/Osiris/issues
DefaultDirName={autopf}\Osiris
DefaultGroupName=Osiris
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesEnvironment=yes
OutputDir=..\..\artifacts
OutputBaseFilename=Osiris-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\desktop\osiris-desktop.exe

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
Name: "path"; Description: "Add the osiris command to PATH"

[Files]
Source: "{#Source}\desktop\*"; DestDir: "{app}\desktop"; Flags: recursesubdirs ignoreversion
Source: "{#Source}\cli\*"; DestDir: "{app}\cli"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Osiris"; Filename: "{app}\desktop\osiris-desktop.exe"
Name: "{autodesktop}\Osiris"; Filename: "{app}\desktop\osiris-desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\desktop\osiris-desktop.exe"; Description: "{cm:LaunchProgram,Osiris}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UserEnvironment = 'Environment';
  MachineEnvironment = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function EnvironmentRoot(): Integer;
begin
  if IsAdminInstallMode() then Result := HKEY_LOCAL_MACHINE else Result := HKEY_CURRENT_USER;
end;

function EnvironmentKey(): String;
begin
  if IsAdminInstallMode() then Result := MachineEnvironment else Result := UserEnvironment;
end;

procedure ChangePath(Dir: String; Add: Boolean);
var
  Path: String;
  At: Integer;
begin
  if not RegQueryStringValue(EnvironmentRoot(), EnvironmentKey(), 'Path', Path) then Path := '';
  At := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';');
  if Add and (At = 0) then
  begin
    if (Path <> '') and (Path[Length(Path)] <> ';') then Path := Path + ';';
    Path := Path + Dir;
  end
  else if (not Add) and (At > 0) then
  begin
    Delete(Path, At, Length(Dir) + 1);
    if (Path <> '') and (Path[Length(Path)] = ';') then Delete(Path, Length(Path), 1);
  end
  else Exit;
  RegWriteExpandStringValue(EnvironmentRoot(), EnvironmentKey(), 'Path', Path);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('path') then ChangePath(ExpandConstant('{app}\cli'), True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then ChangePath(ExpandConstant('{app}\cli'), False);
end;
