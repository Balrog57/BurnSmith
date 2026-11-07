[CustomMessages]
; After the files are in place: installs ImgBurn, BurnSmith's burner, unless it is already there. Included by BurnSmith.iss.
; Its author asks that ImgBurn not be bundled, so nothing of it is carried: his own setup is downloaded from imgburn.com and run silently.
; It stays installed as itself (its own uninstaller in Windows' list), so uninstalling BurnSmith leaves it alone.
french.ImgBurnFailed=BurnSmith est installé, mais ImgBurn n'a pas pu être téléchargé. BurnSmith gravera avec le graveur de Windows, et cette installation retentera ImgBurn à la prochaine mise à jour. Vous pouvez aussi l'installer depuis www.imgburn.com.
english.ImgBurnFailed=BurnSmith is installed, but ImgBurn could not be downloaded. BurnSmith will burn with Windows' own burner, and this installation will try ImgBurn again at the next update. You can also install it from www.imgburn.com.
french.ImgBurnDownloading=Téléchargement d'ImgBurn…
english.ImgBurnDownloading=Downloading ImgBurn…

[Code]
const
  ImgBurnUrl = 'https://download.imgburn.com/SetupImgBurn_2.5.8.0.exe';
  // the SHA-256 Chocolatey's moderated package checks; imgburn.com itself publishes only MD5 / SHA-1
  ImgBurnHash = '49aa06eaffe431f05687109fee25f66781abbe1108f3f8ca78c79bdec8753420';

// where its setup puts it, and where Iso.cs looks
function ImgBurnPresent: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf32}\ImgBurn\ImgBurn.exe')) or FileExists(ExpandConstant('{commonpf64}\ImgBurn\ImgBurn.exe'));
end;

// Never fails the install: BurnSmith falls back on IMAPI2 without it.
procedure SetUpImgBurn;
var
  Code: Integer;
begin
  if ImgBurnPresent then exit;
  try
    WizardForm.StatusLabel.Caption := CustomMessage('ImgBurnDownloading');
    DownloadTemporaryFile(ImgBurnUrl, 'SetupImgBurn.exe', ImgBurnHash, nil);
    if not Exec(ExpandConstant('{tmp}\SetupImgBurn.exe'), '/S', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      RaiseException('ImgBurn setup failed (exit ' + IntToStr(Code) + ')');
    if not ImgBurnPresent then RaiseException('ImgBurn setup ended without ImgBurn.exe');
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
