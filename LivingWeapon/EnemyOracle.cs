using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The enemy-identity team oracle: which (level, brave, faith, maxHp) identities belong to the
/// enemy side. Only identities in here can earn a kill credit -- player corpses, guests, and any
/// uncaptured identity are structurally excluded.
///
/// IDENTITY CAPTURE: static-array slots s &lt;= EnemySlotMax supply identities each onField tick,
/// by SANE FIELDS -- NOT the inBattle flag. Live (2026-06-09): that u16 pulses 0/1 per unit
/// mid-battle (half the live enemies read 0 at any instant), so gating on it dropped those
/// enemies from the oracle and their kills were refused. The slot-sign carries the team
/// semantics; the bounds exclude the junk slots. Additive: never removes during a battle (the
/// array is live at battle start; a restart freezes it but the capture already happened;
/// post-restart reinforcements are a logged, accepted gap).
///
/// COVERAGE CHECK (LW-34, tape-evidenced 2026-07-11): the array capture above ALSO picks up
/// conditional-spawn phantom identities the encounter defines but never fields; they carry sane
/// stats, full HP, and real tiles, so no field-sanity filter can tell them from a real enemy.
/// Only SCHEDULER PARTICIPATION discriminates. So the once-per-battle coverage line counts only
/// EVIDENCED identities: captured ids fed by KillTracker.Corpses.cs's band walk via
/// <see cref="MarkFielded"/> (CT slam or turn-flag participation, debounced) and
/// <see cref="MarkDead"/> (a corpse was definitionally fielded), never every captured array
/// identity. Never-evidenced ids are silently excluded from both the total and the line, logged
/// only as a Debug count. Pure logging; retries on an interval until it passes cleanly. Mid-battle
/// walk-in reinforcements arriving AFTER the line has latched are not re-counted (it prints once
/// per battle, unchanged), but they still enter <see cref="_enemyIds"/> normally and credit fine.
/// </summary>
internal sealed class EnemyOracle
{
    private const int CoverageInterval = 150; // ~5s at 33ms; retry until passes once per battle

    private readonly IGameMemory _mem;
    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _enemyIds = new();
    // LW-34: additive evidence sets fed by KillTracker.Corpses.cs's band walk. An id in EITHER
    // set counts toward coverage; an id in neither is a never-scheduled phantom, excluded from
    // the total entirely (see the class doc).
    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _fielded = new();
    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _died = new();
    // The fielded-evidence total from the PREVIOUS CheckCoverage call (0 = none yet). A clean
    // pass only latches when it agrees with this; see CheckCoverage's doc comment.
    private int _prevTotal;

    private int _coveragePollsLeft = CoverageInterval;
    private bool _coverageDone;
    // Facelift: coverage lines are console-gated on "a Living Weapon is deployed this battle"
    // (KillTracker's sticky latch); the file still gets everything via ScopedLogger's demotion.
    private readonly ScopedLogger _slog;
    // Once-per-identity-per-battle dedup for the unseen-enemy Warning: CheckCoverage re-runs
    // every ~5 seconds until it passes, so a permanently-missing enemy would otherwise re-log
    // (console AND file) for the whole battle. Cleared in ResetBattle.
    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _warnedUnseen = new();
    // The failure variant logs once per battle at Info; retries demote to Debug.
    private bool _coverageFailLogged;

    /// <param name="armed">"Any Living Weapon deployed this battle" (KillTracker's sticky
    /// latch). Null = always armed (test convenience; production always injects).</param>
    public EnemyOracle(IGameMemory mem, Func<bool>? armed = null)
    {
        _mem = mem;
        _slog = ModLogger.For(LogVerb.Credit, armed ?? (() => true));
    }

    /// <summary>True when the identity was captured from an enemy-side array slot.</summary>
    public bool Contains((byte lvl, byte br, byte fa, ushort mhp) id) => _enemyIds.Contains(id);

    /// <summary>LW-34 evidence: KillTracker.Corpses.cs calls this once a band seat's scheduler
    /// participation (CT slam nonzero or turn flag ==1) has held for its 3-tick debounce, with
    /// the realPos filter already applied by the caller. Additive; harmless on an id this oracle
    /// never captured (e.g. a player's own fingerprint), since CheckCoverage only ever
    /// intersects this set against <see cref="_enemyIds"/>.</summary>
    public void MarkFielded((byte lvl, byte br, byte fa, ushort mhp) id) => _fielded.Add(id);

    /// <summary>LW-34 evidence: KillTracker.Corpses.cs calls this at a slot's dead-edge stamp (a
    /// corpse was definitionally fielded), independent of <see cref="MarkFielded"/> so a
    /// credited kill still counts as evidenced even if the corpse later crystallizes or becomes
    /// a chest and leaves the band before the next coverage check.</summary>
    public void MarkDead((byte lvl, byte br, byte fa, ushort mhp) id) => _died.Add(id);

