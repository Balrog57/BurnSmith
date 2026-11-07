; Inno Setup 6 script: installs dist\BurnSmith.exe (made by publish.ps1) in Program Files. Built by publish.ps1 when Inno Setup is installed:
;   ISCC.exe /DAppVersion=0.5.0 installer\BurnSmith.iss      ->  dist\BurnSmith-Setup-0.5.0.exe
; The in-app update (Updater.cs) downloads that file from the newest GitHub release and runs it with /SILENT /relaunch=1.
; Nothing the user made is removed on uninstall: the template library lives in Documents\BurnSmith\Templates.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; never change this id: it is how a new setup finds the copy to replace
AppId={{1CF61A99-C6D2-443A-84E7-6C5363916B1D}
AppName=BurnSmith
AppVersion={#AppVersion}
AppPublisher=Balrog57
AppPublisherURL=https://github.com/Balrog57/BurnSmith
AppSupportURL=https://github.com/Balrog57/BurnSmith/issues
DefaultDirName={autopf}\BurnSmith
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=BurnSmith-Setup-{#AppVersion}
UninstallDisplayIcon={app}\BurnSmith.exe
SetupIconFile=..\assets\burnsmith.ico
WizardStyle=modern
; the exe is already compressed (single file, compressed), so a fast pass is enough
Compression=lzma2/fast
SolidCompression=no
CloseApplications=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\BurnSmith.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\BurnSmith"; Filename: "{app}\BurnSmith.exe"
Name: "{autodesktop}\BurnSmith"; Filename: "{app}\BurnSmith.exe"; Tasks: desktopicon

[Run]
; after a manual install: a tick box; after an update started by the app (/relaunch=1): start it again without asking, as the user, not as administrator
Filename: "{app}\BurnSmith.exe"; Description: "{cm:LaunchProgram,BurnSmith}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\BurnSmith.exe"; Flags: nowait runasoriginaluser skipifnotsilent; Check: Relaunch

[Code]
function Relaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;
