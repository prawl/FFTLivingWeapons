using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// Live image base of fft_enhanced.exe. Pins are documented as preferred-base absolutes (0x140000000).
/// The Denuvo-free build (2026-10-15, Steam buildid 24304444) enabled ASLR, so At(pin) rebases a pin
/// onto the live image. Mod.StartEngine sets it once before the Engine thread starts (LW-387,
/// docs/research/PORT_1.5.2_NODRM_OFFSETS.md).
/// </summary>
internal static class ImageBase
{
    /// <summary>The base every documented pin was captured at (all 1.5.x builds up to the ASLR build).</summary>
    public const long Preferred = 0x140000000L;

    /// <summary>The live image base. Written only before the Engine thread starts, so no locking is needed.</summary>
    public static long Base { get; private set; } = Preferred;

    public static long Delta => Base - Preferred;

    /// <summary>Rebase a documented preferred-base absolute onto the live image.</summary>
    public static long At(long preferredAbsolute) => preferredAbsolute + Delta;

    /// <summary>Accepts a positive 64KB-aligned module base; otherwise returns false and leaves Base alone.</summary>
    public static bool Set(long liveBase)
    {
        if (liveBase <= 0 || (liveBase & 0xFFFF) != 0) return false;
        Base = liveBase;
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>Resolves the live exe base via GetModuleHandleW(null), falling back to
    /// Process.MainModule (as FFTColorCustomizer ModInitializer does). False on null/zero or exception.</summary>
    public static bool TryResolveLive(out long liveBase)
    {
        liveBase = 0;
        try
        {
            IntPtr handle = GetModuleHandleW(null);
            if (handle != IntPtr.Zero)
            {
                liveBase = handle.ToInt64();
                return true;
            }
            using var process = Process.GetCurrentProcess();
            IntPtr? addr = process.MainModule?.BaseAddress;
            if (addr is null || addr.Value == IntPtr.Zero) return false;
            liveBase = addr.Value.ToInt64();
            return true;
        }
        catch (Exception)
        {
            liveBase = 0;
            return false;
        }
    }

    /// <summary>Test seam: restores Preferred.</summary>
    internal static void ResetForTests() => Base = Preferred;
}
