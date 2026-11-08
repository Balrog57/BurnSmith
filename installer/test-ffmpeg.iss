; A setup with no administrator rights around ffmpeg.iss, imgburn.iss and running.iss, for test-ffmpeg.ps1: a test registry key instead of the machine PATH,
; ImgBurn in a folder of the test, and, given /DAppExe, the app itself with the settings of BurnSmith.iss an update relies on. Never released.
#define MachinePathRoot "HKCU"
#define MachinePathKey "Software\BurnSmithFfmpegTest\Machine"
#define ImgBurnDir "{app}\ImgBurn"
#ifndef OutName
  #define OutName "test-ffmpeg"
#endif
#ifndef AppDir
  #define AppDir "{tmp}\unused"
#endif

[Setup]
AppId=BurnSmithFfmpegTest
AppName=BurnSmith ffmpeg test
AppVersion=1
PrivilegesRequired=lowest
CreateUninstallRegKey=no
DefaultDirName={#AppDir}
DisableProgramGroupPage=yes
OutputDir=..\obj\ffmpeg-test
OutputBaseFilename={#OutName}
Compression=none
CloseApplications=yes
SetupLogging=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\LICENSE"; DestDir: "{app}"
#ifdef AppExe
Source: "{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Run]
; the same flags as the relaunch, under the same /SILENT: was ffmpeg already there when it ran?
Filename: "{cmd}"; Parameters: "/c if exist ""{app}\tools\ffmpeg\ffmpeg.exe"" echo present> ""{app}\run-saw.txt"""; Flags: runhidden postinstall skipifnotsilent
#ifdef AppExe
Filename: "{app}\BurnSmith.exe"; Flags: nowait postinstall runasoriginaluser skipifnotsilent; Check: Relaunch
#endif

#include "ffmpeg.iss"
#include "imgburn.iss"
#include "running.iss"

[Code]
function Relaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;
