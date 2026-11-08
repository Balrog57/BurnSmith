; After the files are in place: finds an ffmpeg and ffprobe BurnSmith can use, or downloads them into {app}\tools\ffmpeg. Included by BurnSmith.iss and by test-ffmpeg.iss,
; which replaces the registry keys and URLs below so that all of this but the machine PATH can be proved without administrator rights (test-ffmpeg.ps1).
; {app}\tools is found by Tools.SearchRoots even when the app's environment is stale, and it is safe because only administrators can write under Program Files.

#ifndef MachinePathRoot
  #define MachinePathRoot "HKLM"
  #define MachinePathKey "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"
#endif
#ifndef FfmpegZipUrl
  #define FfmpegZipUrl "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
#endif
#ifndef FfmpegHashUrl
  #define FfmpegHashUrl "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256"
#endif

[Setup]
; the download is a zip: only "full" extracts more than .7z
ArchiveExtraction=full
ChangesEnvironment=yes

[CustomMessages]
french.FfmpegFailed=BurnSmith est installé, mais ffmpeg n'a pas pu être téléchargé. BurnSmith en a besoin pour lire les vidéos : il le dira, et cette installation le retentera à la prochaine mise à jour. Vous pouvez aussi relancer l'installateur, ou installer ffmpeg vous-même et l'ajouter au PATH.
english.FfmpegFailed=BurnSmith is installed, but ffmpeg could not be downloaded. BurnSmith needs it to read videos: it will say so, and this installation will try again at the next update. You can also run the installer again, or install ffmpeg yourself and add it to the PATH.
french.FfmpegDownloading=Téléchargement de ffmpeg…
english.FfmpegDownloading=Downloading ffmpeg…

[UninstallDelete]
Type: filesandordirs; Name: "{app}\tools\ffmpeg"
Type: dirifempty; Name: "{app}\tools"

[Code]
const
  // what src\BurnSmith.Core asks of ffmpeg (the check-* commands also use testsrc2, sine): a copy without one of these counts as absent
  NeededEncoders = 'libx264,mpeg2video,ac3,pcm_s16le,srt,mjpeg';
  NeededDecoders = 'pgssub,dvdsub,ass,subrip';
  NeededFilters = 'scale,setsar,pad,fps,telecine,setfield,format,overlay,crop,setpts,volume,afade,anullsrc,color';
  NeededMuxers = 'dvd,matroska,sup,srt,wav,data,image2';

function OwnFfmpeg: String;
begin
  Result := ExpandConstant('{app}\tools\ffmpeg');
end;

// The word after the flags of a line of "ffmpeg -encoders", "-decoders", "-filters" or "-muxers"; words further on are descriptions, which also say "color" or "scale".
function ListedName(const Line: String): String;
var
  Rest: String;
  At: Integer;
begin
  Result := '';
  Rest := Trim(Line);
  At := Pos(' ', Rest);
  if At = 0 then exit;
  Rest := Trim(Copy(Rest, At, Length(Rest)));
  At := Pos(' ', Rest);
  if At > 0 then Result := Copy(Rest, 1, At - 1);
end;

function Lists(const Exe, Option, Names: String): Boolean;
var
  Code, I: Integer;
  Output: TExecOutput;
  Wanted: TStringList;
begin
  Result := False;
  if not ExecAndCaptureOutput(Exe, '-hide_banner ' + Option, '', SW_HIDE, ewWaitUntilTerminated, Code, Output) or (Code <> 0) or Output.Error then exit;
  Wanted := TStringList.Create;
  try
    Wanted.CommaText := Names;
    for I := 0 to GetArrayLength(Output.StdOut) - 1 do
      if Wanted.IndexOf(ListedName(Output.StdOut[I])) >= 0 then Wanted.Delete(Wanted.IndexOf(ListedName(Output.StdOut[I])));
    Result := Wanted.Count = 0;
    if not Result then Log(Exe + ' ' + Option + ' lacks: ' + Wanted.CommaText);
  finally
    Wanted.Free;
  end;
end;

// Runs them: a file of that name, or a build without what BurnSmith uses, counts as absent. A file that is no program raises here or returns False.
function FfmpegUsable(const Dir: String): Boolean;
var
  Code: Integer;
  Ffmpeg: String;
begin
  Result := False;
  Ffmpeg := AddBackslash(Dir) + 'ffmpeg.exe';
  if not FileExists(Ffmpeg) or not FileExists(AddBackslash(Dir) + 'ffprobe.exe') then exit;
  try
    Result := Exec(AddBackslash(Dir) + 'ffprobe.exe', '-version', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0)
      and Lists(Ffmpeg, '-encoders', NeededEncoders) and Lists(Ffmpeg, '-decoders', NeededDecoders)
      and Lists(Ffmpeg, '-filters', NeededFilters) and Lists(Ffmpeg, '-muxers', NeededMuxers);
  except
    Log(Dir + ': ' + GetExceptionMessage);
  end;
  Log(Dir + ': usable ffmpeg = ' + IntToStr(Ord(Result)));
end;

// %VAR% as the registry keeps it in a REG_EXPAND_SZ value.
function ExpandVariables(const Text: String): String;
var
  Rest, Name, Value: String;
  At: Integer;
begin
  Result := '';
  Rest := Text;
  while Rest <> '' do begin
    At := Pos('%', Rest);
    if At = 0 then begin Result := Result + Rest; exit; end;
    Result := Result + Copy(Rest, 1, At - 1);
    Delete(Rest, 1, At);
    At := Pos('%', Rest);
    if At = 0 then begin Result := Result + '%' + Rest; exit; end;
    Name := Copy(Rest, 1, At - 1);
    Value := GetEnv(Name);
    if Value = '' then Value := '%' + Name + '%';
    Result := Result + Value;
    Delete(Rest, 1, At);
  end;
