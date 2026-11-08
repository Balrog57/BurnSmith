; ffmpeg and ffprobe ship inside this setup: the build CI tests (.github\workflows\ci.yml), whose hashes publish.ps1 checks. They go to {app}\tools\ffmpeg, which the
; machine PATH gets. Included by BurnSmith.iss and by test-ffmpeg.iss, which replaces the registry key below so that all of this but the real machine PATH can be
; proved without administrator rights (test-ffmpeg.ps1). {app}\tools is found first by Tools.SearchRoots, and is safe: only administrators write under Program Files.

#ifndef MachinePathRoot
  #define MachinePathRoot "HKLM"
  #define MachinePathKey "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"
#endif
#ifndef FfmpegDir
  #define FfmpegDir "..\tools\ffmpeg"
#endif

[Setup]
ChangesEnvironment=yes

[Files]
; next to each other: the two share most of their bytes, which the solid compression then stores once
Source: "{#FfmpegDir}\ffmpeg.exe"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion
Source: "{#FfmpegDir}\ffprobe.exe"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion
Source: "ffmpeg-LICENSE.txt"; DestDir: "{app}\tools\ffmpeg"; DestName: "LICENSE"; Flags: ignoreversion
Source: "ffmpeg-README.txt"; DestDir: "{app}\tools\ffmpeg"; DestName: "README.txt"; Flags: ignoreversion

[UninstallDelete]
; what an older setup downloaded there is not in the list of installed files
Type: filesandordirs; Name: "{app}\tools\ffmpeg"
Type: dirifempty; Name: "{app}\tools"

[Code]
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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then AddToMachinePath(ExpandConstant('{app}\tools\ffmpeg'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RemoveFromMachinePath(ExpandConstant('{app}\tools\ffmpeg'));
end;
