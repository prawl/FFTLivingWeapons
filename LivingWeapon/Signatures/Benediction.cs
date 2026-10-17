using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Sanctus Staff's "Benediction". While a +3 Sanctus Staff is the last player to act
/// (KillTracker.LastPlayerMainHand == 64), any HP rise on a live ally is boosted by HealBoostPct (30%)
/// with a quiet band HP write after the heal. It mirrors Ricochet, on an ally rise instead of an enemy drop.
/// The gate is the sticky last-player-actor latch, not a timing window. The first design (acted edge plus a
/// grace window) never fired, because charged spells land late (act 10:23:31, heal 10:23:38). The latch
/// persists across enemy turns until the next player acts, and KillTracker.ResetBattle clears it.
/// Cost: any ally HP rise from the wielder's action until the next player acts is boosted (charged Cure,
/// Regen, absorb, item or reaction heal, revive). A revived ally already reads alive when observed, so
/// BandHeal.NewHp's hp&lt;=0 guard only skips units still at 0. If another player acts before a charged
/// heal lands, the boost is lost and never misdirected. That can be common given the ~7 s charge.
/// All reads and writes are guarded.
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

        // Earned, and the Sanctus Staff is the last player to act. The latch is sticky across enemy
        // turns, so it holds through a charged spell's resolve.
        bool active = Benediction.IsActive(m.Signature, tier)
                      && _tracker.LastPlayerMainHand == SanctusStaffId;

        if (ActivationEdge.Step(ref _wasActive, active))
        {
            ModLogger.Debug(LogVerb.Signature, active
                ? $"benediction latch: the Sanctus Staff is the last player to act (tier {tier}); ally HP rises boosted {m.Signature.HealBoostPct} percent"
                : $"benediction latch: another unit now holds the last-actor latch (last player main-hand weapon id {_tracker.LastPlayerMainHand})");
        }

        var allyFps = active ? Band.AllyFingerprints(_mem) : null;
        var boosted = active ? new HashSet<(int mhp, int lvl, int br, int fa)>() : null;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            // Shared sanity read/bounds extracted to Band.TryReadUnit (Band.Sanity.cs).
            if (!Band.TryReadUnit(_mem, s, out long addr, out var fp, out int hp)) continue;

            // HealState mirrors RicochetState for positive deltas. Observe before the early-continue so a
            // heal from before the latch activated is not read as a stale delta.
            int rise = _state.Observe(s, hp);
            if (!active || rise <= 0) continue;

            // Per-fingerprint dedupe as in Renewal/Ricochet. Two allies with identical (mhp,lvl,br,fa)
            // on a group heal collide and only the first is boosted (accepted).
            if (allyFps is null || !allyFps.Contains(fp)) continue;   // allies only (Band.AllyFingerprints, as Renewal)
            if (boosted!.Contains(fp)) continue;                       // band twin: one boost per unit

            int bonus = BonusHeal(rise, m.Signature.HealBoostPct);
            if (bonus <= 0) continue;
            int newHp = BandHeal.NewHp(hp, fp.mhp, bonus);
            if (newHp == hp)
            {
                // overheal / already at max (BandHeal.NewHp also guards hp <= 0 -> a dead ally is never revived)
                ModLogger.Debug(LogVerb.Signature, $"benediction ally heal +{rise} not boosted; band slot {s} already at or near maximum ({hp}/{fp.mhp})");
                continue;
            }

            BandHeal.WriteHp(_mem, addr, newHp);   // band +0x14
            _state.Consume(s, newHp);   // our write is not a heal event (no re-boost)
            boosted!.Add(fp);
            ModLogger.EventWithTrace(LogVerb.Signature,
                $"An ally's heal of {rise} was boosted by {bonus} more ({m.Signature.HealBoostPct} percent); their HP {hp} to {newHp} of {fp.mhp}.",
                $"benediction boost detail (battle slot {s})");
        }
    }
}
