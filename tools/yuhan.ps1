# Drives ONE program, Yuhan Blu-ray DVD Creator (BlurayDVDCreator.exe), and nothing else:
#   yuhan.ps1 start | shot OUT.png | wins | tree [DEPTH] | click X Y | dclick X Y | rclick X Y | type TEXT | key KEYS | invoke NAME | close
# click coordinates are pixels inside the picture "shot" wrote (the program's front window). Every input first checks that the
# window in front belongs to that program, and gives up otherwise.
param([string]$cmd, [string]$a1, [string]$a2)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class W {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static List<IntPtr> Of(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list; // front to back
    }
    public static string Title(IntPtr h) { var s = new StringBuilder(300); GetWindowText(h, s, 300); return s.ToString(); }
    public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
}
'@
[void][W]::SetProcessDPIAware()
$exe = 'C:\Program Files\Yuhan Blu-ray DVD Creator\BlurayDVDCreator.exe'

function Proc { Get-Process BlurayDVDCreator -ErrorAction SilentlyContinue | Select-Object -First 1 }
function Wins { $p = Proc; if (-not $p) { throw 'Yuhan is not running' }; ,([W]::Of([uint32]$p.Id) | Where-Object { $r = New-Object W+RECT; [void][W]::GetWindowRect($_, [ref]$r); ($r.R - $r.L) -gt 40 -and ($r.B - $r.T) -gt 40 }) }
function Front { (Wins)[0] }
function Rect($h) { $r = New-Object W+RECT; [void][W]::GetWindowRect($h, [ref]$r); $r }
function Focus {
    $h = Front
    if ([W]::IsIconic($h)) { [void][W]::ShowWindow($h, 9) }
    [void][W]::SetForegroundWindow($h); Start-Sleep -Milliseconds 250
    $p = Proc
    if ([W]::Pid([W]::GetForegroundWindow()) -ne $p.Id) {
        # Windows refuses the focus to a background process: an Alt press lifts that, then try once more
        [System.Windows.Forms.SendKeys]::SendWait('%'); [void][W]::SetForegroundWindow($h); Start-Sleep -Milliseconds 250
        if ([W]::Pid([W]::GetForegroundWindow()) -ne $p.Id) { throw 'the window in front is not Yuhan: nothing sent' }
    }
    $h
}
function Mouse([int]$x, [int]$y, [string]$kind) {
    $h = Focus; $r = Rect $h
    [void][W]::SetCursorPos($r.L + $x, $r.T + $y); Start-Sleep -Milliseconds 120
    if ($kind -eq 'right') { [W]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); [W]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero) }
    else {
        [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        if ($kind -eq 'double') { Start-Sleep -Milliseconds 80; [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }
    }
}

switch ($cmd) {
    'start' { if (-not (Proc)) { Start-Process $exe; Start-Sleep -Seconds 6 }; (Wins) | ForEach-Object { '{0} "{1}"' -f $_, [W]::Title($_) } }
    'wins' { (Wins) | ForEach-Object { $r = Rect $_; '{0} "{1}" {2},{3} {4}x{5}' -f $_, [W]::Title($_), $r.L, $r.T, ($r.R - $r.L), ($r.B - $r.T) } }
    'shot' {
        $h = Front; $r = Rect $h
        $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
        [void][W]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
        $bmp.Save($a1, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        '{0} "{1}" {2}x{3}' -f $h, [W]::Title($h), ($r.R - $r.L), ($r.B - $r.T)
    }
    'tree' {
        Add-Type -AssemblyName UIAutomationClient; Add-Type -AssemblyName UIAutomationTypes
        $h = Front; $r = Rect $h; $max = if ($a1) { [int]$a1 } else { 6 }
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        function Dump($e, $depth) {
            if ($depth -gt $max) { return }
            $b = $e.Current.BoundingRectangle
            if (-not $b.IsEmpty) { '{0}{1} "{2}" @{3},{4} {5}x{6}' -f ('  ' * $depth), $e.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''), $e.Current.Name, [int]($b.X - $r.L), [int]($b.Y - $r.T), [int]$b.Width, [int]$b.Height }
            $c = $walker.GetFirstChild($e)
            while ($c) { Dump $c ($depth + 1); $c = $walker.GetNextSibling($c) }
        }
        Dump ([System.Windows.Automation.AutomationElement]::FromHandle($h)) 0
    }
    'click' { Mouse ([int]$a1) ([int]$a2) 'left' }
    'dclick' { Mouse ([int]$a1) ([int]$a2) 'double' }
    'rclick' { Mouse ([int]$a1) ([int]$a2) 'right' }
    'type' { [void](Focus); [System.Windows.Forms.SendKeys]::SendWait(($a1 -replace '([+^%~(){}\[\]])', '{$1}')) } # typed, so the clipboard is left alone
    'key' { [void](Focus); [System.Windows.Forms.SendKeys]::SendWait($a1) }
    'close' { $p = Proc; if ($p) { [void]$p.CloseMainWindow() } }
    default { throw "unknown command: $cmd" }
}
