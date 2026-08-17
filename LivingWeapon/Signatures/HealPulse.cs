using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The stateful turn-edge heal-pulse core that Renewal rides. Extracted in LW-153, when Dragon
/// Rod's Wyrmblood splash was Renewal's twin (~85 token-identical hand-synced lines; a
/// name-normalized diff left only the differences that now ride in <see cref="Config"/> --
/// weapon id, which meta knob is the radius, the per-unit heal amount, the range metric, and
/// the narration strings, which are behavior (the evidence chain) and stay byte-identical per
/// weapon). Wyrmblood was deleted as dormant in LW-159, leaving Renewal.cs the one thin config
/// over this core; the core KEEPS the module shape so a future turn-edge healing signature is
/// one Config away, not a re-fold. Renewal.Policy.cs keeps the pure per-module rules the tests
/// drive directly.
///
/// The behavior, unchanged since the fold: at each of the +3 wielder's COMPLETED-turn
/// edges (TurnTracker's rising-Acted edge, adjacency measured from the post-move tile), heal
/// the wielder and every ALLY in range for the config's amount of their OWN max HP, clamped at
/// full. Allies only, positively identified against the static-array PLAYER slots; the dead
/// are never healed (BandHeal.NewHp leaves hp 0 alone); each fingerprint heals once per pulse
/// (band twins). Console gets ONE aggregated summary per pulse; per-ally detail is file-only.
/// </summary>
internal sealed class HealPulse
{
    /// <summary>The per-signature differences; everything absent from here is shared by
    /// construction. Renewal is the sole rider today (see the class doc).</summary>
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
    private readonly ScopedLogger _slog;   // armed gate: a benched +3 weapon must not narrate on console
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

    /// <summary>The wielder's turn edge: a PRIMED TurnTracker count climbed. -1 = unprimed
    /// (first sight after a reset or a re-equip baselines silently). A count that DROPPED
    /// (tracker reset under us) re-baselines instead of pulsing. THE rule; Renewal.Policy's
    /// IsTurnEdge static delegates here (the pre-fold twins carried verbatim copies).</summary>
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

        // LW-252 stage 5: query keyed by this wielder's own roster nameId, resolved off the SAME
        // main-hand row TryResolveMainHand just matched -- Wielder.RosterNameId's -1 (capture
        // failed/ambiguous) falls through to the fp lane exactly like 0 (unseeded) does, so no
        // extra clamping is needed. Query key and credit key must come from the same
        // TurnTracker/WielderKeyedStore law, or the ramp-edge detection below desyncs.
        int nameId = Wielder.RosterNameId(_mem, _cfg.WeaponId, fp);
        int turns = _turns.Turns(nameId, fp.lvl, fp.br, fp.fa);
        bool edge = IsTurnEdge(_lastTurns, turns);
        _lastTurns = turns;
        if (!edge) return;

        long w = Wielder.Locate(_mem, _cfg.WeaponId, _hands, fp);
        if (w == 0) { ModLogger.Warn(LogVerb.Signature, _cfg.WielderMissWarn); return; }
        Pulse(_mem.U8(w + Offsets.AGx), _mem.U8(w + Offsets.AGy), _cfg.Radius(m.Signature));
    }

    /// <summary>One pulse: heal every live ALLY band entry within the radius (the wielder is
    /// its own ally at distance 0) by the config's amount of its OWN maxHp, once per
    /// fingerprint. Console gets ONE aggregated summary per pulse; the per-ally tile/HP detail
    /// is file-only (the old one-Info-line-per-ally shape ate the console ceiling every
    /// wielder turn).</summary>
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
            if (healed.Contains(fp)) continue;       // band twin: one heal per unit
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
