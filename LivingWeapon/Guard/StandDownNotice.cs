using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LivingWeapon;

/// <summary>
/// OS-level stand-down notice. A player who never opens livingweapon.log still learns the
/// mod went inert. Zero project dependencies (as FingerprintGuard.cs), so a sibling mod can copy this file.
/// <see cref="Show"/> runs on the Engine tick thread from LaunchGuard's stand-down edge, and that thread
/// also drains the flight recorder (FlightRecorder.DrainPending; LaunchGuard.StandDown). A modal
/// MessageBoxW or an exception there would stall that evidence write, so Show uses its own background
/// thread and swallows every exception.
/// </summary>
internal static class StandDownNotice
{
    // MessageBoxW flags (User32), OR-combined:
    //   MB_OK            = 0x00000000 (single OK button; also the implicit default)
    //   MB_ICONWARNING   = 0x00000030 (warning triangle icon)
    //   MB_SETFOREGROUND = 0x00010000 (brings the message box window to the foreground)
    //   MB_TOPMOST       = 0x00040000 (WS_EX_TOPMOST. Stays above the fullscreen game window)
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const uint MB_TOPMOST = 0x00040000;
    private const uint Flags = MB_OK | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND;

    /// <summary>Raises a native message box on a background thread and returns at once; never blocks or throws.</summary>
    public static void Show(string title, string text)
    {
        try
        {
            var thread = new Thread(() =>
            {
                try { MessageBoxW(IntPtr.Zero, text, title, Flags); }
                catch { /* swallow: the caller's loop thread must never stall on a dialog failure */ }
            })
            {
                IsBackground = true,
                Name = "LivingWeapon-StandDownNotice",
            };
            thread.Start();
        }
        catch { /* swallow: same rule, covering thread construction and start */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
