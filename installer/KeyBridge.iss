#define MyAppName "KeyBridge"
#define MyAppVersion "0.2.5"
#define MyAppPublisher "KeyBridge"
#define MyAppExeName "KeyBridge.exe"
#define PublishDir "..\artifacts\publish\KeyBridge-win-x64"

[Setup]
AppId={{6FB0D551-7532-4F1D-B8C0-A0165F410D8E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Taxperia/keyboard
AppSupportURL=https://github.com/Taxperia/keyboard/issues
AppUpdatesURL=https://github.com/Taxperia/keyboard/releases
DefaultDirName={localappdata}\Programs\KeyBridge
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=KeyBridgeSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
LicenseFile=..\LICENSE
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstü kısayolu oluştur"; GroupDescription: "Ek kısayollar:"

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\Assets\FONT-AWESOME-LICENSE.txt"; DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "{#PublishDir}\setup-private-network.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} başlat"; Flags: nowait postinstall skipifsilent
