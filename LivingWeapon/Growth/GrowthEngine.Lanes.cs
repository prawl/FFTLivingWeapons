using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Multi-lane growth decisions, split out of GrowthEngine.cs. This file decides what to
/// hold (<see cref="GrowthEngine.Routes"/>, the Apply merge helper) and holds the two extra hold
/// kinds, <see cref="GrowthEngine.HoldFlatCapped"/> and <see cref="GrowthEngine.HoldU16"/>.
/// GrowthEngine.cs keeps the original <c>Hold</c>.
/// </summary>
internal sealed partial class GrowthEngine
{
    /// <summary>One address a weapon's baked lane wants held at a given tier. Factor-based
    /// targets (Pa/Ma/Speed, u16 MaxHp) use Factor; flat-capped targets (Brave/Faith current)
    /// use Flat/Cap. U16 marks the 2-byte MaxHp lane and is checked before Cap, since Cap is
    /// nonzero on both MaxHp and the flat-capped lanes.</summary>
    internal readonly record struct LaneRoute(long Addr, double Factor, int Flat, int Cap, StatLane Lane, bool U16);

    /// <summary>The <see cref="LaneRoute"/>s a weapon's baked WeaponMeta.Lane wants at this tier,
    /// before Apply merges per address across both hands. Ownership bails (Afterimage, Ultima,
    /// Mushin) apply per component. See Routes_MateriaBlade_UltimaOwnsPa_MaStillRoutes. An empty
    /// or unknown lane returns an empty list.</summary>
    internal static List<LaneRoute> Routes(long s, WeaponMeta m, int tier)
    {
        var list = new List<LaneRoute>();
        bool skipPa = OwnsPa(m) || OwnsMushin(m);   // Ultima / Mushin own the PA half outright
        bool skipSpeed = OwnsSpeed(m);              // Afterimage owns the Speed lane outright
        switch (m.Lane)
        {
            case "speed":
                if (!skipSpeed) list.Add(new LaneRoute(s + Offsets.CSpeed, Tuning.SpeedFactor[tier], 0, 0, StatLane.Speed, false));
                break;
            case "ma":
                list.Add(new LaneRoute(s + Offsets.CMa, Tuning.Factor[tier], 0, 0, StatLane.Ma, false));
                break;
            case "pa":
                if (!skipPa) list.Add(new LaneRoute(s + Offsets.CPa, Tuning.Factor[tier], 0, 0, StatLane.Pa, false));
                break;
            case "pa+ma":
                if (!skipPa) list.Add(new LaneRoute(s + Offsets.CPa, Tuning.MultiFactor[tier], 0, 0, StatLane.Pa, false));
                list.Add(new LaneRoute(s + Offsets.CMa, Tuning.MultiFactor[tier], 0, 0, StatLane.Ma, false));
                break;
            case "pa+ma+brave":
                if (!skipPa) list.Add(new LaneRoute(s + Offsets.CPa, Tuning.MultiFactor[tier], 0, 0, StatLane.Pa, false));
                list.Add(new LaneRoute(s + Offsets.CMa, Tuning.MultiFactor[tier], 0, 0, StatLane.Ma, false));
                list.Add(new LaneRoute(s + Offsets.CBraveCurrent, 0, Tuning.BraveBonus[tier], Tuning.BraveLaneCap, StatLane.Brave, false));
                break;
            case "hp":
                list.Add(new LaneRoute(s + Offsets.CMaxHp, Tuning.Factor[tier], 0, Tuning.HpCeiling, StatLane.MaxHp, true));
                break;
            case "wp":
                break;   // WpTableHold owns it. Routes contributes no combat-struct lane
            case "wp+faith":
                list.Add(new LaneRoute(s + Offsets.CFaithCurrent, 0, Tuning.FaithBonus[tier], Tuning.FaithLaneCap, StatLane.Faith, false));
                break;
            default:
                break;   // stale/missing lane. No growth, never a guess
        }
        return list;
    }

    /// <summary>When both hands route the same address, the stronger effect wins. U16 is checked
    /// first because MaxHp carries a Cap too; then flat-capped lanes compare Flat and the rest
    /// compare Factor. An address's lane kind is fixed by <see cref="Routes"/>, so the kinds
    /// never collide.</summary>
    internal static LaneRoute MergeRoute(LaneRoute existing, LaneRoute incoming)
    {
        if (existing.U16 || existing.Cap == 0)
            return incoming.Factor > existing.Factor ? incoming : existing;
        return incoming.Flat > existing.Flat ? incoming : existing;
    }