end;

// The entries of a PATH value, as written.
function PathEntries(const Value: String): TStringList;
begin
  Result := TStringList.Create;
  Result.Delimiter := ';';
  Result.StrictDelimiter := True;
  Result.DelimitedText := Value;
end;

function SameFolder(const Entry, Folder: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(ExpandVariables(RemoveQuotes(Trim(Entry)))), RemoveBackslashUnlessRoot(Folder)) = 0;
end;

// Not setx: it cuts a long PATH and turns %VAR% into its value. A value that cannot be read is left alone, never replaced by this one entry.
procedure AddToMachinePath(const Folder: String);
var
  Value: String;
  Entries: TStringList;
  I: Integer;
begin
  if not RegQueryStringValue({#MachinePathRoot}, '{#MachinePathKey}', 'Path', Value) then begin Log('machine PATH unreadable: left alone'); exit; end;
  Entries := PathEntries(Value);
  try
    for I := 0 to Entries.Count - 1 do
      if SameFolder(Entries[I], Folder) then exit;
  finally
    Entries.Free;
  end;
  if (Value <> '') and (Value[Length(Value)] <> ';') then Value := Value + ';';
  if not RegWriteExpandStringValue({#MachinePathRoot}, '{#MachinePathKey}', 'Path', Value + Folder) then Log('machine PATH not written');
end;

procedure RemoveFromMachinePath(const Folder: String);
var
  Value: String;
  Entries: TStringList;
  I: Integer;
  Found: Boolean;
begin
  if not RegQueryStringValue({#MachinePathRoot}, '{#MachinePathKey}', 'Path', Value) then exit;
  Entries := PathEntries(Value);
  try
    Found := False;
    for I := Entries.Count - 1 downto 0 do
      if SameFolder(Entries[I], Folder) then begin Entries.Delete(I); Found := True; end;
    if Found and not RegWriteExpandStringValue({#MachinePathRoot}, '{#MachinePathKey}', 'Path', Entries.DelimitedText) then Log('machine PATH not written');
  finally
    Entries.Free;
  end;
end;

function FfmpegProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if ProgressMax > 0 then WizardForm.FilenameLabel.Caption := IntToStr(Progress div 1048576) + ' / ' + IntToStr(ProgressMax div 1048576) + ' Mo';
  Result := True;
end;

function IsHexHash(const Text: String): Boolean;
var
  I: Integer;
begin
  Result := Length(Text) = 64;
  for I := 1 to Length(Text) do
    if Pos(Lowercase(Text[I]), '0123456789abcdef') = 0 then Result := False;
end;

// shortcut: the hash comes from the same site as the file, over HTTPS only, so it catches a damaged download, not a compromised site; upgrade if gyan.dev ever signs its builds.
procedure DownloadFfmpeg(const Dest: String);
var
  HashText: AnsiString;
  Hash, Unpacked: String;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('FfmpegDownloading');
  DownloadTemporaryFile('{#FfmpegHashUrl}', 'ffmpeg.sha256', '', nil);
  if not LoadStringFromFile(ExpandConstant('{tmp}\ffmpeg.sha256'), HashText) then RaiseException('hash file unreadable');
  Hash := Copy(Trim(String(HashText)), 1, 64);
  // an empty hash would make the download check nothing
  if not IsHexHash(Hash) then RaiseException('no SHA-256 in the hash file');
  DownloadTemporaryFile('{#FfmpegZipUrl}', 'ffmpeg.zip', Hash, @FfmpegProgress);
  Unpacked := ExpandConstant('{tmp}\ffmpeg');
  ExtractArchive(ExpandConstant('{tmp}\ffmpeg.zip'), Unpacked, '', False, nil);
  if not FfmpegUsable(Unpacked) then RaiseException('the downloaded build lacks what BurnSmith uses');
  if not ForceDirectories(Dest)
    or not CopyFile(Unpacked + '\ffmpeg.exe', Dest + '\ffmpeg.exe', False)
    or not CopyFile(Unpacked + '\ffprobe.exe', Dest + '\ffprobe.exe', False)
    or not CopyFile(Unpacked + '\LICENSE', Dest + '\LICENSE', False) then RaiseException('copy into ' + Dest + ' failed');
end;

// An update leaves {app}\tools alone, so a good copy is kept without a download. A broken one goes first: Tools.Find would take it before anything on the PATH.
// An ffmpeg on the PATH is never run here: this setup is elevated, and a user-writable PATH folder would let any program of the user's run as administrator.
// Its own copy is downloaded instead (hash checked, under Program Files), which Tools.Find prefers anyway.
// Never fails the install: nothing here may raise past this procedure. Run at every install, so a failed download is tried again at the next update.
procedure SetUpFfmpeg;
var
  Dest: String;
begin
  Dest := OwnFfmpeg;
  try
    if FfmpegUsable(Dest) then begin
      AddToMachinePath(Dest);
      exit;
    end;
    if DirExists(Dest) then begin
      DelTree(Dest, True, True, True);
      RemoveFromMachinePath(Dest);
    end;
    Log('no usable ffmpeg: downloading');
    DownloadFfmpeg(Dest);
    AddToMachinePath(Dest);
    Log('ffmpeg installed in ' + Dest);
  except
    Log('ffmpeg not installed: ' + GetExceptionMessage);
    DelTree(Dest, True, True, True);
    SuppressibleMsgBox(CustomMessage('FfmpegFailed'), mbInformation, MB_OK, IDOK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then SetUpFfmpeg;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RemoveFromMachinePath(OwnFfmpeg);
end;
