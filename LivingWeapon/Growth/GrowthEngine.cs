using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Grows every player's weapon stat, holding it at round(natural*(1+factor)) across the
/// per-battle reset. Roster-driven. For each populated roster player slot it reads Brave/Faith/
/// R-hand, then finds that unit's combat struct by fingerprint, searching +/-N slots around the
/// anchor because the array base shifts per battle. The found address is cached per slot, so
/// enemies are never grown even when they wield the same weapon id. Reads and writes are
/// guarded (Mem.Readable/Writable). The combat-struct field map (+0x20 weapon / +0x3E PA) is
/// confirmed for Ramza only; all-party is still awaiting a live test.
/// </summary>
internal sealed partial class GrowthEngine
{
    // StatMin/StatSaneHi are internal so OwnershipHold.cs shares the bounds.
    internal const int StatMin = 1, StatSaneHi = 99;
    private const int StatMax = 255, SigStatHi = 199;
    internal const int StructSpan = 0x41;   // bytes we touch per combat struct (0x20..0x40); internal so tests cite the real gate span instead of a drift-prone mirror

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly TurnTracker _turns;
    private readonly IGameMemory _mem;
    // `baked` (0 = none) is a restart-residue ownership token. If the post-restart normalize
    // restores the baked baseline instead of the true natural (unverified; Iai's corrective hold is
    // the discriminator), a hold that did not recognize it would go foreign forever.
    private readonly NaturalLedger _ledger;
    private readonly Dictionary<long, (int natural, int target, double factor, int baked)> _applied = new();
    private readonly Dictionary<long, (int nat, int baked)> _timedNatural = new();   // timed-grant stat addr -> captured natural
    // roster slot -> (combat-struct base, tier 1 = nameId-exact, 2 = fingerprint fallback). The tier
    // is cached so LocateStruct revalidates with the predicate that found it.
    private readonly Dictionary<int, (long addr, int tier)> _structForSlot = new();
    private readonly HashSet<int> _ambiguousLogged = new();           // slots whose multi-match was already logged this battle
    private readonly HashSet<int> _fallbackLogged = new();            // slots whose tier-2 fallback was already logged this battle
    private bool _logged;
    // Kiku-ichimonji's Mushin stack count, shared with Mushin.cs (Engine.cs passes one
    // WielderKeyedStore to both). Keyed by nameId, fp fallback, which closes the
    // fp-twin collision. Defaults to an empty store so older call sites compile unchanged.
    private readonly WielderKeyedStore<Box<int>> _mushinArmed;

    public GrowthEngine(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, TurnTracker turns,
                        IGameMemory? mem = null, WielderKeyedStore<Box<int>>? mushinArmed = null,
                        NaturalLedger? ledger = null)
    {
        _meta = meta;
        _kills = kills;
        _turns = turns;
        _mem = mem ?? new LiveMemory();
        _mushinArmed = mushinArmed ?? new WielderKeyedStore<Box<int>>();
        // Engine passes the shared instance; the default is for tests.
        _ledger = ledger ?? new NaturalLedger();
    }

    /// <summary>Forget captured naturals + struct locations. Call on battle exit.</summary>
    public void ResetBattle()
    {
        _applied.Clear();
        _timedNatural.Clear();
        _timedReverted.Clear();
        _timedUnexpectedLogged.Clear();   // a fresh battle re-arms the flood latch
        _afterimage.Clear();
        _ultima.Clear();
        _mushin.Clear();
        _structForSlot.Clear();
        _grantLogged.Clear();
        _heldSupports.Clear();   // no writes. The per-battle struct is gone/rebuilding
        _ambiguousLogged.Clear();
        _fallbackLogged.Clear();
        _appliedFlat.Clear();    // the flat-capped Brave/Faith lanes (GrowthEngine.Lanes.cs)
        _appliedU16.Clear();     // the u16 MaxHp lane (GrowthEngine.Lanes.cs)
        _refillLines = 0;
        _logged = false;
    }

