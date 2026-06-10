using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Dragon Rod's "Wyrmblood" signature: at each of the +3 wielder's turn edges (TurnTracker),
/// the wielder AND every ALLY within RegenSplashRadius Manhattan tiles regenerate their OWN
/// maxHP/WyrmbloodDiv (the vanilla Regen rate), clamped at full. This is EMULATED regen --
/// the Regen status bit is unmapped and never touched; the heal is a plain guarded HP write
/// on the authoritative band entries (the field Ricochet's chip writes).
///
/// TIMING: TurnTracker's edge is the COMPLETED-turn edge (the rising Acted flag), so the
/// splash lands as the wielder FINISHES acting, with adjacency measured from the post-move
/// tile -- NOT before the wielder moves, as vanilla Regen would tick. Completed turns are
/// the only edge TurnTracker offers (and the mechanism the design prescribes); true
/// start-of-turn semantics would need an active-unit fingerprint watch on the turn queue
/// (TryActiveFingerprint before Acted rises) and is deliberately not attempted here.
///
/// ALLIES ONLY, positively identified: a splash target's fingerprint must match a static-array
/// PLAYER slot (s &gt; EnemySlotMax) -- "not an enemy" would risk healing an uncaptured enemy
/// reinforcement. The dead are never healed (LifeSap.NewHp leaves hp 0 alone -- no accidental
/// revival), and each fingerprint is healed once per splash (band twins). Wielder location =
/// the shared roster resolve + band twin-filter walk (Wielder.cs).
/// </summary>
internal sealed partial class Wyrmblood
{
    private const int DragonRodId = 57;

    private static readonly LiveMemory Live = new();   // Wielder/Band reads ride IGameMemory; == Mem

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly TurnTracker _turns;
    private readonly List<int> _hands = new();
    private int _lastTurns = -1;
    private bool _wasActive;

    public Wyrmblood(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, TurnTracker turns)
    {
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
        if (!_meta.TryGetValue(DragonRodId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierFor(_kills.TryGetValue(DragonRodId, out int k) ? k : 0);
        (int lvl, int br, int fa) fp = default;
        bool active = IsActive(m.Signature, tier) && Wielder.TryResolve(Live, DragonRodId, out fp, _hands);
        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info($"wyrmblood {(active ? "ACTIVE (+3 Dragon Rod wielded)" : "inactive")}");
        }
        if (!active) { _lastTurns = -1; return; }   // re-baseline on re-equip (no stale-diff splash)

        int turns = _turns.Turns(fp.lvl, fp.br, fp.fa);
        bool edge = IsTurnEdge(_lastTurns, turns);
        _lastTurns = turns;
        if (!edge) return;

        long w = Wielder.Locate(Live, DragonRodId, _hands, fp);
        if (w == 0) { Log.Info("wyrmblood: turn edge but wielder unlocated -> no splash"); return; }
        Splash(Live.U8(w + Offsets.AGx), Live.U8(w + Offsets.AGy), m.Signature.RegenSplashRadius, turns);
    }

    /// <summary>One splash: heal every live ALLY band entry within the radius (the wielder is
    /// its own ally at distance 0) by its OWN maxHp/WyrmbloodDiv, once per fingerprint.</summary>
    private void Splash(int wgx, int wgy, int radius, int turn)
    {
        var allies = AllyFingerprints();
        var healed = new HashSet<(int mhp, int lvl, int br, int fa)>();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(Live, e)) continue;
            int gx = Live.U8(e + Offsets.AGx), gy = Live.U8(e + Offsets.AGy);
            if (!InSplash(wgx, wgy, gx, gy, radius)) continue;
            var fp = (mhp: (int)Live.U16(e + Offsets.AMaxHp), lvl: (int)Live.U8(e + Offsets.ALevel),
                      br: (int)Live.U8(e + Offsets.ABrave), fa: (int)Live.U8(e + Offsets.AFaith));
            if (!allies.Contains(fp)) continue;      // never enemies (positive ally match only)
            if (healed.Contains(fp)) continue;       // band twin: one heal per unit
            int hp = Live.U16(e + Offsets.AHp);
            int newHp = LifeSap.NewHp(hp, fp.mhp, RegenAmount(fp.mhp, Tuning.WyrmbloodDiv));
            if (newHp == hp) continue;               // full, or dead (never revive)
            LifeSap.WriteHp(e, newHp);
            healed.Add(fp);
            Log.Info($"wyrmblood: turn {turn} splash -> ally at ({gx},{gy}) hp {hp} -> {newHp} (max {fp.mhp})");
        }
        if (healed.Count == 0) Log.Info($"wyrmblood: turn {turn} splash -> nobody to mend");
    }

    /// <summary>PLAYER-side fingerprints from the static array (slots above EnemySlotMax) --
    /// the positive ally oracle, mirroring the enemy filter EagleEye/Ricochet/Maim use.
    /// Same caveat: the array freezes on battle restart (capture already happened).</summary>
    private HashSet<(int mhp, int lvl, int br, int fa)> AllyFingerprints()
    {
        var set = new HashSet<(int, int, int, int)>();
        for (int s = Offsets.EnemySlotMax + 1; s < Offsets.NSlots; s++)
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