    // addr -> (natural, target, flat, baked). Same shape as Hold's _applied, with the additive
    // bonus kept in its own field instead of reusing Hold's multiplicative factor (N4).
    private readonly Dictionary<long, (int natural, int target, int flat, int baked)> _appliedFlat = new();

    /// <summary>Holds current brave ("pa+ma+brave") or current faith ("wp+faith"). Apply routes
    /// here when the merged Cap is nonzero and the route is not u16. Target = max(natural,
    /// min(natural + flat, cap)), so it never lowers the wielder's own value. Uses the
    /// NaturalLedger like Hold (FilterCapture at first sight, RecordWrite each pass). A cur that
    /// is not natural, the target, or the baked residue is left alone. After a Kobu brave raise
    /// (Kobu.cs) the hold retires for the battle and resumes next battle.</summary>
    internal void HoldFlatCapped(long addr, int flat, int cap, StatLane lane, int nameId, int level)
    {
        if (!_mem.Writable(addr, 1)) return;
        int cur = _mem.U8(addr);
        if (_appliedFlat.TryGetValue(addr, out var e))
        {
            _ledger.RecordWrite(nameId, lane, e.target);
            if (cur == e.target) { if (flat != e.flat) WriteFlatTarget(addr, e.natural, flat, cap, lane, nameId, e.baked); return; }
            if (cur == e.natural || (e.baked > 0 && cur == e.baked))
                WriteFlatTarget(addr, e.natural, flat, cap, lane, nameId, e.baked);   // battle reset / baked normalize -> re-apply
            return;                                                                   // anything else (a Kobu raise): leave it
        }
        if (cur >= StatMin && cur <= StatSaneHi)
        {
            int natural = _ledger.FilterCapture(nameId, lane, cur, level, out int baked);
            WriteFlatTarget(addr, natural, flat, cap, lane, nameId, baked);
        }
    }

    private void WriteFlatTarget(long addr, int natural, int flat, int cap, StatLane lane, int nameId, int baked)
    {
        int target = Math.Max(natural, Math.Min(natural + flat, cap));
        if (target > StatMax) target = StatMax;
        _ledger.RecordWrite(nameId, lane, target);
        if (_mem.U8(addr) != target) _mem.W8(addr, (byte)target);
        _appliedFlat[addr] = (natural, target, flat, baked);
    }

    // addr -> (natural, target, factor) for the u16 "hp" lane. Not routed through the
    // NaturalLedger (HoldU16_RestartResidue_DecisionPin): its capture bounds are
    // u8-shaped. Decision 3 accepted that a battle-restart chain could re-capture the held MaxHp
    // as natural and creep up; the Venombolt poison work (2026-08-26) overturned that, see _u16WriteRecord.
    private readonly Dictionary<long, (int natural, int target, double factor)> _appliedU16 = new();

    // (nameId, lane) -> the last (natural, target) this hold wrote, keyed by roster identity
    // because the combat-struct address changes every battle. Not cleared by ResetBattle. The
    // pre-battle transition (owner-witnessed 2026-08-26) fires several enter/exit resets, each
    // clearing _appliedU16 while the written max stays in game memory, so the next capture
    // read the stale target as natural and compounded (697 -> 906 -> 999).
    private readonly Dictionary<(int nameId, StatLane lane), (int natural, int target)> _u16WriteRecord = new();

    // (nameId, lane) -> pending current-HP top-up (2026-08-26). A genuine
    // HoldU16 capture with a positive delta adds its key here; TryDeliverTopUp clears it once
    // the hp field reads sane. Not cleared by ResetBattle, since the pre-battle transition
    // tears the struct back to natural mid-cycle (healing 209, 697 -> 906, then damage 209,
    // 906 -> 697). A held (filtered) recapture never adds a key, or it would revive the
    // phantom heal.
    private readonly HashSet<(int nameId, StatLane lane)> _pendingTopUp = new();

    // Accepted edge cases (owner call 2026-08-25, tied to the Knight Sword top-up). (1) A tier that
    // first rises between battles opens one battle with the max grown but no top-up, because
    // the record still reads (natural, natural); it self-corrects the next battle. (2) The
    // record is in-memory only and survives PlaythroughReset and battle resets; a stale match
    // causes at most one mis-filtered write. (3) A pending intent surviving a reset could top
    // up once after it; bounded to one delta.

    /// <summary>Sane first-sight u16 read range. StatSaneHi (99) is too low for MaxHp.</summary>
    internal const int U16CaptureMin = 1, U16CaptureMax = 1500;

