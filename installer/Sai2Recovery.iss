; Inno Setup script for SAI2 Recovery.
; Built by CI:  ISCC /DAppVersion=1.0.0 /DPublishDir=..\publish installer\Sai2Recovery.iss
; publish\app  - SAI2Recovery.exe (self-contained, single file)
; publish\cli  - sai2fix.exe      (self-contained, single file)

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define AppName "SAI2 Recovery"
#define AppExe "SAI2Recovery.exe"

[Setup]
AppId={{6C1F4E2A-9B7D-4C51-A0E3-5A2D8F7B3C19}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=MakarovMM96
AppPublisherURL=https://github.com/MakarovMM96/TestProject
AppSupportURL=https://github.com/MakarovMM96/TestProject/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=SAI2Recovery-Setup-{#AppVersion}
SetupIconFile=..\src\Sai2Recovery.App\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Установщик {#AppName}

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.ContextMenu=Добавить в меню проводника «Восстановить в SAI2 Recovery» для файлов .sai2
en.ContextMenu=Add "Repair with SAI2 Recovery" to the Explorer menu for .sai2 files
ru.AddToPath=Добавить консольную утилиту sai2fix в PATH
en.AddToPath=Add the sai2fix command-line tool to PATH
ru.VerbText=Восстановить в SAI2 Recovery
en.VerbText=Repair with SAI2 Recovery
ru.LaunchApp=Запустить {#AppName}
en.LaunchApp=Launch {#AppName}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "contextmenu"; Description: "{cm:ContextMenu}"
Name: "addtopath"; Description: "{cm:AddToPath}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\cli\sai2fix.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Explorer context menu for .sai2 files (does not change the default program for .sai2)
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.sai2\shell\SAI2Recovery"; ValueType: string; ValueName: ""; ValueData: "{cm:VerbText}"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.sai2\shell\SAI2Recovery"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.sai2\shell\SAI2Recovery\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
; Allow "Open with" without taking over the association
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".sai2"; ValueData: ""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
const
  EnvKeyUser = 'Environment';
  EnvKeyMachine = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function EnvRoot: Integer;
begin
  if IsAdminInstallMode then Result := HKEY_LOCAL_MACHINE else Result := HKEY_CURRENT_USER;
end;

function EnvKey: String;
begin
  if IsAdminInstallMode then Result := EnvKeyMachine else Result := EnvKeyUser;
end;

procedure AddToPath(Dir: String);
var
  Paths: String;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then Paths := '';
  if Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0 then exit;
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then Paths := Paths + ';';
  RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Paths + Dir);
end;

procedure RemoveFromPath(Dir: String);
var
  Paths: String;
  P: Integer;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then exit;
  P := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';');
  if P = 0 then exit;
  if P = 1 then
    Delete(Paths, 1, Length(Dir))
  else
    Delete(Paths, P - 1, Length(Dir) + 1);
  if (Length(Paths) > 0) and (Copy(Paths, 1, 1) = ';') then Delete(Paths, 1, 1);
  RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;
