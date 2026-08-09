#ifndef MyAppVersion
  #error MyAppVersion must be supplied by Build-Release.ps1.
#endif

#ifndef SourceDir
  #error SourceDir must be supplied by Build-Release.ps1.
#endif

#ifndef OutputDir
  #error OutputDir must be supplied by Build-Release.ps1.
#endif

#define MyAppName "KiriScope"
#define MyAppPublisher "KiriScope"
#define MyAppExeName "KiriScope.Gui.exe"

[Setup]
AppId={{D3CD5D40-FF96-4A50-9259-84C9BD7BFCBA}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=KiriScope-Setup-{#MyAppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}\ChineseSimplified.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
english.DesktopShortcut=Create a desktop shortcut
english.AdditionalShortcuts=Additional shortcuts:
chinesesimplified.DesktopShortcut=创建桌面快捷方式
chinesesimplified.AdditionalShortcuts=附加快捷方式：
japanese.DesktopShortcut=デスクトップ ショートカットを作成する
japanese.AdditionalShortcuts=追加のショートカット:

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalShortcuts}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
