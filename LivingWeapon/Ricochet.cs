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
        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info($"ricochet {(active ? "ACTIVE -- Stormarc wielder is acting, chain lightning ready to bounce" : "inactive")}");
        }

        var enemyFps = active ? Band.EnemyFingerprints(_mem) : null;
        var slots = new List<SlotInfo>(Offsets.BandSlots);
        var events = new List<(int Slot, int Gx, int Gy, int Dmg)>(2);

        // Pass 1: observe every valid band entry; build the COMPLETE candidate list first.
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!_mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
            int mhp = _mem.U16(addr + Offsets.AMaxHp), lvl = _mem.U8(addr + Offsets.ALevel);
            if (mhp < 1 || mhp >= 2000 || lvl < 1 || lvl > 99) continue;
            int br = _mem.U8(addr + Offsets.ABrave), fa = _mem.U8(addr + Offsets.AFaith);
            if (br < 1 || br > 100 || fa < 1 || fa > 100) continue;
            int gx = _mem.U8(addr + Offsets.AGx), gy = _mem.U8(addr + Offsets.AGy);
            if (gx > 30 || gy > 30) continue;
            int hp = _mem.Readable(addr + Offsets.AHp, 2) ? _mem.U16(addr + Offsets.AHp) : 0;

            int dmg = _state.Observe(s, hp);   // always observe: baselining while inactive
            if (!active) continue;

            bool enemy = enemyFps!.Contains((mhp, lvl, br, fa));
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
                        Log.Info($"ricochet: chain lightning hop {hopIndex + 1} -- {chip} chip to slot {target} (source hit {dmg}, HP {tHp}->{newHp})");
                    }
                }
                hopIndex++;
            }
        }
    }

}
