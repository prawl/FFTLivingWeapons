using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Grows every PLAYER's weapon stat, holding it at round(natural*(1+factor)) through
/// the engine's per-battle reset.
///
/// ROSTER-DRIVEN (not a blind sweep): for each populated roster *player* slot we read
/// its Brave/Faith/R-hand, then locate THAT unit's combat struct in the array by
/// fingerprint match. Consequences:
///   - team-safe: only roster units (players) are ever grown -- never enemies, even
///     when an enemy wields the same catalogued weapon id.
///   - relocation-tolerant: the array base shifts per battle, so we SEARCH +/-N slots
///     around the anchor for the fingerprint rather than trusting a fixed offset.
///   - identity-stable: the found struct address is cached per roster slot for the
///     battle, so natural-capture stays tied to one unit.
/// Every speculative read AND every write is VirtualQuery-guarded (Mem.Readable/Writable)
/// so a wrong guess or a freed page can never fault the game.
///
/// NOTE: the combat-struct field map (+0x20 weapon / +0x3E PA / ...) and that a write
/// there actually moves damage are verified for Ramza; treat all-party as live-test-pending.
/// </summary>
internal sealed partial class GrowthEngine
{
    private const int StatMin = 1, StatMax = 255, StatSaneHi = 99, SigStatHi = 199;
    private const int StructSpan = 0x41;   // bytes we touch per combat struct (0x20..0x40)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly TurnTracker _turns;
    private readonly Dictionary<long, (int natural, int target, double factor)> _applied = new();
    private readonly Dictionary<long, int> _timedNatural = new();   // timed-grant stat addr -> captured natural
    private readonly Dictionary<int, long> _structForSlot = new();   // roster slot -> combat-struct base
    private bool _logged;

    public GrowthEngine(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, TurnTracker turns)
    {
        _meta = meta;
        _kills = kills;
        _turns = turns;
    }

    /// <summary>Forget captured naturals + struct locations. Call on battle exit.</summary>
    public void ResetBattle()
    {
        _applied.Clear();
        _timedNatural.Clear();
        _structForSlot.Clear();
        _grantLogged.Clear();
        _heldSupports.Clear();   // no writes: the per-battle struct is gone/rebuilding
        _logged = false;
    }

