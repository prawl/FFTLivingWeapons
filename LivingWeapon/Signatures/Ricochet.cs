using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Stormarc's "Chain Lightning" signature: while a +3 Stormarc is equipped and the wielder's
/// action deals damage to an ENEMY, the bolt arcs through up to RicochetMaxHops further live
/// enemies -- each hop re-centers on the last struck unit and picks the nearest unhit enemy
/// within RicochetRadius tiles. First hop = ricochetPct% of the original damage; each later
/// hop = RicochetHopDecayPct% of the previous hop's chip (floor 1, min HP 1 -- never kills).
///
/// DETECTION: two passes per tick. Pass 1 observes every valid band entry (HP diff) and tags
/// each with enemy-side membership (static-array fingerprints -- the EagleEye filter, same
/// frozen-on-restart caveat). Pass 2 runs a chain per enemy damage event, using a tick-wide
/// struck set to guarantee no unit is chipped twice and no real-hit victim is a chain target.
/// Consume() is called per struck slot so the chip write is never read back as a fresh event.
///
/// ATTRIBUTION: the acting player is identified by KillTracker.LastPlayerWeapons (the same
/// latch used for kill attribution). Events are processed only while that set contains the
/// Stormarc id and the acted flag is high. While inactive, pass 1 still baselines, so damage
/// dealt outside the wielder's action can never be mis-attributed when it next activates.
/// Every write is VirtualQuery-guarded (Mem.Writable). No raw pointer derefs.
/// </summary>
internal sealed partial class Ricochet : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);
    private const int Stormarc = 86;

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly RicochetState _state;
    private bool _wasActive;

    public Ricochet(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker,
                    IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
        _state = new RicochetState(Offsets.BandSlots);
    }

    public void ResetBattle()
    {
        _wasActive = false;
        _state.ResetBattle();
    }

    /// <summary>One in-battle tick. <paramref name="onField"/> gates the scan (same as KillTracker).</summary>
    public void Tick(bool onField)
    {
        if (!onField) return;
        if (!_meta.TryGetValue(Stormarc, out var m) || m.Signature is null) return;
        int tier = Tuning.TierOf(_kills, Stormarc);
        bool active = IsActive(m.Signature, tier) && Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, Stormarc)
                      && _mem.U8(Offsets.Acted) == 1;
        if (ActivationEdge.Step(ref _wasActive, active))
        {
            ModLogger.Debug(LogVerb.Signature, $"ricochet window {(active ? "armed for this action; chain lightning is ready to bounce" : "closed")}");
        }

        var enemyFps = active ? Band.EnemyFingerprints(_mem) : null;
        var slots = new List<SlotInfo>(Offsets.BandSlots);
        var events = new List<(int Slot, int Gx, int Gy, int Dmg)>(2);

        // Pass 1: observe every valid band entry; build the COMPLETE candidate list first.
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            // LW-149: shared sanity read/bounds extracted to Band.TryReadUnit (Band.Sanity.cs).
            // The gx/gy > 30 reject stays caller-side -- it is Ricochet's own extra gate, not
            // part of the shared core the other six callers share.
            if (!Band.TryReadUnit(_mem, s, out long addr, out var fp, out int hp)) continue;
            int gx = _mem.U8(addr + Offsets.AGx), gy = _mem.U8(addr + Offsets.AGy);
            if (gx > 30 || gy > 30) continue;

            int dmg = _state.Observe(s, hp);   // always observe: baselining while inactive
            if (!active) continue;

            bool enemy = enemyFps!.Contains(fp);
            slots.Add(new SlotInfo(s, gx, gy, hp, enemy));
            if (dmg > 0 && enemy) events.Add((s, gx, gy, dmg));
        }
        if (!active) return;

        // Pass 2: chain each enemy damage event; tick-wide struck set prevents double-hits.
        var struck = new HashSet<int>();
        foreach (var ev in events) struck.Add(ev.Slot);   // real-hit victims are never chip targets
        foreach (var (vs, gx, gy, dmg) in events)
        {
            var chain = PickChain(gx, gy, m.Signature.RicochetRadius, Tuning.RicochetMaxHops, slots, struck);
            int hopIndex = 0;
            foreach (int target in chain)
            {
                struck.Add(target);
                long tAddr = Band.Entry(target);
                if (_mem.Readable(tAddr + Offsets.AHp, 2))
                {
                    int tHp = _mem.U16(tAddr + Offsets.AHp);
                    if (tHp > 0)
                    {
                        int chip = ChipForHop(dmg, m.Signature.RicochetPct, Tuning.RicochetHopDecayPct, hopIndex);
                        ApplyChip(_mem, tAddr, tHp, chip);
                        int newHp = ClampHp(tHp, chip);
                        _state.Consume(target, newHp);
                        ModLogger.EventWithTrace(LogVerb.Signature,
                            $"Chain lightning hop {hopIndex + 1} struck the next enemy for {chip} damage, from a {dmg} source hit (HP {tHp} to {newHp}).",
                            $"ricochet hop detail (battle slot {target})");
                    }
                }
                hopIndex++;
            }
        }
    }

}
