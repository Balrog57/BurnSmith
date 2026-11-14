using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BurnSmith.App;

/// <summary>
/// The end of a long job told to someone away from the screen: a sound, the taskbar button flashing, and a Windows notification
/// (a notification-area icon shown just long enough for its message; Windows 10/11 turn the balloon into a notification).
/// Shell calls rather than WinForms' NotifyIcon: the self-contained exe would carry WinForms for one balloon.
/// </summary>
static class Notify
{
    public static void Ended(Window window, string title, string text, bool failed)
    {
        (failed ? System.Media.SystemSounds.Hand : System.Media.SystemSounds.Asterisk).Play();
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!window.IsActive)
        {
            var flash = new FlashInfo { Size = Marshal.SizeOf<FlashInfo>(), Hwnd = hwnd, Flags = 3 | 12, Count = 0 }; // all, until the window comes to the front
            FlashWindowEx(ref flash);
        }
        var data = new IconData
        {
            Size = Marshal.SizeOf<IconData>(), Hwnd = hwnd, Id = 1, Flags = 0x2 | 0x4 | 0x10, // icon, tip, balloon
            Tip = "BurnSmith", Info = text.Length > 255 ? text[..255] : text, InfoTitle = title.Length > 63 ? title[..63] : title, InfoFlags = failed ? 3 : 1, // error / information
        };
        ExtractIconExW(Environment.ProcessPath ?? "", 0, IntPtr.Zero, out data.Icon, 1);
        if (!Shell_NotifyIconW(0, ref data)) return; // NIM_ADD
        // the icon goes once the notification has been seen; Windows keeps the message in its notification centre
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => { timer.Stop(); Shell_NotifyIconW(2, ref data); DestroyIcon(data.Icon); }; // NIM_DELETE
        timer.Start();
        window.Closed += (_, _) => { if (timer.IsEnabled) { timer.Stop(); Shell_NotifyIconW(2, ref data); } };
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo { public int Size; public IntPtr Hwnd; public int Flags; public int Count; public int Timeout; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct IconData
    {
        public int Size; public IntPtr Hwnd; public int Id; public int Flags; public int CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State; public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags; public Guid Item; public IntPtr BalloonIcon;
    }

    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(int message, ref IconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int ExtractIconExW(string file, int index, IntPtr large, out IntPtr small, int count);
}
