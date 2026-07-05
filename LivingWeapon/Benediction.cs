using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Sanctus Staff's "Benediction" signature: while a +3 Sanctus Staff is the LAST PLAYER to act
/// (KillTracker.LastPlayerMainHand == 64), any HP rise on a live ALLY is boosted by HealBoostPct%
/// (30%) via a quiet post-heal band HP write. This is the mirror of Ricochet -- an HP RISE on an
/// ally, not a drop on an enemy.
///
/// GATE: the sticky last-player-actor latch, NOT a timing window. The first design used the
/// acted-edge + a short grace window and fired ZERO times live: healing spells are CHARGED, so a
/// Cure's HP write lands ~7 seconds AFTER the wielder selects it (live: act 10:23:31, heal 10:23:38),
/// long after any acted window closes. LastPlayerMainHand is set when a player acts and persists
/// across enemy turns until the NEXT PLAYER acts (enemies never touch it), so it stays 64 across the
/// whole charge gap and the boost still fires when the HP finally rises. KillTracker.ResetBattle
/// clears it to 0, so no stale 64 carries across battles.
///
/// THE TRADE-OFF (be honest -- this is WIDER exposure than the old windowed design, accepted as the
/// cost of supporting charged spells): the boost is live for the ENTIRE span from the wielder's
/// action until the next PLAYER acts -- across enemy turns, possibly tens of seconds. ANY ally HP
/// rise in that span is boosted 30%: a charged Cure, Regen ticking, an elemental absorb, an item
/// heal, a reaction heal, a co-equipped Wyrmblood splash, even a revive (we observe HP after the
/// engine applies the heal, so a revived ally already reads alive and IS boosted; only a unit still
/// reading 0 at scan time is skipped, via LifeSap.NewHp's hp&lt;=0 guard, which never fires on the
/// revive path because hp is already positive when we observe). The old claim that Wielder.Locate
/// "closed an enemy-turn hole" was FALSE -- it only narrowed a TIME window; this gate has no time
/// component at all. COMMON MISS (fail-safe): if another PLAYER acts before the charged heal lands,
/// the latch moves off 64 and the boost is silently lost -- never a wrong-target boost. In a real
/// fight the turn queue keeps advancing during the ~7 s charge, so this miss can be common.
///
/// DETECTION: HealState (a RicochetState mirror for positive deltas) observes every valid band slot
/// EVERY tick, whether active or not, so the baseline stays fresh while inactive and a heal that
/// lands on the tick the latch flips active is not read as a giant stale diff.
///
/// ALLIES ONLY, positively identified: Band.AllyFingerprints (static-array PLAYER-slot fingerprints)
/// -- the same oracle as Wyrmblood. Enemies are never boosted.
///
/// HP WRITE: LifeSap.NewHp (clamp at max, never revive) + LifeSap.WriteHp (the proven band +0x14
/// guarded write). A dead ally (HP 0) is left alone.
///
/// BAND-TWIN DEDUPE: per-fingerprint HashSet (same discipline as Wyrmblood/Ricochet) so a frozen
/// band twin doesn't double-boost. Known minor limitation (pre-existing, accepted): two distinct
/// allies with byte-identical (maxHp,level,brave,faith) on a group heal collide and only the first
/// is boosted. The per-slot Consume prevents our own write from being read back as a second event.
/// All reads/writes are VirtualQuery-guarded. No raw pointer derefs.
/// </summary>
internal sealed partial class Benediction : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);
    internal const int SanctusStaffId = 64;

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly HealState _state;
    private bool _wasActive;

    public Benediction(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
                       KillTracker tracker, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
        _state = new HealState(Offsets.BandSlots);
    }

    public void ResetBattle()
    {
        _wasActive = false;
        _state.ResetBattle();
    }

    /// <summary>One in-battle tick. <paramref name="onField"/> gates the scan.</summary>
    public void Tick(bool onField)
    {
        if (!onField) return;
        if (!_meta.TryGetValue(SanctusStaffId, out var m) || m.Signature is null) return;

        int tier = Tuning.TierOf(_kills, SanctusStaffId);

        // The whole gate: the signature is earned AND the Sanctus Staff is the last player to act.
        // LastPlayerMainHand is sticky across enemy turns, so this stays true across a charged spell's
        // multi-second resolve -- the failure a timing window could not cover.
        bool active = Benediction.IsActive(m.Signature, tier)
                      && _tracker.LastPlayerMainHand == SanctusStaffId;

        if (active != _wasActive)
        {
            _wasActive = active;
            ModLogger.Debug(LogVerb.Signature, active
                ? $"benediction latch: the Sanctus Staff is the last player to act (tier {tier}); ally HP rises boosted {m.Signature.HealBoostPct} percent"
                : $"benediction latch: another unit now holds the last-actor latch (last player main-hand weapon id {_tracker.LastPlayerMainHand})");
        }

        var allyFps = active ? Band.AllyFingerprints(_mem) : null;
        var boosted = active ? new HashSet<(int mhp, int lvl, int br, int fa)>() : null;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!_mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
            int mhp = _mem.U16(addr + Offsets.AMaxHp), lvl = _mem.U8(addr + Offsets.ALevel);
            if (mhp < 1 || mhp >= 2000 || lvl < 1 || lvl > 99) continue;
            int br = _mem.U8(addr + Offsets.ABrave), fa = _mem.U8(addr + Offsets.AFaith);
            if (br < 1 || br > 100 || fa < 1 || fa > 100) continue;
            int hp = _mem.Readable(addr + Offsets.AHp, 2) ? _mem.U16(addr + Offsets.AHp) : 0;

            // Always observe (baseline) BEFORE the active/rise early-continue, so a heal that predates
            // the latch flipping active is never read as a giant stale delta when it first activates.
            int rise = _state.Observe(s, hp);
            if (!active || rise <= 0) continue;

            var fp = (mhp, lvl, br, fa);
            // Per-fingerprint dedupe (NOT slot-keying): the proven discipline shared with
            // Wyrmblood/Ricochet. Known minor limitation: two distinct allies with byte-identical
            // (mhp,lvl,br,fa) on a group heal collide and only the first is boosted -- accepted, pre-existing.
            if (allyFps is null || !allyFps.Contains(fp)) continue;   // enemies are never boosted
            if (boosted!.Contains(fp)) continue;                       // band twin: one boost per unit

            int bonus = BonusHeal(rise, m.Signature.HealBoostPct);
            if (bonus <= 0) continue;
            int newHp = LifeSap.NewHp(hp, mhp, bonus);
            if (newHp == hp)
            {
                // overheal / already at max (LifeSap.NewHp also guards hp <= 0 -> a dead ally is never revived)
                ModLogger.Debug(LogVerb.Signature, $"benediction ally heal +{rise} not boosted; band slot {s} already at or near maximum ({hp}/{mhp})");
                continue;
            }

            LifeSap.WriteHp(_mem, addr, newHp);
            _state.Consume(s, newHp);   // our write is not a heal event (no re-boost)
            boosted!.Add(fp);
            ModLogger.EventWithTrace(LogVerb.Signature,
                $"An ally's heal of {rise} was boosted by {bonus} more ({m.Signature.HealBoostPct} percent); their HP {hp} to {newHp} of {mhp}.",
                $"benediction boost detail (battle slot {s})");
        }
    }
}
