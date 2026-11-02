; SlideForge Inno Setup Installer Script
#define MyAppName "SlideForge"
#define MyAppVersion "1.7.5.5"
#define MyAppPublisher "exarobibliologist"
#define MyAppExeName "SlideForge.exe"
; Point this to your Visual Studio Publish folder:
#define PublishDir "bin\Release\net8.0-windows"

[Setup]
AppId={{8F4E9A12-5C3B-4D8E-9A1F-7B2E6D4C8A90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Uses SlideForge.ico for the Setup.exe installer and Add/Remove Programs:
SetupIconFile=SlideForge.ico
; Installs into C:\Program Files\SlideForge
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=Installer
OutputBaseFilename=SlideForge_Setup_{#MyAppVersion}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "explorermenu"; Description: "Add 'Open Folder in SlideForge...' to Windows Explorer right-click menu"; GroupDescription: "Windows Explorer Integration:"

[Files]
; Automatically bundles SlideForge.exe, all DLLs, and the entire libvlc codec tree
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 1. Right-click menu when clicking ON a folder in Windows Explorer
Root: HKCR; Subkey: "Directory\shell\SlideForge"; ValueType: string; ValueName: ""; ValueData: "Open Folder in SlideForge..."; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKCR; Subkey: "Directory\shell\SlideForge"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: explorermenu
Root: HKCR; Subkey: "Directory\shell\SlideForge\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: explorermenu

; 2. Right-click menu when clicking the EMPTY BACKGROUND inside an open folder
Root: HKCR; Subkey: "Directory\Background\shell\SlideForge"; ValueType: string; ValueName: ""; ValueData: "Open Folder in SlideForge..."; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKCR; Subkey: "Directory\Background\shell\SlideForge"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: explorermenu
Root: HKCR; Subkey: "Directory\Background\shell\SlideForge\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%V"""; Tasks: explorermenu

; 3. Register .sfplay.json Playlist File Association
Root: HKCR; Subkey: ".sfplay"; ValueType: string; ValueName: ""; ValueData: "SlideForge.Playlist"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SlideForge.Playlist"; ValueType: string; ValueName: ""; ValueData: "SlideForge Playlist"; Flags: uninsdeletekey
Root: HKCR; Subkey: "SlideForge.Playlist\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCR; Subkey: "SlideForge.Playlist\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent