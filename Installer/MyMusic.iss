#define MyAppName "MyMusic"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "MyMusic"
#define MyAppExeName "MyMusic.exe"

[Setup]
AppId={{E794A20D-9D65-4F3D-BD38-0B257E9A81A7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\MyMusic
DefaultGroupName=MyMusic
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=MyMusic-Setup-v1.0.0
SetupIconFile=..\MyMusic.ico
UninstallDisplayIcon={app}\MyMusic.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MyMusic"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\MyMusic.ico"
Name: "{autodesktop}\MyMusic"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\MyMusic.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch MyMusic"; Flags: nowait postinstall skipifsilent

[Code]
var
  DeleteUserData: Boolean;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataRoot: String;
  LibraryRoot: String;
  LocationFile: String;
  DefaultLibrary: String;
  Choice: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    DeleteUserData := False;
    Choice := MsgBox(
      'Do you also want to delete MyMusic''s library and app data?' + #13#10 + #13#10 +
      'Choose Yes to remove the default managed library, database, settings, and other MyMusic data.' + #13#10 +
      'Choose No to uninstall only the app and keep your music library/data for a future reinstall.' + #13#10 + #13#10 +
      'For safety, a custom library folder outside MyMusic''s AppData folder will NOT be deleted automatically.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
    DeleteUserData := (Choice = IDYES);
  end;

  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
  begin
    DataRoot := ExpandConstant('{userappdata}\MyMusic');
    DefaultLibrary := AddBackslash(DataRoot) + 'Library';
    LocationFile := AddBackslash(DataRoot) + 'library-location.txt';

    { Delete the default managed library. Never recursively delete an arbitrary custom folder. }
    if DirExists(DefaultLibrary) then
      DelTree(DefaultLibrary, True, True, True);

    { Remove database/settings/cache and the remaining MyMusic AppData tree. }
    if DirExists(DataRoot) then
      DelTree(DataRoot, True, True, True);
  end;
end;
