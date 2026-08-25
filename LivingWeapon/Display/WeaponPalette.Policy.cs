namespace LivingWeapon;

/// <summary>What WeaponPalette.Tick does with the two resident palette banks this tick: nothing,
/// restore the currently-painted palette to its snapshot, paint the desired palette (a full
/// 15-entry overwrite), or restore then paint (a palette switch). See
/// <see cref="WeaponPalettePolicy.Decide"/> for which row of the six-row table produces each
/// one.</summary>
internal enum WeaponPaletteAction { Nothing, Restore, Paint, RestoreThenPaint }

/// <summary>
/// LW-251: the pure decision half of the WeaponPalette runtime -- no memory access; the caller
/// (WeaponPalette.cs) supplies every input and executes the returned action. Six rows (the plan's
/// decision table, WeaponPaletteTests.cs's "Decide_*" section pins each one directly):
///
/// | painted | desired | action |
/// |---|---|---|
/// | none | none | Nothing |
/// | P | none | Restore |
/// | none | (P,W) | Paint |
/// | (P,W) | same (P,W), ticksUnchanged &lt; reassertTicks | Nothing |
/// | (P,W) | same (P,W), ticksUnchanged &gt;= reassertTicks | Paint (re-assert; counter resets) |
/// | (P0,W0) | (P1,W1), P1 != P0 | RestoreThenPaint |
/// | (P,W0) | (P,W1), W1 != W0 | Paint (same palette, full overwrite, no restore needed) |
///
/// "none" is the -1/-1 sentinel for both pal and weapon, mirroring <see cref="WeaponMeta.Palette"/>'s
/// own -1 default and the runtime's initial latch (0 is a real, used palette index -- Materia
/// Blade's own -- so it cannot double as "no colours").
/// </summary>
internal static class WeaponPalettePolicy
{
    public static WeaponPaletteAction Decide(int paintedPal, int paintedWeapon, int desiredPal, int desiredWeapon,
        int ticksUnchanged, int reassertTicks)
    {
        bool hasPainted = paintedWeapon >= 0;
        bool hasDesired = desiredWeapon >= 0;

        if (!hasPainted && !hasDesired) return WeaponPaletteAction.Nothing;
        if (hasPainted && !hasDesired) return WeaponPaletteAction.Restore;
        if (!hasPainted) return WeaponPaletteAction.Paint;   // hasDesired, nothing painted yet

        if (paintedPal == desiredPal && paintedWeapon == desiredWeapon)
            return ticksUnchanged >= reassertTicks ? WeaponPaletteAction.Paint : WeaponPaletteAction.Nothing;

        return paintedPal != desiredPal ? WeaponPaletteAction.RestoreThenPaint : WeaponPaletteAction.Paint;
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
