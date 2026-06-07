using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Holds each tracked unit's growing stat at round(natural * (1 + factor)).
/// Ported from the Python engine's apply_unit_pa: capture the natural value the
/// first time it's seen, re-apply after the battle engine resets it each fight,
/// and bump live when a kill crosses a tier. Built never to run away -- it only
/// re-captures "natural" when the live value is neither our last target nor our
/// stored natural.
///
/// A weapon grows the stat its damage actually keys off: caster gear / magic-cast
/// -> Magick Attack; Speed-formula weapons -> Speed; missing-HP formulas -> no
/// lever; everything else -> Physical Attack.
///
/// Scope: Ramza (roster slot 0, the verified anchor). The rest of the party needs
/// the combat-array slot-0 base; see Offsets.RamzaPa.
/// </summary>
internal sealed class GrowthEngine
{
    private const int StatMin = 1, StatMax = 255, StatSaneHi = 99;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    // stat-address -> (natural value, last target we wrote, factor at that write)
    private readonly Dictionary<long, (int natural, int target, double factor)> _applied = new();

    public GrowthEngine(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills)
    {
        _meta = meta;
        _kills = kills;
    }

    /// <summary>Forget captured naturals. Call on battle exit -- the array is rebuilt next fight.</summary>
    public void ResetBattle() => _applied.Clear();

    /// <summary>One in-battle tick: apply growth for Ramza's wielded weapon.</summary>
    public void Apply()
    {
        int weapon = Mem.U16(Offsets.RosterBase + Offsets.RRHand);   // roster slot 0 R-hand
        if (weapon == 0xFFFF) return;
        int kills = _kills.TryGetValue(weapon, out int k) ? k : 0;
        int tier = Tuning.TierFor(kills);

        if (!Route(weapon, tier, out long addr, out double factor)) return;
        Hold(addr, factor);
    }

    /// <summary>Pick the stat address + factor for a weapon, or false to skip it.</summary>
    private bool Route(int weapon, int tier, out long addr, out double factor)
    {
        _meta.TryGetValue(weapon, out var m);
        int formula = m?.Formula ?? 1;
        string cat = m?.Cat ?? "";

        if (Tuning.SkipFormula(formula)) { addr = 0; factor = 0; return false; }
        if (Tuning.IsSpeedFormula(formula))
        {
            addr = Offsets.RamzaPa + Offsets.SpeedDelta;
            factor = Tuning.SpeedFactor[tier];
            return true;
        }
        if (Tuning.IsCaster(cat) || Tuning.IsMagicCastFormula(formula))
        {
            addr = Offsets.RamzaPa + Offsets.MaDelta;
            factor = Tuning.Factor[tier];
            return true;
        }
        addr = Offsets.RamzaPa;
        factor = Tuning.Factor[tier];
        return true;
    }

    /// <summary>Hold the stat at its target, surviving the engine's per-battle reset.</summary>
    private void Hold(long addr, double factor)
    {
        int cur = Mem.U8(addr);
        if (cur < StatMin || cur > StatSaneHi) return;          // no live/sane stat here right now

        if (!_applied.TryGetValue(addr, out var e))
        {
            WriteTarget(addr, cur, factor);                     // first sight: cur is the natural value
            return;
        }
        if (cur == e.target)
        {
            if (factor != e.factor) WriteTarget(addr, e.natural, factor);   // tier crossed mid-battle
            return;                                                          // else intact, leave it
        }
        if (cur == e.natural)
        {
            WriteTarget(addr, e.natural, factor);               // battle reset to natural -> re-apply
            return;
        }
        WriteTarget(addr, cur, factor);                         // context changed -> treat cur as new natural
    }

    private void WriteTarget(long addr, int natural, double factor)
    {
        int target = (int)Math.Round(natural * (1 + factor));
        if (target < StatMin) target = StatMin;
        if (target > StatMax) target = StatMax;
        if (Mem.U8(addr) != target) Mem.W8(addr, (byte)target);
        _applied[addr] = (natural, target, factor);
    }
}
