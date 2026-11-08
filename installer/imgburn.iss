[CustomMessages]
; After the files are in place: installs ImgBurn, BurnSmith's burner, unless it is already there. Included by BurnSmith.iss and test-ffmpeg.iss.
; This setup carries ImgBurn's own setup, unmodified (publish.ps1 checks its hash), and runs it silently: nothing is downloaded.
; It stays installed as itself (its own uninstaller in Windows' list), so uninstalling BurnSmith leaves it alone.
french.ImgBurnFailed=BurnSmith est installé, mais ImgBurn n'a pas pu l'être. BurnSmith gravera avec le graveur de Windows, et cette installation retentera ImgBurn à la prochaine mise à jour. Vous pouvez aussi l'installer depuis www.imgburn.com.
english.ImgBurnFailed=BurnSmith is installed, but ImgBurn could not be. BurnSmith will burn with Windows' own burner, and this installation will try ImgBurn again at the next update. You can also install it from www.imgburn.com.
french.ImgBurnInstalling=Installation d'ImgBurn…
english.ImgBurnInstalling=Installing ImgBurn…

#ifndef ImgBurnSetup
  #define ImgBurnSetup "..\tools\imgburn-setup\SetupImgBurn_2.5.8.0.exe"
#endif
; where Iso.cs looks; test-ffmpeg.iss gives a folder of its own
#ifndef ImgBurnDir
  #define ImgBurnDir "{commonpf32}\ImgBurn"
#endif

[Files]
Source: "{#ImgBurnSetup}"; DestName: "SetupImgBurn.exe"; Flags: dontcopy

[Code]
function ImgBurnPresent: Boolean;
begin
  Result := FileExists(ExpandConstant('{#ImgBurnDir}\ImgBurn.exe')) or FileExists(ExpandConstant('{commonpf64}\ImgBurn\ImgBurn.exe'));
end;

// Never fails the install: BurnSmith falls back on IMAPI2 without it.
procedure SetUpImgBurn;
var
  Code: Integer;
  HadIcon: Boolean;
begin
  if ImgBurnPresent then exit;
  try
    WizardForm.StatusLabel.Caption := CustomMessage('ImgBurnInstalling');
    ExtractTemporaryFile('SetupImgBurn.exe');
    HadIcon := FileExists(ExpandConstant('{commondesktop}\ImgBurn.lnk'));
    // /D last and unquoted (NSIS): without it, ImgBurn goes back to the folder its setup remembers (HKCU\Software\ImgBurn), where Iso.cs does not look
    if not Exec(ExpandConstant('{tmp}\SetupImgBurn.exe'), '/S /D=' + ExpandConstant('{#ImgBurnDir}'), '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      RaiseException('ImgBurn setup failed (exit ' + IntToStr(Code) + ')');
    if not ImgBurnPresent then RaiseException('ImgBurn setup ended without ImgBurn.exe in {#ImgBurnDir}');
    // ImgBurn is BurnSmith's burner here, not an app of its own on the desktop; its Start menu entry stays
    if not HadIcon then DeleteFile(ExpandConstant('{commondesktop}\ImgBurn.lnk'));
    Log('ImgBurn installed');
  except
    Log('ImgBurn not installed: ' + GetExceptionMessage);
    SuppressibleMsgBox(CustomMessage('ImgBurnFailed'), mbInformation, MB_OK, IDOK);
  end;
end;

<event('CurStepChanged')>
procedure ImgBurnStep(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then SetUpImgBurn;
end;
