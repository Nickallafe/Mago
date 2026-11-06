; Instalador do Mago da Performance.
;
; Compile com build-installer.ps1 (que corre o dotnet publish antes) ou direto:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyVersion=0.2.0 MagoSetup.iss
;
; Entradas:
;   MyVersion    versao que aparece no nome do setup (default 0.2.0)
;   MySourceDir  pasta onde esta MagoDaPerformance.exe (default publish)
;   MyOutputDir  pasta onde o setup e escrito (default .)

#ifndef MyVersion
  #define MyVersion "0.2.0"
#endif

#ifndef MySourceDir
  #define MySourceDir "publish"
#endif

#ifndef MyOutputDir
  #define MyOutputDir "."
#endif

#define MyAppName "Mago da Performance"
#define MyAppExeName "MagoDaPerformance.exe"
#define MyAppId "{{7C1E4E1B-5B0C-4C47-9E0F-0D2A6B7F3C11}"
#define MyPublisher "Ribanense Solucoes"
#define MyUrl "https://github.com/Nickallafe/Mago"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyVersion}
AppVerName={#MyAppName} {#MyVersion}
AppPublisher={#MyPublisher}
AppPublisherURL={#MyUrl}
AppSupportURL={#MyUrl}
AppUpdatesURL={#MyUrl}/releases
AppComments=Diagnostico e otimizacao de desempenho do Windows
VersionInfoVersion={#MyVersion}
VersionInfoDescription=Instalador do {#MyAppName}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#MyOutputDir}
OutputBaseFilename=MagoSetup-{#MyVersion}
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousLanguage=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Diagnostico e otimizacao do Windows"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
