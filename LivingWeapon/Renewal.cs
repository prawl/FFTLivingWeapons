using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Mending Staff's "Renewal" signature: at each of the +3 wielder's turn edges (TurnTracker),
/// the wielder AND every ALLY within RegenAuraRadius Chebyshev tiles are healed for
/// round(maxHP * Tuning.RenewalPct) of their OWN max HP, clamped at full. This is a SILENT
/// band +0x14 HP write -- no status icon, no floating number (proven impossible to surface
/// without engine hooks). The Regen status bit is never touched.
///
/// TIMING: TurnTracker's edge is the COMPLETED-turn edge (the rising Acted flag), so the
/// aura fires as the wielder FINISHES acting, with adjacency measured from the post-move
/// tile. Completed turns are the only edge TurnTracker offers and the edge the design
/// prescribes.
///
/// ALLIES ONLY, positively identified: an aura target's fingerprint must match a static-array
/// PLAYER slot (s &gt; EnemySlotMax) -- "not an enemy" would risk healing an uncaptured enemy
/// reinforcement. The dead are never healed (LifeSap.NewHp leaves hp 0 alone -- no accidental
/// revival), and each fingerprint is healed once per pulse (band twins). Wielder location =
/// the shared roster resolve + band twin-filter walk (Wielder.cs).
///
/// CHEBYSHEV (not Manhattan): radius 1 covers all 8 surrounding tiles, including diagonals.
/// This is the key difference from Wyrmblood's Manhattan splash.
///
/// MULTI-WIELDER: if two players equip a +3 Mending Staff simultaneously, TryResolveMainHand
/// returns false (ambiguity) and the aura is suppressed for that tick -- inherited behaviour
/// from the shared wielder-resolve path.
/// </summary>
internal sealed partial class Renewal : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);
    private const int MendingStaffId = 61;

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly TurnTracker _turns;
    private readonly List<int> _hands = new();
    private int _lastTurns = -1;
    private bool _wasActive;

    public Renewal(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, TurnTracker turns, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _turns = turns;
    }

    public void ResetBattle()
    {
        _lastTurns = -1;
        _wasActive = false;
    }

    public void Tick(bool onField)
    {
        if (!onField) return;
        if (!_meta.TryGetValue(MendingStaffId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierOf(_kills, MendingStaffId);
        (int lvl, int br, int fa) fp = default;
        bool active = IsActive(m.Signature, tier) && Wielder.TryResolveMainHand(_mem, MendingStaffId, out fp, _hands);
        if (active != _wasActive)
        {
            _wasActive = active;
            ModLogger.Log($"renewal {(active ? "ACTIVE -- Mending Staff at +3 is wielded, turn-edge regen aura is enabled" : "inactive")}");
        }
        if (!active) { _lastTurns = -1; return; }   // re-baseline on re-equip (no stale-diff aura)

        int turns = _turns.Turns(fp.lvl, fp.br, fp.fa);
        bool edge = IsTurnEdge(_lastTurns, turns);
        _lastTurns = turns;
        if (!edge) return;

        long w = Wielder.Locate(_mem, MendingStaffId, _hands, fp);
        if (w == 0) { ModLogger.Log("renewal: turn ended but the wielder could not be found in memory this tick -- regen aura skipped [locate miss]"); return; }
        Aura(_mem.U8(w + Offsets.AGx), _mem.U8(w + Offsets.AGy), m.Signature.RegenAuraRadius, turns);
    }

    /// <summary>One aura pulse: heal every live ALLY band entry within the radius (the wielder is
    /// its own ally at distance 0) by round(its OWN maxHp * Tuning.RenewalPct), once per fingerprint.</summary>
    private void Aura(int wgx, int wgy, int radius, int turn)
    {
        var allies = Band.AllyFingerprints(_mem);
        var healed = new HashSet<(int mhp, int lvl, int br, int fa)>();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) continue;
            int gx = _mem.U8(e + Offsets.AGx), gy = _mem.U8(e + Offsets.AGy);
            if (!InAura(wgx, wgy, gx, gy, radius)) continue;
            var fp = (mhp: (int)_mem.U16(e + Offsets.AMaxHp), lvl: (int)_mem.U8(e + Offsets.ALevel),
                      br: (int)_mem.U8(e + Offsets.ABrave), fa: (int)_mem.U8(e + Offsets.AFaith));
            if (!allies.Contains(fp)) continue;      // never enemies (positive ally match only)
            if (healed.Contains(fp)) continue;       // band twin: one heal per unit
            int hp = _mem.U16(e + Offsets.AHp);
            int newHp = LifeSap.NewHp(hp, fp.mhp, LifeSap.HealAmount(fp.mhp, Tuning.RenewalPct));
            if (newHp == hp) continue;               // full, or dead (never revive)
            LifeSap.WriteHp(_mem, e, newHp);
            healed.Add(fp);
            ModLogger.Log($"renewal: end-of-turn healing -- ally at ({gx},{gy}) mended {newHp - hp} HP (HP {hp}->{newHp}, max {fp.mhp})");
        }
        if (healed.Count == 0) ModLogger.LogDebug($"renewal: turn-edge aura -- no allies were in range to mend");
    }

}
