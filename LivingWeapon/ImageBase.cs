using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// The live image base of fft_enhanced.exe. Every pinned game address in this mod is documented and stored as
/// a PREFERRED-BASE absolute (image base 0x140000000, the only base the game ever had while Denuvo-wrapped).
/// The Denuvo-free build (2026-10-15, Steam buildid 24304444) turned ASLR on, so the image now loads at a
/// random base each launch; At(pin) rebases a documented pin onto the live image. Default Base is Preferred
/// so unit tests and tools keep reading the documented numbers unchanged. Mod.StartEngine resolves the live
/// base ONCE before the Engine (and its background thread) exists; nothing else writes it (LW-387,
/// docs/research/PORT_1.5.2_NODRM_OFFSETS.md). Dependency-free by design: no Mem, no ModLogger, no Reloaded types.
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

    /// <summary>Accepts a plausible module base (positive, 64KB-aligned: the PE section/allocation
    /// granularity); returns false and leaves Base untouched otherwise.</summary>
    public static bool Set(long liveBase)
    {
        if (liveBase <= 0 || (liveBase & 0xFFFF) != 0) return false;
        Base = liveBase;
        return true;
    }

    // GetModuleHandleW(NULL) returns the calling process's exe base by definition (kernel32).
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>Resolves the live exe base. Primary: GetModuleHandleW(null), which is the exe base by
    /// definition, needs no module enumeration and has no transient failure mode. Fallback:
    /// Process.GetCurrentProcess().MainModule?.BaseAddress (precedent: FFTColorCustomizer
    /// ModInitializer). False on a zero/null result or any exception; never throws, liveBase is 0 then.</summary>
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
