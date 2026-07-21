using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The Ultima half of GrowthEngine: the Materia Blade's HP%-scaled PA hold. For an Ultima
/// weapon, <see cref="GrowthEngine.Route"/> yields the PA lane entirely to this method, so a
/// single writer owns the byte (no fight with the multiplicative growth hold). It holds PA at
/// round(naturalPA × UltimaMul[tier][hpBand]) -- high at full health, lower when hurt.
///
/// The ownership idiom is the proven one (HoldAfterimage): capture natural on first sight,
/// re-apply our target against the engine's per-turn normalize, and leave a FOREIGN value
/// (a real buff/debuff) untouched. Every write is VirtualQuery-guarded (Mem.Writable) and
/// only ever writes a natural-derived value in [1,255], so a wrong guess can at worst linger
/// one battle (the fresh per-battle combat struct clears it on next ResetBattle).
///
/// Ownership caveat (accepted, benign): Afterimage's Speed hold is always >= natural, so its
/// held values and any Speed-DOWN debuff (below natural) occupy disjoint ranges. Ultima's held
/// PA spans BOTH above AND below natural (full HP -> 115-130%, low HP -> 50-90%), so it
/// OVERLAPS the range a PA-down debuff would produce. Consequence: if a transient PA-down
/// equals our lastTarget or natural, the cur== token false-positives and Ultima overwrites the
/// debuff. This is accepted: the write is guarded and always natural-derived (no crash, no
/// corruption; resets next battle), and player-side PA debuffs are essentially absent in FFT:IC.
/// Do NOT add a value-set tracker to the OWNERSHIP-TOKEN check above -- over-engineering for a
/// non-existent threat. (The LW-90 NaturalLedger consulted at the CAPTURE moment is a different
/// boundary: it acts once per battle, across the reset, where no ownership token exists yet.)
/// </summary>
internal sealed partial class GrowthEngine
{
    // PA addr -> (captured natural, the last value WE wrote -- our ownership token; LW-90
    // baked = the restart residue a corrected capture read, also recognized).
    private readonly Dictionary<long, (int natural, int lastTarget, int baked)> _ultima = new();

    /// <summary>True when this weapon's signature is Ultima -- it owns the wielder's PA at
    /// every tier, so Route declines the PA lane for it.</summary>
    internal static bool OwnsPa(WeaponMeta m) => m.Signature is { Ultima: true };

    /// <summary>Hold the Materia Blade wielder's PA at round(naturalPA × UltimaMul[tier][hpBand]).
    /// Always-on (no tier gate); tier only indexes UltimaMul to raise the whole curve.
    /// Main-hand only (the gift commands from the main hand), guarded, fail-safe no-op.
    /// rosterNameId (D7) threads through to ReadHp's two-tier-with-veto locate and the LW-90
    /// NaturalLedger. Internal for the LW-90 seam tests (LocateIn precedent).</summary>
    internal void HoldUltima(long s, WeaponMeta m, int tier, int level, int brave, int faith, int rosterNameId)
    {
        if (!OwnsPa(m)) return;
        long addr = s + Offsets.CPa;
        if (!_mem.Writable(addr, 1)) return;

        if (!_ultima.TryGetValue(addr, out var rec))
        {
            int cur0 = _mem.U8(addr);
            if (cur0 < StatMin || cur0 > StatSaneHi) return;   // wait for a sane natural reading
            // LW-90: see through the mod's own restart residue; lastTarget seeds to the byte
            // as-is either way (residue or natural, both are re-owned on the first check).
            int nat0 = _ledger.FilterCapture(rosterNameId, StatLane.Pa, cur0, level, out int baked0);
            if (baked0 > 0)
                ModLogger.Debug(LogVerb.Growth, $"ultima: restart residue corrected at capture (read {baked0}, natural {nat0})");
            rec = (nat0, cur0, baked0);
        }

        var (hp, maxHp) = ReadHp(_mem, level, brave, faith, rosterNameId);   // (0,0) when no band match -> policy leaves PA natural
        int target = Clamp(UltimaPolicy.PaHeld(rec.natural, hp, maxHp, tier, Tuning.UltimaMul));

        int cur = _mem.U8(addr);
        if (cur == rec.lastTarget || cur == rec.natural
            || (rec.baked > 0 && cur == rec.baked))   // we own it (or the engine normalized -- possibly to the baked residue)
        {
            // Live-verify signal: log when the held multiplier actually moves (an HP band crossed or a
            // kill tier earned) -- NOT every tick. target only changes with band/tier (natural is fixed),
            // so this stays low-volume while HP drifts inside a band.
            if (target != rec.lastTarget)
            {
                int band = UltimaPolicy.Band(hp, maxHp);
                int pct = band < 0 ? 100 : Tuning.UltimaMul[tier][band];
                int hpPct = maxHp > 0 ? (int)(hp * 100L / maxHp) : 0;
                ModLogger.EventWithTrace(LogVerb.Signature,
                    $"{m.Name} at {hpPct} percent health now deals {pct} percent damage (tier {tier}).",
                    $"ultima Physical Attack hold {rec.natural} -> {target} (tier {tier})");
            }
            _ledger.RecordWrite(rosterNameId, StatLane.Pa, target);   // per evaluation (LW-90)
            if (cur != target) _mem.W8(addr, (byte)target);
            _ultima[addr] = (rec.natural, target, rec.baked);
        }
        else _ultima[addr] = (rec.natural, rec.lastTarget, rec.baked);   // foreign value (buff/debuff): leave the byte
    }
}
