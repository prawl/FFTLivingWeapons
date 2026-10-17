using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Turn-edge heal-pulse core that Renewal rides, extracted when Dragon Rod's Wyrmblood
/// splash (since deleted) was its twin. Per-signature differences live in <see cref="Config"/>.
/// At each completed-turn edge of the +3 wielder, heals the wielder and every ally in range for a
/// fraction of their own max HP, clamped at full. Dead units are skipped and each fingerprint heals
/// once per pulse (band twins). One console summary per pulse; per-ally detail is file-only.
/// Pure rules are in Renewal.Policy.cs.
/// </summary>
internal sealed class HealPulse
{
    /// <summary>Per-signature differences; Renewal is the only user today.</summary>
    internal sealed class Config
    {
        public int WeaponId;
        public Func<WeaponSignature, int> Radius = null!;          // which meta knob (aura vs splash)
        public Func<WeaponSignature?, int, bool> IsActive = null!; // the module's earned-tier rule
        public Func<int, int> Amount = null!;                      // maxHp -> per-unit heal
        public Func<int, int, int, int, int, bool> InRange = null!;// (wx,wy,x,y,radius) metric
        public string ActiveLine = "";                             // activation-edge console pair
        public string InactiveLine = "";
        public string WielderMissWarn = "";                        // turn edge fired, wielder unlocatable
        public string DebugVerb = "";                              // "renewal mended"
        public string NoAlliesDebug = "";                          // empty-pulse file-only line
        public Func<int, int, string> Summary = null!;             // (healedCount, totalMended) console line
    }

    private readonly Config _cfg;
    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly TurnTracker _turns;
    private readonly List<int> _hands = new();
    private readonly ScopedLogger _slog;   // armed gate. A benched +3 weapon must not narrate on console
    private int _lastTurns = -1;
    private bool _wasActive;

    public HealPulse(Config cfg, Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
                     TurnTracker turns, IGameMemory? mem)
    {
        _cfg = cfg;
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _turns = turns;
        _slog = ModLogger.For(LogVerb.Signature, () => Wielder.AnyDeployedMainHand(_mem, _cfg.WeaponId));
    }

    /// <summary>The wielder's turn edge, a primed TurnTracker count that climbed (-1 = unprimed, baselines silently).
    /// A dropped count re-baselines instead of pulsing. Renewal.Policy's IsTurnEdge delegates here.</summary>
    public static bool IsTurnEdge(int lastTurns, int turns) => lastTurns >= 0 && turns > lastTurns;

    public void ResetBattle()
    {
        _lastTurns = -1;
        _wasActive = false;
    }

    public void Tick(bool onField)
    {
        if (!onField) return;
        if (!_meta.TryGetValue(_cfg.WeaponId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierOf(_kills, _cfg.WeaponId);
        (int lvl, int br, int fa) fp = default;
        bool active = _cfg.IsActive(m.Signature, tier) && Wielder.TryResolveMainHand(_mem, _cfg.WeaponId, out fp, _hands);
        if (ActivationEdge.Step(ref _wasActive, active))
            _slog.Info(active ? _cfg.ActiveLine : _cfg.InactiveLine);
        if (!active) { _lastTurns = -1; return; }   // re-baseline on re-equip (no stale-diff pulse)

        // Query keyed by the wielder's roster nameId from the main-hand row TryResolveMainHand matched.
        // A -1 nameId falls through to the fp lane like 0 does. Query and credit keys must share the TurnTracker/WielderKeyedStore law.
        int nameId = Wielder.RosterNameId(_mem, _cfg.WeaponId, fp);
        int turns = _turns.Turns(nameId, fp.lvl, fp.br, fp.fa);
        bool edge = IsTurnEdge(_lastTurns, turns);
        _lastTurns = turns;
        if (!edge) return;

        long w = Wielder.Locate(_mem, _cfg.WeaponId, _hands, fp);
        if (w == 0) { ModLogger.Warn(LogVerb.Signature, _cfg.WielderMissWarn); return; }
        Pulse(_mem.U8(w + Offsets.AGx), _mem.U8(w + Offsets.AGy), _cfg.Radius(m.Signature));
    }

    /// <summary>One pulse that heals every live ally in radius (the wielder counts at distance 0) by the config's
    /// amount of its own maxHp, once per fingerprint. One console summary; per-ally detail is file-only.</summary>
    internal void Pulse(int wgx, int wgy, int radius)
    {
        var allies = Band.AllyFingerprints(_mem);
        var healed = new HashSet<(int mhp, int lvl, int br, int fa)>();
        int totalMended = 0;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) continue;
            int gx = _mem.U8(e + Offsets.AGx), gy = _mem.U8(e + Offsets.AGy);
            if (!_cfg.InRange(wgx, wgy, gx, gy, radius)) continue;
            var fp = (mhp: (int)_mem.U16(e + Offsets.AMaxHp), lvl: (int)_mem.U8(e + Offsets.ALevel),
                      br: (int)_mem.U8(e + Offsets.ABrave), fa: (int)_mem.U8(e + Offsets.AFaith));
            if (!allies.Contains(fp)) continue;      // never enemies (positive ally match only)
            if (healed.Contains(fp)) continue;       // band twin. One heal per unit
            int hp = _mem.U16(e + Offsets.AHp);
            int newHp = BandHeal.NewHp(hp, fp.mhp, _cfg.Amount(fp.mhp));
            if (newHp == hp) continue;               // full, or dead (never revive)
            BandHeal.WriteHp(_mem, e, newHp);
            healed.Add(fp);
            totalMended += newHp - hp;
            ModLogger.Debug(LogVerb.Signature, $"{_cfg.DebugVerb} the ally at ({gx},{gy}) for {newHp - hp} HP (HP {hp} to {newHp}, maximum {fp.mhp})");
        }
        if (healed.Count == 0) ModLogger.Debug(LogVerb.Signature, _cfg.NoAlliesDebug);
        else ModLogger.Event(LogVerb.Signature, _cfg.Summary(healed.Count, totalMended));
    }
}