    /// <summary>One in-battle tick. Grow each player's wielded weapon(s) -- both hands.</summary>
    public void Apply()
    {
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            if (!_mem.Readable(rb + Offsets.RNameId, 2)) continue;          // roster slot mapped?
            int rosterNameId = _mem.U16(rb + Offsets.RNameId);              // this slot's roster nameId
            int level = _mem.U8(rb + Offsets.RLevel);
            if (level < 1 || level > 99) continue;                         // empty slot
            int brave = _mem.U8(rb + Offsets.RBrave);
            int faith = _mem.U8(rb + Offsets.RFaith);

            // Both hands. A dual-wielder's off-hand weapon grows and grants its signature too (KillTracker
            // already credits both blades).
            var hands = Hands(rb);
            if (hands.Count == 0) continue;                                // unarmed / non-overhaul both hands

            long s = LocateStruct(r, level, brave, faith, hands, rosterNameId);
            if (s == 0) continue;

            // Stat growth is shared (one PA/MA/Speed), so take the strongest factor per stat across the
            // hands. Signatures fire from the main hand only; stat growth applies from either hand.
            int pickedSupport = _mem.Readable(rb + Offsets.RSupport, 1) ? _mem.U8(rb + Offsets.RSupport) : 0;
            // One RRHand snapshot per slot, so a mid-tick gear change cannot make both hands main-hand.
            int mainHandId = _mem.U16(rb + Offsets.RRHand);
            var plan = new Dictionary<long, LaneRoute>();
            foreach (var (weapon, m) in hands)
            {
                int tier = Tuning.TierOf(_kills, weapon);
                bool isMainHand = weapon == mainHandId;
                if (isMainHand)
                {
                    int hp = 0, maxHp = 0;
                    if (m.Signature != null && m.Signature.HpBelow > 0)    // only conditional sigs read HP
                        (hp, maxHp) = ReadHp(_mem, level, brave, faith, rosterNameId);
                    HoldSignature(s, r, weapon, m.Name, m.Signature, tier, hp, maxHp, brave, faith, pickedSupport);
                    if (m.Signature != null && m.Signature.Mounted)   // mount-gated flat stat grant
                        HoldTimedStat(s, m.Signature, tier, rosterNameId, level);
                    HoldAfterimage(s, m, tier, level, brave, faith, rosterNameId);   // Swiftedge. Ramping Speed (owns the speed lane)
                    HoldUltima(s, m, tier, level, brave, faith, rosterNameId);       // Materia Blade. HP%-scaled PA hold (owns PA lane)
                    HoldMushin(s, m, tier, level, brave, faith, rosterNameId);       // Kiku-ichimonji. One-hit charged-PA hold (owns PA lane)
                }
                // Routes/MergeRoute (GrowthEngine.Lanes.cs) let a weapon want several combat-struct
                // bytes; hands sharing an address merge to the stronger effect.
                foreach (var route in Routes(s, m, tier))
                    plan[route.Addr] = plan.TryGetValue(route.Addr, out var ex) ? MergeRoute(ex, route) : route;
            }
            foreach (var kv in plan)
            {
                var route = kv.Value;
                if (route.U16) HoldU16(kv.Key, route.Factor, route.Cap, route.Lane, rosterNameId, level);
                else if (route.Cap > 0) HoldFlatCapped(kv.Key, route.Flat, route.Cap, route.Lane, rosterNameId, level);
                else Hold(kv.Key, route.Factor, route.Lane, rosterNameId, level);
            }
        }
        ReleaseUnequipped();   // strip support grants whose weapon left its wielder's hands
    }

    /// <summary>Both hands' overhaul weapons (right + dual-wield off-hand at +0x18), deduped.</summary>
    private List<(int weapon, WeaponMeta m)> Hands(long rb)
    {
        var list = new List<(int, WeaponMeta)>(2);
        foreach (int hw in new[] { _mem.U16(rb + Offsets.RRHand), _mem.U16(rb + Offsets.ROffHand) })
            if (_meta.TryGetValue(hw, out var hm) && !list.Exists(x => x.Item1 == hw))
                list.Add((hw, hm));
        return list;
    }

    /// <summary>Hold the stat at target across the per-battle reset. Re-application keys off the
    /// captured record, so a grown stat &gt;99 still bumps on a tier cross, and an unexpected value
    /// (buff/debuff) is left alone. first-sight capture consults the NaturalLedger, and
    /// RecordWrite runs per evaluation so chained restarts match. Internal for the ledger seam tests.
    /// Not converted to the OwnershipHold core (OwnershipHold.cs)
    /// because RecordWrite fires unconditionally; see GrowthEngineCadenceTests.cs.</summary>
    internal void Hold(long addr, double factor, StatLane lane, int nameId, int level)
    {
        if (!_mem.Writable(addr, 1)) return;
        int cur = _mem.U8(addr);
        if (_applied.TryGetValue(addr, out var e))
        {
            _ledger.RecordWrite(nameId, lane, e.target);
            if (cur == e.target) { if (factor != e.factor) WriteTarget(addr, e.natural, factor, lane, nameId, e.baked); return; }
            if (cur == e.natural || (e.baked > 0 && cur == e.baked))
                WriteTarget(addr, e.natural, factor, lane, nameId, e.baked);   // battle reset / baked normalize -> re-apply
            return;                                                            // anything else. Leave it
        }
        if (cur >= StatMin && cur <= StatSaneHi)
        {
            int natural = _ledger.FilterCapture(nameId, lane, cur, level, out int baked);   // first sight
            if (baked > 0)
                ModLogger.Debug(LogVerb.Growth, $"growth: restart residue corrected at capture (read {baked}, natural {natural}, lane {lane})");
            WriteTarget(addr, natural, factor, lane, nameId, baked);
        }
    }

    private void WriteTarget(long addr, int natural, double factor, StatLane lane, int nameId, int baked)
    {
        int target = (int)Math.Round(natural * (1 + factor));
        if (target < StatMin) target = StatMin;
        if (target > StatMax) target = StatMax;
        _ledger.RecordWrite(nameId, lane, target);
        if (_mem.U8(addr) != target) _mem.W8(addr, (byte)target);
        _applied[addr] = (natural, target, factor, baked);
    }
}
