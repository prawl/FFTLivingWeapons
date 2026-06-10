using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Stormarc's "Chain Lightning" signature: while a +3 Stormarc is equipped and the wielder's
/// action deals damage to an ENEMY, the nearest OTHER live enemy within 3 Manhattan-distance
/// tiles of the victim takes chip damage = 50% of the original (floor 1), clamped so the
/// bounce target's HP never reaches 0 (the chip NEVER kills -- keeps kill-credit clean).
///
/// DETECTION: two passes per tick. Pass 1 observes every valid band entry (HP diff) and tags
/// each with enemy-side membership (static-array fingerprints -- the EagleEye filter, same
/// frozen-on-restart caveat). Pass 2 bounces each ENEMY damage event off the COMPLETE candidate
/// list (so higher band slots are reachable) and then Consume()s its own write, which is what
/// makes the no-chain guarantee real: our chip is never read back as a fresh event.
///
/// ATTRIBUTION: the acting player is identified by KillTracker._lastPlayerWeapons (the same
/// latch used for kill attribution). Events are processed only while that set contains the
/// Stormarc id and the acted flag is high. While inactive, pass 1 still baselines, so damage
/// dealt outside the wielder's action can never be mis-attributed when it next activates.
/// Every write is VirtualQuery-guarded (Mem.Writable). No raw pointer derefs.
/// </summary>
internal sealed partial class Ricochet
{
    private const int Stormarc = 86;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly RicochetState _state;
    private bool _wasActive;

    public Ricochet(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker)
    {
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
        int tier = Tuning.TierFor(_kills.TryGetValue(Stormarc, out int k) ? k : 0);
        bool active = IsActive(m.Signature, tier) && IsActingMainHand(_tracker._lastPlayerMainHand, Stormarc)
                      && Mem.U8(Offsets.Acted) == 1;
        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info($"ricochet {(active ? "ACTIVE -- Stormarc wielder is acting, chain lightning ready to bounce" : "inactive")}");
        }

        var enemyFps = active ? EnemyFingerprints() : null;
        var slots = new List<SlotInfo>(Offsets.BandSlots);
        var events = new List<(int Slot, int Gx, int Gy, int Dmg)>(2);

        // Pass 1: observe every valid band entry; build the COMPLETE candidate list first.
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
            int mhp = Mem.U16(addr + Offsets.AMaxHp), lvl = Mem.U8(addr + Offsets.ALevel);
            if (mhp < 1 || mhp >= 2000 || lvl < 1 || lvl > 99) continue;
            int br = Mem.U8(addr + Offsets.ABrave), fa = Mem.U8(addr + Offsets.AFaith);
            if (br < 1 || br > 100 || fa < 1 || fa > 100) continue;
            int gx = Mem.U8(addr + Offsets.AGx), gy = Mem.U8(addr + Offsets.AGy);
            if (gx > 30 || gy > 30) continue;
            int hp = Mem.Readable(addr + Offsets.AHp, 2) ? Mem.U16(addr + Offsets.AHp) : 0;

            int dmg = _state.Observe(s, hp);   // always observe: baselining while inactive
            if (!active) continue;

            bool enemy = enemyFps!.Contains((mhp, lvl, br, fa));
            slots.Add(new SlotInfo(s, gx, gy, hp, enemy));
            if (dmg > 0 && enemy) events.Add((s, gx, gy, dmg));
        }
        if (!active) return;

        // Pass 2: bounce each enemy damage event off the full list; consume our own write.
        foreach (var (vs, gx, gy, dmg) in events)
        {
            int target = PickTarget(vs, gx, gy, m.Signature.RicochetRadius, slots);
            if (target < 0) continue;
            long tAddr = Band.Entry(target);
            if (!Mem.Readable(tAddr + Offsets.AHp, 2)) continue;
            int tHp = Mem.U16(tAddr + Offsets.AHp);
            if (tHp <= 0) continue;   // died since pass 1

            int chip = ChipDamage(dmg, m.Signature.RicochetPct);
            ApplyChip(tAddr, tHp, chip);
            int newHp = ClampHp(tHp, chip);
            _state.Consume(target, newHp);   // our write is NOT a damage event (no chains)
            Log.Info($"ricochet: chip damage bounced to the nearest other enemy -- {chip} damage dealt (source hit was {dmg}, target HP {tHp}->{newHp})");
        }
    }

    /// <summary>Enemy fingerprints from the static array (slots 0..EnemySlotMax), the EagleEye
    /// filter. Caveat shared with EagleEye: the static array FREEZES on battle restart.</summary>
    private HashSet<(int mhp, int lvl, int br, int fa)> EnemyFingerprints()
    {
        var set = new HashSet<(int, int, int, int)>();
        for (int s = 0; s <= Offsets.EnemySlotMax; s++)
        {
            long slot = Offsets.ArrayReadBase + (long)s * Offsets.ArrayStride;
            if (!Mem.Readable(slot + Offsets.AMaxHp, 2)) continue;
            int mhp = Mem.U16(slot + Offsets.AMaxHp), lvl = Mem.U8(slot + Offsets.ALevel);
            int br = Mem.U8(slot + Offsets.ABrave), fa = Mem.U8(slot + Offsets.AFaith);
            if (mhp < 1 || mhp > 2000 || lvl < 1 || lvl > 99 || br > 100 || fa > 100) continue;
            set.Add((mhp, lvl, br, fa));
        }
        return set;
    }
}
