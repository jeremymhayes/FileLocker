#ifndef AppVersion
#define AppVersion "1.3.0.0"
#endif

#ifndef PublishDir
#define PublishDir "..\..\artifacts\inno\publish"
#endif

#ifndef OutputDir
#define OutputDir "..\..\artifacts\inno"
#endif

#ifndef SourceRoot
#define SourceRoot "..\.."
#endif

#define AppName "FileLocker"
#define AppPublisher "Jeremy Hayes"
#define AppExeName "FileLocker.exe"

[Setup]
AppId={{04C72652-51B0-4214-8ED0-F43E6751E7AE}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/jeremymhayes/FileLocker
AppSupportURL=https://github.com/jeremymhayes/FileLocker/issues
AppUpdatesURL=https://github.com/jeremymhayes/FileLocker/releases/latest
DefaultDirName={autopf}\FileLocker
DefaultGroupName={#AppName}
DisableProgramGroupPage=no
LicenseFile={#SourceRoot}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=FileLocker-Setup-{#AppVersion}
SetupIconFile={#SourceRoot}\FileLocker\logo.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "startmenuicon"; Description: "Create a Start Menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
