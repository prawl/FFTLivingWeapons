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
/// Do NOT add a value-set tracker to fix this -- over-engineering for a non-existent threat.
/// </summary>
internal sealed partial class GrowthEngine
{
    // PA addr -> (captured natural, the last value WE wrote -- our ownership token).
    private readonly Dictionary<long, (int natural, int lastTarget)> _ultima = new();

    /// <summary>True when this weapon's signature is Ultima -- it owns the wielder's PA at
    /// every tier, so Route declines the PA lane for it.</summary>
    internal static bool OwnsPa(WeaponMeta m) => m.Signature is { Ultima: true };

    /// <summary>Hold the Materia Blade wielder's PA at round(naturalPA × UltimaMul[tier][hpBand]).
    /// Always-on (no tier gate); tier only indexes UltimaMul to raise the whole curve.
    /// Main-hand only (the gift commands from the main hand), guarded, fail-safe no-op.
    /// rosterNameId (D7) threads through to ReadHp's two-tier-with-veto locate.</summary>
    private void HoldUltima(long s, WeaponMeta m, int tier, int level, int brave, int faith, int rosterNameId)
    {
        if (!OwnsPa(m)) return;
        long addr = s + Offsets.CPa;
        if (!_mem.Writable(addr, 1)) return;

        if (!_ultima.TryGetValue(addr, out var rec))
        {
            int cur0 = _mem.U8(addr);
            if (cur0 < StatMin || cur0 > StatSaneHi) return;   // wait for a sane natural reading
            rec = (cur0, cur0);                                 // first sight: own the byte at natural
        }

        var (hp, maxHp) = ReadHp(_mem, level, brave, faith, rosterNameId);   // (0,0) when no band match -> policy leaves PA natural
        int target = Clamp(UltimaPolicy.PaHeld(rec.natural, hp, maxHp, tier, Tuning.UltimaMul));

        int cur = _mem.U8(addr);
        if (cur == rec.lastTarget || cur == rec.natural)   // we own it (or the engine just normalized)
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
            if (cur != target) _mem.W8(addr, (byte)target);
            _ultima[addr] = (rec.natural, target);
        }
        else _ultima[addr] = (rec.natural, rec.lastTarget);   // foreign value (buff/debuff): leave the byte
    }
}
