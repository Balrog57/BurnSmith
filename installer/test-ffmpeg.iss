; A setup with no administrator rights around ffmpeg.iss, for test-ffmpeg.ps1: test registry keys instead of the real PATHs, and the URLs it passes with /D. Never released.
#define MachinePathRoot "HKCU"
#define MachinePathKey "Software\BurnSmithFfmpegTest\Machine"
#ifndef OutName
  #define OutName "test-ffmpeg"
#endif

[Setup]
AppId=BurnSmithFfmpegTest
AppName=BurnSmith ffmpeg test
AppVersion=1
PrivilegesRequired=lowest
CreateUninstallRegKey=no
DefaultDirName={tmp}\unused
DisableProgramGroupPage=yes
OutputDir=..\obj\ffmpeg-test
OutputBaseFilename={#OutName}
Compression=none

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\LICENSE"; DestDir: "{app}"

[Run]
; the same flags as the relaunch, under the same /SILENT: was ffmpeg already there when it ran?
Filename: "{cmd}"; Parameters: "/c if exist ""{app}\tools\ffmpeg\ffmpeg.exe"" echo present> ""{app}\run-saw.txt"""; Flags: runhidden postinstall skipifnotsilent

#include "ffmpeg.iss"
#include "running.iss"