    /// <summary>Holds a u16 combat-struct field (today only Offsets.CMaxHp) at
    /// min(round(natural * (1 + factor)), ceiling). Same capture-then-check shape as Hold, over
    /// U16/W16, with no NaturalLedger (see _appliedU16); lane and level are accepted only to
    /// match Apply's dispatch. Re-applies at cur == natural, retargets at cur == target when
    /// the factor changes, and leaves any other reading alone. on a first capture, a
    /// cur equal to <see cref="_u16WriteRecord"/>'s last target for (nameId, lane) means the
    /// recorded natural is used and no <see cref="_pendingTopUp"/> is added. nameId &lt;= 0
    /// skips the filter and the top-up. Every branch with a known applied entry ends in
    /// <see cref="TryDeliverTopUp"/> so a top-up missed while hp was unpopulated retries on a
    /// later tick.</summary>
    internal void HoldU16(long addr, double factor, int ceiling, StatLane lane, int nameId, int level)
    {
        if (!_mem.Writable(addr, 2)) return;
        int cur = _mem.U16(addr);
        if (_appliedU16.TryGetValue(addr, out var e))
        {
            if (cur == e.target)
            {
                if (factor != e.factor) WriteU16Target(addr, e.natural, factor, ceiling, lane, nameId);
                var applied = _appliedU16[addr];
                TryDeliverTopUp(addr, applied.natural, applied.target, lane, nameId);
                return;
            }
            if (cur == e.natural)
            {
                WriteU16Target(addr, e.natural, factor, ceiling, lane, nameId);
                var applied = _appliedU16[addr];
                TryDeliverTopUp(addr, applied.natural, applied.target, lane, nameId);
            }
            return;   // anything else. Leave it
        }
        if (cur >= U16CaptureMin && cur <= U16CaptureMax)
        {
            int natural = cur;
            bool held = false;   // true when cur is recognized as OUR OWN prior target, not a real natural
            if (nameId > 0 && _u16WriteRecord.TryGetValue((nameId, lane), out var rec) && cur == rec.target)
            {
                natural = rec.natural;
                held = true;
            }
            WriteU16Target(addr, natural, factor, ceiling, lane, nameId);
            var applied = _appliedU16[addr];
            if (!held && nameId > 0 && applied.target > applied.natural)
                _pendingTopUp.Add((nameId, lane));   // genuine capture. Intent to top up (delivered below, now or later)
            TryDeliverTopUp(addr, applied.natural, applied.target, lane, nameId);
        }
    }

    private void WriteU16Target(long addr, int natural, double factor, int ceiling, StatLane lane, int nameId)
    {
        int target = (int)Math.Round(natural * (1 + factor));
        if (target > ceiling) target = ceiling;
        if (target < 1) target = 1;
        if (_mem.U16(addr) != target) _mem.W16(addr, (ushort)target);
        _appliedU16[addr] = (natural, target, factor);
        // Recorded on every write so a mid-battle tier-up's new target is what a later recapture is filtered against.
        if (nameId > 0) _u16WriteRecord[(nameId, lane)] = (natural, target);
    }

    /// <summary>Raises current HP by the Max HP gain so a Knight Sword wielder does not open the
    /// battle hurt (owner call 2026-08-25). Made a retry on 2026-08-26:
    /// a genuine capture can land while current HP is still unpopulated (read &lt; 1), which
    /// used to drop the heal. It consumes the <see cref="_pendingTopUp"/> intent once hp reads
    /// sane, and returns at once when there is none. Writing current HP is safe because the
    /// engine otherwise leaves it alone (docs/LIVE_LEDGER.md, row
    /// [maxhp-hold-attribution-safe]).</summary>
    private void TryDeliverTopUp(long addr, int natural, int target, StatLane lane, int nameId)
    {
        var key = (nameId, lane);
        if (!_pendingTopUp.Contains(key)) return;
        int delta = target - natural;
        long hpAddr = addr - (Offsets.CMaxHp - Offsets.CHp);   // CHp sits 2 bytes before CMaxHp
        if (!_mem.Writable(hpAddr, 2)) return;                 // stay pending -- retry next call
        int hp = _mem.U16(hpAddr);
        if (hp < 1 || hp > natural) return;   // 0 == KO'd; above natural is anomalous -- stay pending, retry
        int newHp = Math.Min(hp + delta, target);
        if (newHp > hp) _mem.W16(hpAddr, (ushort)newHp);
        _pendingTopUp.Remove(key);   // delivered
    }
}