    /// <summary>True once <see cref="CheckCoverage"/> has confirmed every EVIDENCED captured
    /// enemy identity is visible in the band (or died): the once-per-battle "kill: all N enemies
    /// accounted for" edge. Read-only exposure for BattleCensus's fire trigger
    /// (docs/RELIQUARY_AC.md P2).</summary>
    public bool CoverageDone => _coverageDone;

    public void ResetBattle()
    {
        _enemyIds.Clear();
        _fielded.Clear();
        _died.Clear();
        _prevTotal = 0;
        _coveragePollsLeft = CoverageInterval;
        _coverageDone = false;
        _warnedUnseen.Clear();
        _coverageFailLogged = false;
    }

    /// <summary>One onField tick: capture identities, and run the once-per-battle coverage
    /// check when its interval comes due.</summary>
    public void TickField()
    {
        Capture();
        if (!_coverageDone && --_coveragePollsLeft <= 0)
        {
            CheckCoverage();
            _coveragePollsLeft = CoverageInterval;
        }
    }

    private void Capture()
    {
        for (int s = 0; s <= Offsets.EnemySlotMax; s++)
        {
            long slot = Offsets.ArrayReadBase + (long)s * Offsets.ArrayStride;
            byte lvl = _mem.U8(slot + Offsets.ALevel);
            byte br = _mem.U8(slot + Offsets.ABrave);
            byte fa = _mem.U8(slot + Offsets.AFaith);
            ushort mhp = _mem.U16(slot + Offsets.AMaxHp);
            if (lvl < 1 || lvl > 99 || br < 1 || br > 100 || fa < 1 || fa > 100
                || mhp < 1 || mhp >= 2000) continue;
            _enemyIds.Add((lvl, br, fa, mhp));
        }
    }

    /// <summary>True when a captured identity is currently visible as a valid band entry.</summary>
    private bool BandVisible((byte lvl, byte br, byte fa, ushort mhp) id)
    {
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;
            if (_mem.U8(addr + Offsets.ALevel) == id.lvl &&
                _mem.U8(addr + Offsets.ABrave) == id.br &&
                _mem.U8(addr + Offsets.AFaith) == id.fa &&
                _mem.U16(addr + Offsets.AMaxHp) == id.mhp) return true;
        }
        return false;
    }

    /// <summary>Coverage invariant (LW-34 v2): only EVIDENCED captured identities (MarkFielded
    /// or MarkDead, see the class doc) count toward total/found; a never-evidenced id is a
    /// never-scheduled phantom, excluded entirely (logged only as a Debug count on the latching
    /// pass). A MarkDead id counts as found unconditionally (a credited corpse may legitimately
    /// leave the band). A MarkFielded-only id must still be band-visible; if not, the reworded
    /// unseen Warn fires (deduped).
    ///
    /// LATCH STABILITY: because evidence comes FROM the same band walk this check reads, a clean
    /// pass is nearly self-satisfying, so latching on the very first one would freeze a partial
    /// count while evidence is still arriving. The pass only LATCHES (prints the Info line, sets
    /// <see cref="_coverageDone"/>) when found == total, total &gt; 0, AND total matches the
    /// PREVIOUS check's total: two consecutive checks agreeing. A changed total (evidence grew)
    /// updates the remembered total and defers to the next retry with no line printed; total == 0
    /// defers silently (no "All 0" line).</summary>
    private void CheckCoverage()
    {
        int total = 0, found = 0, excluded = 0;
        foreach (var id in _enemyIds)
        {
            bool fielded = _fielded.Contains(id);
            bool died = _died.Contains(id);
            if (!fielded && !died) { excluded++; continue; }   // never-scheduled phantom
            total++;
            if (died) { found++; continue; }   // a corpse may legitimately leave the band afterward
            if (BandVisible(id)) { found++; continue; }
            if (_warnedUnseen.Add(id))
            {
                // A real Warning now (was Info with an embedded "WARN" token), deduped to once
                // per identity per battle; the fingerprint numerics ride a Debug companion.
                _slog.Warn("A fielded enemy is no longer visible in the battle band; its kills may go uncredited.");
                ModLogger.Debug(LogVerb.Trace, $"unseen-enemy detail (level={id.lvl} brave={id.br} faith={id.fa} maximum hit points={id.mhp})");
            }
        }

        if (total == 0) return;   // nothing evidenced yet: defer silently, retry

        bool stableTotal = total == _prevTotal;
        _prevTotal = total;

        if (found == total)
        {
            if (!stableTotal) return;   // the fielded count just grew: wait for one more agreeing pass
            _slog.Info($"All {total} enemies are accounted for; kill credit will be reliable this battle.");
            ModLogger.Debug(LogVerb.Trace, $"coverage detail: {excluded} captured identities excluded as never-scheduled");
            _coverageDone = true;
            return;
        }

        if (!_coverageFailLogged)
        {
            _coverageFailLogged = true;
            _slog.Info($"Only {found} of {total} enemies are accounted for so far; kill credit may be unreliable this battle.");
        }
        else
        {
            // Retry heartbeat: file evidence only, never console (the first failure already spoke).
            ModLogger.Debug(LogVerb.Credit, $"coverage retry: only {found} of {total} enemies accounted for so far");
        }
    }
}
