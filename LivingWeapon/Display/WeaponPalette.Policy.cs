using System;

namespace LivingWeapon;

/// <summary>What WeaponPalette.Tick does with the two resident palette banks this tick: nothing,
/// restore the currently-painted palette to its snapshot, paint the desired palette (a full
/// 15-entry overwrite), or restore then paint (a palette switch). See
/// <see cref="WeaponPalettePolicy.Decide"/> for which row of the seven-row table produces each
/// one.</summary>
internal enum WeaponPaletteAction { Nothing, Restore, Paint, RestoreThenPaint }

/// <summary>
/// LW-251: the pure decision half of the WeaponPalette runtime -- no memory access; the caller
/// (WeaponPalette.cs) supplies every input and executes the returned action. Seven rows (the
/// plan's decision table, WeaponPaletteTests.cs's "Decide_*" section pins each one directly):
///
/// | painted | desired | action |
/// |---|---|---|
/// | none | none | Nothing |
/// | P | none | Restore |
/// | none | (P,W) | Paint |
/// | (P,W,G) | same (P,W,G), ticksUnchanged &lt; reassertTicks | Nothing |
/// | (P,W,G) | same (P,W,G), ticksUnchanged &gt;= reassertTicks | Paint (re-assert; counter resets) |
/// | (P0,W0,G0) | (P1,W1,*), P1 != P0 | RestoreThenPaint |
/// | (P,W0,G0) | (P,W1,*), W1 != W0 | Paint (same palette, full overwrite, no restore needed) |
/// | (P,W,G0) | (P,W,G1), G1 != G0 | Paint (LW-295: kill-tier glow changed -- full overwrite, no
///   restore needed, identical mechanics to the same-palette weapon-swap row just above) |
///
/// "none" is the -1/-1 sentinel for both pal and weapon, mirroring <see cref="WeaponMeta.Palette"/>'s
/// own -1 default and the runtime's initial latch (0 is a real, used palette index -- Materia
/// Blade's own -- so it cannot double as "no colours"). Glow has no matching -1 sentinel: tier 0
/// (no brightening) already means "no glow" and pairs naturally with the painted/desired "none"
/// state (-1/-1 palette+weapon, glow 0).
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
            // LW-295: a kill-tier bump (or drop) repaints right away, the same as the reassert
            // cadence's own Paint -- the full 15-entry overwrite already replaces every byte the
            // previous glow level wrote, so there is nothing for a restore to undo first.
            if (paintedGlow != desiredGlow) return WeaponPaletteAction.Paint;
            return ticksUnchanged >= reassertTicks ? WeaponPaletteAction.Paint : WeaponPaletteAction.Nothing;
        }

        return paintedPal != desiredPal ? WeaponPaletteAction.RestoreThenPaint : WeaponPaletteAction.Paint;
    }

    /// <summary>Brightens one BGR555 channel toward white by fraction <paramref name="f"/> (0 =
    /// unchanged, 1 = full white): c' = min(31, round(c + (31-c)*f)). Operates on the low 15 bits
    /// of <paramref name="code"/> only and always returns with bit 15 CLEAR -- PaintBanks is the
    /// only legitimate source of that bit (the LW-308 rule: a baked code can never smuggle it in).
    /// Math.Round's default MidpointRounding.ToEven deliberately matches Python's round() in the
    /// committed look-test probe (tools/probes/lw305_bench_paint.py, commit 2dbab25): at f=0.75 a
    /// channel value of 5 lands exactly on the 24.5 half-case, and ToEven is the only rounding
    /// mode that reproduces the probe's own ladder (AwayFromZero would move that channel to 25,
    /// brightening the probe's own sample code 0x14B8 to 0x673D instead of the probe's 0x631D).</summary>
    public static int Brighten(int code, double f)
    {
        int r = code & 31, g = (code >> 5) & 31, b = (code >> 10) & 31;
        int rr = Math.Min(31, (int)Math.Round(r + (31 - r) * f));
        int gg = Math.Min(31, (int)Math.Round(g + (31 - g) * f));
        int bb = Math.Min(31, (int)Math.Round(b + (31 - b) * f));
        return rr | (gg << 5) | (bb << 10);
    }

    /// <summary>Element-wise <see cref="Brighten"/> over a full authored 15-entry code array. f ==
    /// 0.0 (tier 0, no glow) returns <paramref name="codes"/> itself, unchanged -- no allocation
    /// on the common untiered/plain-wielder path -- rather than a freshly built but identical
    /// copy.</summary>
    public static int[] BrightenAll(int[] codes, double f)
    {
        if (f == 0.0) return codes;
        var result = new int[codes.Length];
        for (int i = 0; i < codes.Length; i++) result[i] = Brighten(codes[i], f);
        return result;
    }

    /// <summary>LW-308: true when a bank's CURRENT entries (1..15) match neither the first-look
    /// vanilla snapshot nor what this runtime last wrote there -- a foreign writer, or a bad
    /// first look. Advisory only: the caller logs and keeps painting; it never re-memorizes,
    /// because if the first look WAS wrong, adopting the current bytes would launder that error
    /// into the snapshot every restore then repeats.</summary>
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
