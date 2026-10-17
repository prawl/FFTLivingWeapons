using System;

namespace LivingWeapon;

/// <summary>What WeaponPalette.Tick does with the two resident palette banks. Nothing, restore the
/// painted palette to its snapshot, paint the desired one (full 15-entry overwrite), or restore
/// then paint (a palette switch). See <see cref="WeaponPalettePolicy.Decide"/>.</summary>
internal enum WeaponPaletteAction { Nothing, Restore, Paint, RestoreThenPaint }

/// <summary>
/// The pure decision half of the WeaponPalette runtime. No memory access; the caller
/// (WeaponPalette.cs) supplies the inputs and runs the action. The "Decide_*" tests in
/// WeaponPaletteTests.cs pin each row.
///
/// | painted | desired | action |
/// |---|---|---|
/// | none | none | Nothing |
/// | P | none | Restore |
/// | none | (P,W) | Paint |
/// | (P,W,G) | same (P,W,G), ticksUnchanged &lt; reassertTicks | Nothing |
/// | (P,W,G) | same (P,W,G), ticksUnchanged &gt;= reassertTicks | Paint (re-assert; counter resets) |
/// | (P0,W0,G0) | (P1,W1,*), P1 != P0 | RestoreThenPaint |
/// | (P,W0,G0) | (P,W1,*), W1 != W0 | Paint (same palette, full overwrite) |
/// | (P,W,G0) | (P,W,G1), G1 != G0 | Paint (kill-tier glow changed, same overwrite) |
///
/// "none" is -1/-1 for palette and weapon, matching <see cref="WeaponMeta.Palette"/>'s default
/// (0 is a real palette, Materia Blade's). Glow has no sentinel. Tier 0 means no glow.
/// </summary>
internal static class WeaponPalettePolicy
{
    public static WeaponPaletteAction Decide(int paintedPal, int paintedWeapon, int paintedGlow,
        int desiredPal, int desiredWeapon, int desiredGlow, int ticksUnchanged, int reassertTicks)
    {
        bool hasPainted = paintedWeapon >= 0;
        bool hasDesired = desiredWeapon >= 0;

        if (!hasPainted && !hasDesired) return WeaponPaletteAction.Nothing;
        if (hasPainted && !hasDesired) return WeaponPaletteAction.Restore;
        if (!hasPainted) return WeaponPaletteAction.Paint;   // hasDesired, nothing painted yet

        if (paintedPal == desiredPal && paintedWeapon == desiredWeapon)
        {
            // A kill-tier change repaints at once; the full overwrite replaces every
            // byte the old glow level wrote, so no restore is needed first.
            if (paintedGlow != desiredGlow) return WeaponPaletteAction.Paint;
            return ticksUnchanged >= reassertTicks ? WeaponPaletteAction.Paint : WeaponPaletteAction.Nothing;
        }

        return paintedPal != desiredPal ? WeaponPaletteAction.RestoreThenPaint : WeaponPaletteAction.Paint;
    }

    /// <summary>Brightens each BGR555 channel toward white by fraction f. C' = min(31, round(c + (31-c)*f)).
    /// Bit 15 is always returned clear (PaintBanks owns it, LW-308). Rounds half to even to match Python's
    /// round() in tools/probes/lw305_bench_paint.py (2dbab25): at f=0.75, 0x14B8 gives 0x631D, not 0x673D.</summary>
    public static int Brighten(int code, double f)
    {
        int r = code & 31, g = (code >> 5) & 31, b = (code >> 10) & 31;
        int rr = Math.Min(31, (int)Math.Round(r + (31 - r) * f));
        int gg = Math.Min(31, (int)Math.Round(g + (31 - g) * f));
        int bb = Math.Min(31, (int)Math.Round(b + (31 - b) * f));
        return rr | (gg << 5) | (bb << 10);
    }

    /// <summary>Element-wise <see cref="Brighten"/> over a 15-entry code array. f == 0.0 returns
    /// <paramref name="codes"/> itself, with no copy.</summary>
    public static int[] BrightenAll(int[] codes, double f)
    {
        if (f == 0.0) return codes;
        var result = new int[codes.Length];
        for (int i = 0; i < codes.Length; i++) result[i] = Brighten(codes[i], f);
        return result;
    }

    /// <summary>true when a bank's current entries (1..15) match neither the first-look vanilla
    /// snapshot nor what this runtime last wrote. A foreign writer or a bad first look. Advisory; the
    /// caller logs and keeps painting, since re-memorizing would copy a bad first look into every restore.</summary>
    public static bool SnapshotSuspect(ushort[] current, ushort[] vanilla, ushort[]? lastWritten)
    {
        return !Matches(vanilla) && (lastWritten is null || !Matches(lastWritten));

        bool Matches(ushort[] reference)
        {
            if (reference.Length != current.Length) return false;
            for (int i = 0; i < current.Length; i++)
                if (current[i] != reference[i]) return false;
            return true;
        }
    }
}