    /// <summary>One in-battle tick: grow each player's wielded weapon(s) -- BOTH hands.</summary>
    public void Apply()
    {
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            if (!Mem.Readable(rb + Offsets.RNameId, 2)) continue;          // roster slot mapped?
            int level = Mem.U8(rb + Offsets.RLevel);
            if (level < 1 || level > 99) continue;                         // empty slot
            int brave = Mem.U8(rb + Offsets.RBrave);
            int faith = Mem.U8(rb + Offsets.RFaith);

            // BOTH hands: a dual-wielder's OFF-HAND weapon grows + grants its signature too (KillTracker
            // already credits both blades; growth previously read only the right hand, so an off-hand
            // Galewind/Gloomfang silently never fired).
            var hands = Hands(rb);
            if (hands.Count == 0) continue;                                // unarmed / non-overhaul both hands

            long s = LocateStruct(r, brave, faith, hands);
            if (s == 0) continue;

            // Signatures are per-weapon (independent fields); stat growth is shared (one PA/MA/Speed),
            // so take the strongest factor per stat across the hands rather than letting them fight.
            int pickedSupport = Mem.Readable(rb + Offsets.RSupport, 1) ? Mem.U8(rb + Offsets.RSupport) : 0;
            var plan = new Dictionary<long, double>();
            foreach (var (weapon, m) in hands)
            {
                int tier = Tuning.TierFor(_kills.TryGetValue(weapon, out int k) ? k : 0);
                int hp = 0, maxHp = 0;
                if (m.Signature != null && m.Signature.HpBelow > 0)        // only conditional sigs read HP
                    (hp, maxHp) = ReadHp(level, brave, faith);
                HoldSignature(s, r, weapon, m.Name, m.Signature, tier, hp, maxHp, brave, faith, pickedSupport);   // iconic passive (+ read-back log)
                if (m.Signature != null && m.Signature.ForTurns > 0)       // timed flat-stat grant
                    HoldTimedStat(s, m.Signature, tier, _turns.Turns(level, brave, faith));
                if (Route(s, m, tier, out long addr, out double factor))
                    if (!plan.TryGetValue(addr, out double ex) || factor > ex) plan[addr] = factor;
            }
            foreach (var kv in plan) Hold(kv.Key, kv.Value);
        }
        ReleaseUnequipped();   // strip support grants whose weapon left its wielder's hands
    }

    /// <summary>Both hands' overhaul weapons (right + dual-wield off-hand at +0x18), deduped.</summary>
    private List<(int weapon, WeaponMeta m)> Hands(long rb)
    {
        var list = new List<(int, WeaponMeta)>(2);
        foreach (int hw in new[] { Mem.U16(rb + Offsets.RRHand), Mem.U16(rb + Offsets.ROffHand) })
            if (_meta.TryGetValue(hw, out var hm) && !list.Exists(x => x.Item1 == hw))
                list.Add((hw, hm));
        return list;
    }

    /// <summary>Find (and cache) this player's combat struct by fingerprint; guarded reads. The
    /// struct's weapon field (+0x20) holds ONE of the wielded hands (the right hand for a
    /// dual-wielder), so we match it against either hand.</summary>
    private long LocateStruct(int slot, int brave, int faith, List<(int weapon, WeaponMeta m)> hands)
    {
        if (_structForSlot.TryGetValue(slot, out long cached) && Matches(cached, brave, faith, hands))
            return cached;
        for (int n = -Offsets.CombatSearchSlots; n <= Offsets.CombatSearchSlots; n++)
        {
            long s = Offsets.CombatAnchor + (long)n * Offsets.CombatStride;
            if (!Matches(s, brave, faith, hands)) continue;
            _structForSlot[slot] = s;
            if (!_logged) { _logged = true; Log.Info($"growth: found combat struct for party slot {slot} at array anchor{(n >= 0 ? "+" : "")}{n}"); }
            return s;
        }
        return 0;
    }

    /// <summary>True if S is a readable combat struct matching this unit (brave/faith + its weapon
    /// field equals either wielded hand + sane PA/MA).</summary>
    private bool Matches(long s, int brave, int faith, List<(int weapon, WeaponMeta m)> hands)
    {
        if (!Mem.Readable(s, StructSpan)) return false;
        int cw = Mem.U16(s + Offsets.CWeapon);
        if (!hands.Exists(x => x.weapon == cw)) return false;
        if (Mem.U8(s + Offsets.CBrave) != brave || Mem.U8(s + Offsets.CFaith) != faith) return false;
        int pa = Mem.U8(s + Offsets.CPa), ma = Mem.U8(s + Offsets.CMa);
        return pa >= StatMin && pa <= SigStatHi && ma >= StatMin && ma <= SigStatHi;
    }

    /// <summary>Pick the stat address + factor for a weapon, or false to skip.</summary>
    private bool Route(long s, WeaponMeta m, int tier, out long addr, out double factor)
    {
        if (Tuning.SkipFormula(m.Formula)) { addr = 0; factor = 0; return false; }
        if (Tuning.IsSpeedFormula(m.Formula)) { addr = s + Offsets.CSpeed; factor = Tuning.SpeedFactor[tier]; return true; }
        if (Tuning.IsCaster(m.Cat) || Tuning.IsMagicCastFormula(m.Formula)) { addr = s + Offsets.CMa; factor = Tuning.Factor[tier]; return true; }
        addr = s + Offsets.CPa; factor = Tuning.Factor[tier]; return true;
    }

    /// <summary>Hold the stat at target, surviving the per-battle reset. Re-application keys off the
    /// captured record (not the raw value), so a grown stat &gt;99 still bumps on a tier cross, and an
    /// unexpected value (buff/debuff/transient) is left alone rather than re-baselined.</summary>
    private void Hold(long addr, double factor)
    {
        if (!Mem.Writable(addr, 1)) return;
        int cur = Mem.U8(addr);
        if (_applied.TryGetValue(addr, out var e))
        {
            if (cur == e.target) { if (factor != e.factor) WriteTarget(addr, e.natural, factor); return; }
            if (cur == e.natural) WriteTarget(addr, e.natural, factor);   // battle reset -> re-apply
            return;                                                       // anything else: leave it
        }
        if (cur >= StatMin && cur <= StatSaneHi) WriteTarget(addr, cur, factor);   // first sight: capture natural
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
