using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LivingWeapon;

/// <summary>
/// LW-50's OS-level stand-down notice: a player who never opens livingweapon.log still learns the
/// mod went inert. COPY-FILE PORTABILITY CONTRACT (mirrors FingerprintGuard.cs): zero project
/// dependencies (no Offsets, no ModLogger, no IGameMemory, no Reloaded types), so a sibling mod
/// adopts it by copying this one file.
///
/// NEVER-BLOCK, NEVER-THROW CONTRACT: <see cref="Show"/> fires from LaunchGuard's stand-down edge,
/// which runs on the Engine tick loop thread. That same loop thread drains the flight recorder's
/// pending flush on its NEXT tick (FlightRecorder.DrainPending, called once per Engine tick), and
/// the stand-down is also the flight recorder's FlushOnce archive trigger (the first LogError of a
/// launch). FlightRecorder.Flush is a documented never-throw doctrine (FlightRecorder.cs: "Never
/// throws and never calls ModLogger... every failure here is swallowed") precisely so a flush
/// failure cannot stall the engine loop. A message box that BLOCKED that same thread (MessageBoxW
/// is modal and does not return until dismissed) or THREW out of the stand-down path would stall
/// that evidence write just as badly, so Show hands the dialog to its own background thread and
/// swallows every exception, both around starting that thread and inside it.
/// </summary>
internal static class StandDownNotice
{
    // MessageBoxW flags (User32), OR-combined:
    //   MB_OK            = 0x00000000 (single OK button; also the implicit default)
    //   MB_ICONWARNING   = 0x00000030 (warning triangle icon)
    //   MB_SETFOREGROUND = 0x00010000 (brings the message box window to the foreground)
    //   MB_TOPMOST       = 0x00040000 (WS_EX_TOPMOST: stays above the fullscreen game window)
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const uint MB_TOPMOST = 0x00040000;
    private const uint Flags = MB_OK | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND;

    /// <summary>Raises a native message box on a dedicated background thread and returns
    /// immediately; never blocks the caller and never throws (see the class doctrine above).</summary>
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
        catch { /* swallow: same doctrine, covering thread construction/start itself */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
