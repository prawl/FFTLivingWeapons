using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-317: the multi-lane growth decision layer, split out of GrowthEngine.cs (already over the
/// 200-line refactor trigger before this feature) because the real seam here is "deciding what
/// to hold" (this file: <see cref="GrowthEngine.Routes"/>, the Apply plan-merge helper) versus
/// "hold machinery" (also this file: the two new hold kinds, <see cref="GrowthEngine.HoldFlatCapped"/>
/// and <see cref="GrowthEngine.HoldU16"/> -- GrowthEngine.cs keeps the original <c>Hold</c>).
/// </summary>
internal sealed partial class GrowthEngine
{
    /// <summary>One combat-struct (or resident-table-adjacent) target a weapon's baked lane
    /// wants held, at a given tier. Addr is the absolute address; a FACTOR-based target
    /// (Pa/Ma/Speed/the u16 MaxHp lane) reads <see cref="Factor"/> and ignores Flat/Cap==0; a
    /// FLAT-CAPPED target (the Brave/Faith current-stat holds) reads Flat/Cap and ignores
    /// Factor. U16 marks the one 2-byte lane (MaxHp) -- checked FIRST by both
    /// <see cref="MergeRoute"/> and Apply's dispatch, because Cap is nonzero on both the u16
    /// MaxHp lane AND the flat-capped u8 lanes (a bare "Cap &gt; 0" test alone cannot tell them
    /// apart).</summary>
    internal readonly record struct LaneRoute(long Addr, double Factor, int Flat, int Cap, StatLane Lane, bool U16);

    /// <summary>Per-hand lane decision: which <see cref="LaneRoute"/>(s) a weapon's baked
    /// WeaponMeta.Lane wants at this tier, before Apply's per-address merge across both hands.
    /// Ownership bails (Afterimage/Ultima/Mushin) apply PER-COMPONENT, not per-weapon: a
    /// "pa+ma"/"pa+ma+brave" weapon whose signature owns PA (Ultima/Mushin) still routes its MA
    /// (and Brave) components -- see Routes_MateriaBlade_UltimaOwnsPa_MaStillRoutes
    /// (LivingWeapon.Tests). Empty/unknown lane returns an empty list, never a guess, matching
    /// the single-lane Route this method replaces.</summary>
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
                break;   // WpTableHold owns it: Routes contributes no combat-struct lane
            case "wp+faith":
                list.Add(new LaneRoute(s + Offsets.CFaithCurrent, 0, Tuning.FaithBonus[tier], Tuning.FaithLaneCap, StatLane.Faith, false));
                break;
            default:
                break;   // stale/missing lane: no growth, never a guess
        }
        return list;
    }

    /// <summary>Apply's per-address merge: when two hands both route the same combat-struct byte
    /// (dual-wielding two "pa+ma" weapons both want CPa, say), the STRONGER effect wins.
    /// <see cref="LaneRoute.U16"/> is checked first because Cap is nonzero on BOTH the u16 MaxHp
    /// lane (a factor-based target that also carries a ceiling) and the flat-capped u8 lanes
    /// (Brave/Faith) -- a bare "Cap &gt; 0" test alone would compare MaxHp entries by Flat (always
    /// 0) instead of Factor. Past that: flat-capped lanes (Cap &gt; 0) compare by Flat, every other
    /// lane compares by Factor. The three kinds can never collide on one address by construction
    /// (an address's lane kind is fixed by which <see cref="Routes"/> switch case can ever emit
    /// it), so this single three-way branch is exhaustive, not a guess.</summary>
    internal static LaneRoute MergeRoute(LaneRoute existing, LaneRoute incoming)
    {
        if (existing.U16 || existing.Cap == 0)
            return incoming.Factor > existing.Factor ? incoming : existing;
        return incoming.Flat > existing.Flat ? incoming : existing;
    }

    // addr -> (natural, target, flat, baked). Mirrors GrowthEngine.Hold's own _applied shape,
    // but the bonus (flat, an ADD) is tracked in its OWN field rather than overloading Hold's
    // factor field (N4): Hold's factor means "multiply", this lane's own knob means "add".
    private readonly Dictionary<long, (int natural, int target, int flat, int baked)> _appliedFlat = new();

    /// <summary>The "pa+ma+brave" katana lane's CURRENT-brave hold and the "wp+faith" magic-gun
    /// lane's CURRENT-faith hold both dispatch here (Apply picks this method whenever a merged
    /// <see cref="LaneRoute"/>'s Cap is nonzero and it is not the u16 lane). Target = max(natural,
    /// min(natural + flat, cap)) -- ADDITIVE and never lowers the wielder's own current
    /// brave/faith below what it already was. Threads the LW-90 NaturalLedger exactly like Hold
    /// (FilterCapture at first sight, RecordWrite every evaluation) so a battle restart's baked
    /// residue is recognized the same way every other capture-natural lane recognizes it.
    ///
    /// A cur that is neither natural, the recorded target, nor the recognized baked residue is
    /// LEFT ALONE -- the Kobu compose rule: after a Kiyomori bearer's brave-match raise (Kobu.cs)
    /// the byte never reads natural again this battle (Kobu writes past whatever this hold last
    /// held), so this hold silently retires for the battle rather than fighting Kobu's own write.
    /// The bearer's brave simply ends higher than the flat bonus alone would have given it --
    /// harmless, and the hold resumes clean on the next battle.</summary>
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

    // addr -> (natural, target, factor) for the u16 "hp" lane. Deliberately NOT threaded through
    // the LW-90 NaturalLedger (decision 3, pinned by the former HoldU16_RestartResidue_DecisionPin):
    // FilterCapture/RecordWrite are u8-shaped (target bounds 1..255, natural sane range 1..99)
    // and widening them would churn the most battle-tested code in the repo for seven knight
    // swords. Decision 3's ACCEPTED consequence -- across a battle-RESTART chain the held MaxHp
    // can be re-captured as natural and creep upward -- was OVERTURNED 2026-08-26 (LW-330, see
    // _u16WriteRecord below): it is not actually battle-scoped in practice, because the pre-battle
    // transition itself fires several enter/exit resets before the real fight.
    private readonly Dictionary<long, (int natural, int target, double factor)> _appliedU16 = new();

    // (nameId, lane) -> the last (natural, target) this hold WROTE, keyed by roster identity
    // rather than address (the combat-struct address is itself battle-scoped, relocating every
    // battle). Deliberately NOT cleared by ResetBattle -- that is the entire point: owner-
    // witnessed 2026-08-26, the battle-enter sequence fires several full enter/exit cycles before
    // the real fight, and each exit clears _appliedU16 above while the WRITTEN max persists in
    // game memory across them, so the next capture read the stale target as natural and
    // compounded toward the ceiling (697 -> 906 -> 999, phantom top-up heal riding along). This
    // record survives that reset so HoldU16 can recognize "this cur is MY last target" and
    // recover the true natural instead.
    private readonly Dictionary<(int nameId, StatLane lane), (int natural, int target)> _u16WriteRecord = new();

    // (nameId, lane) -> a pending current-HP top-up INTENT (LW-330 stage 2, owner-witnessed
    // 2026-08-26). A genuine HoldU16 capture that computes a positive current-HP delta adds
    // its key here instead of writing the top-up on the spot; TryDeliverTopUp (below) is then
    // called from every HoldU16 branch and clears the key the first time it finds the paired
    // hp field readable and sane. Deliberately NOT cleared by ResetBattle -- same survival
    // rationale as _u16WriteRecord just above: the pre-battle transition tears the transient
    // struct back to natural mid-cycle (tape shape: healing 209, 697 -> 906 of 906, then damage
    // 209, 906 -> 697 of 697, all before the real fight), and the intent must outlive that tear
    // so it can still land once the REAL struct finishes settling a tick or more later. A HELD
    // (filtered) recapture never adds a key -- that is the reset-flicker path, and adding there
    // would revive the exact phantom heal this hold already guards against.
    private readonly HashSet<(int nameId, StatLane lane)> _pendingTopUp = new();

    // Accepted edge cases (owner call 2026-08-25, tied to the LW-327 top-up below). (1) A tier
    // that first rises BETWEEN battles (the kill credits after the prior battle's last growth
    // tick) opens exactly ONE battle with the max grown but the top-up skipped -- at tier 0 the
    // record still reads (natural, natural), so the next capture of that natural filters as HELD,
    // not real, and the top-up never fires; self-corrects the following battle. Cosmetic, accepted.
    // (2) This record is in-memory only and deliberately survives PlaythroughReset/battle resets;
    // the worst stale cases (a true natural matching an old recorded target, or relaunch residue)
    // are bounded to one mis-filtered write and self-arrest on the next real capture. Accepted.
    // (3) A pending intent that survives a reset in the sub-second window before its first
    // delivery could top up once after the reset; bounded to one delta, accepted.

    /// <summary>Sane capture range for a first-sight u16 read: 1..1500, NOT StatSaneHi (99) --
    /// MaxHp routinely sits in the hundreds, so the u8 lanes' sane ceiling would refuse every
    /// real capture.</summary>
    internal const int U16CaptureMin = 1, U16CaptureMax = 1500;

    /// <summary>Hold a u16 combat-struct field (today only Offsets.CMaxHp) at
    /// min(round(natural × (1 + factor)), ceiling). Same capture-then-check idiom as Hold, over
    /// IGameMemory.U16/W16 instead of U8/W8, and with NO NaturalLedger consult (decision 3,
    /// above -- lane/level are still accepted so every hold kind shares one dispatch shape in
    /// Apply, but this lane never reads them for that ledger). nameId IS now read for a second,
    /// narrower purpose: keying <see cref="_u16WriteRecord"/>, the reset-surviving filter (LW-330,
    /// owner-witnessed 2026-08-26 -- see that field's doc for the mechanism). Re-applies on
    /// cur == natural (the engine's per-turn normalize); retargets in place when factor changes
    /// at cur == target (a kill-tier crossed mid-battle); leaves any other reading alone (a real
    /// HP-affecting effect this runtime must never fight) -- all three unchanged from before
    /// LW-330. What changed: on a FIRST capture (no <see cref="_appliedU16"/> entry yet -- e.g.
    /// right after a battle-transition reset), cur is first checked against
    /// <see cref="_u16WriteRecord"/>; if it equals the last value THIS hold itself wrote for this
    /// (nameId, lane), the recorded natural is used instead of cur, and no new
    /// <see cref="_pendingTopUp"/> intent is added (it already ran for this unit's real capture,
    /// and re-adding it is the phantom heal the live defect showed). nameId &lt;= 0 disables the
    /// filter entirely (no identity to key on), preserving the pre-LW-330 behavior for that case,
    /// and also means no top-up intent is ever created or delivered (nothing to key it on).
    /// <see cref="TryDeliverTopUp"/> is called at the end of every branch above that keeps a
    /// known applied entry (capture, held-recapture, re-apply, stable/retarget) -- stage 2 of
    /// LW-330 (owner-witnessed 2026-08-26): the real battle's genuine capture can land at an
    /// instant when the freshly-built combat struct's current-HP field is not yet populated, so
    /// a single-instant top-up attempt silently loses the heal forever. Routing every branch
    /// through TryDeliverTopUp turns that into a retry that lands on whichever later tick first
    /// finds hp readable and sane.</summary>
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
            return;   // anything else: leave it
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
                _pendingTopUp.Add((nameId, lane));   // genuine capture: intent to top up (delivered below, now or later)
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
        // Reset-surviving: recorded on EVERY write (first capture, re-apply, and retarget alike)
        // so a mid-battle tier-up's NEW target is what a later post-reset recapture gets filtered
        // against, not the stale pre-retarget one.
        if (nameId > 0) _u16WriteRecord[(nameId, lane)] = (natural, target);
    }

    /// <summary>So the knight opens the battle with the grown HP real instead of reading hurt
    /// (LW-327, owner call 2026-08-25): a Knight Sword's "hp" lane raises Max HP via HoldU16, but
    /// current HP historically stayed put, so the unit's card read something like 679/883 the
    /// instant the hold first captured. LW-330 stage 2 (owner-witnessed 2026-08-26) replaced the
    /// original one-shot version of this method with a pending-intent retry: the live tape
    /// showed a genuine capture landing at an instant when the freshly-built combat struct's
    /// current-HP field was not yet populated by the game (read &lt; 1), so a single-instant
    /// attempt's KO guard silently ate the heal forever. Now every HoldU16 branch that touches a
    /// known applied entry calls this, and it only CONSUMES the caller's <see cref="_pendingTopUp"/>
    /// intent (added once, at genuine capture) once hp reads sane -- so delivery can land one or
    /// more ticks later than the capture that requested it, exactly the retry tonight's failure
    /// needed. No entry in <see cref="_pendingTopUp"/> for (nameId, lane) is the common case (a
    /// held recapture, an already-delivered capture, or nameId &lt;= 0, which never gets a key at
    /// all) and returns immediately. The engine otherwise leaves current HP alone -- already
    /// established in docs/LIVE_LEDGER.md's Proven section (row [maxhp-hold-attribution-safe])
    /// -- which is exactly what lets a delivery here stick without fighting anything else that
    /// touches the byte.</summary>
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
