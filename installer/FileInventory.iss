#ifndef PublishDir
  #error Supply /DPublishDir= followed by the absolute path of the win-x64 publish folder.
#endif
#ifndef BuildOutput
  #define BuildOutput "output"
#endif
#ifndef BuildVersion
  #define BuildVersion "1.0.0"
#endif

[Setup]
AppId={{C79DF90C-B339-4190-8A4C-BC3E619780E2}
AppName=File Inventory
AppVersion={#BuildVersion}
AppPublisher=File Inventory
DefaultDirName={localappdata}\Programs\File Inventory
DefaultGroupName=File Inventory
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#BuildOutput}
OutputBaseFilename=FileInventory_Setup_x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\FileInventory.exe
UninstallDisplayName=File Inventory
VersionInfoVersion={#BuildVersion}.0
VersionInfoDescription=File Inventory Setup

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\File Inventory"; Filename: "{app}\FileInventory.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\FileInventory.exe"; Description: "Open File Inventory"; Flags: nowait postinstall skipifsilent
