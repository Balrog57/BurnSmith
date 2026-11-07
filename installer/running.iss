[CustomMessages]
; Uninstalling a running BurnSmith would remove everything but its exe and say nothing. The installed app holds this mutex (App.xaml.cs), so the uninstaller stops first, having removed nothing.
; Not AppMutex: that would also stop setup, and the in-app update relies on Restart Manager closing the app. Included by BurnSmith.iss and test-ffmpeg.iss.
french.AppRunning=BurnSmith est ouvert : fermez-le, puis réessayez. Annuler laisse BurnSmith installé.
english.AppRunning=BurnSmith is open: close it, then retry. Cancel leaves BurnSmith installed.

[Code]
// shortcut: the name is per Windows session, so a copy open in another user's session is not seen; upgrade with a Global\ name and an open DACL if shared PCs matter.
function InitializeUninstall: Boolean;
begin
  Result := True;
  while Result and CheckForMutexes('BurnSmithRunning') do
    Result := SuppressibleMsgBox(CustomMessage('AppRunning'), mbError, MB_RETRYCANCEL, IDCANCEL) = IDRETRY;
end;
