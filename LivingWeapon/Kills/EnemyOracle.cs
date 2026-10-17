using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Which (level, brave, faith, maxHp) identities belong to the enemy side. Only these can earn a
/// kill credit, so player corpses, guests and uncaptured identities are excluded.
///
/// Capture reads static-array slots up to EnemySlotMax each onField tick and filters by sane field
/// values. The inBattle flag is unusable. It pulses 0/1 per unit mid-battle (2026-06-09), which
/// dropped half the enemies. Capture only adds during a battle; post-restart reinforcements are a
/// logged, accepted gap.
///
/// The coverage check (LW-34, 2026-07-11) logs once per battle that all enemies are accounted for.
/// The array also holds conditional-spawn phantoms that look like real enemies, and only scheduler
/// participation tells them apart, so only identities marked by <see cref="MarkFielded"/> or
/// <see cref="MarkDead"/> (fed by KillTracker.Corpses.cs's band walk) are counted. Others are
/// excluded and logged as a Debug count. Reinforcements arriving after the line latches are not
/// re-counted but still credit normally.
/// </summary>
internal sealed class EnemyOracle
{
    private const int CoverageInterval = 150; // ~5s at 33ms; retry until passes once per battle

    private readonly IGameMemory _mem;
    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _enemyIds = new();
    // Evidence sets fed by KillTracker.Corpses.cs's band walk. An id in neither is a
    // phantom and is left out of the total (see the class doc).
    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _fielded = new();
    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _died = new();
    // Total from the previous CheckCoverage call (0 = none yet); a pass latches only if it agrees.
    private int _prevTotal;

    private int _coveragePollsLeft = CoverageInterval;
    private bool _coverageDone;
    // Facelift. Coverage lines reach the console only once a Living Weapon is deployed; the file gets
    // everything via ScopedLogger's demotion. the armed latch usually rises after the success
    // line latches (97s late, 2026-07-11), so a demoted success line is kept and replayed (see TickField).
    private readonly ScopedLogger _slog;
    private readonly Func<bool> _armed;
    private string? _pendingConsoleLine;
    // Dedup for the unseen-enemy Warning, since CheckCoverage retries every ~5s. Cleared in ResetBattle.
    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _warnedUnseen = new();
    // The failure line logs once per battle at Info; retries go to Debug.
    private bool _coverageFailLogged;

    /// <param name="armed">KillTracker's "Living Weapon deployed this battle" latch; null means always armed (tests).</param>
    public EnemyOracle(IGameMemory mem, Func<bool>? armed = null)
    {
        _mem = mem;
        _armed = armed ?? (() => true);
        _slog = ModLogger.For(LogVerb.Credit, _armed);
    }

    /// <summary>True if the identity was captured from an enemy-side array slot.</summary>
    public bool Contains((byte lvl, byte br, byte fa, ushort mhp) id) => _enemyIds.Contains(id);

    /// <summary>LW-34 evidence. Called once a band seat's scheduler participation (CT slam nonzero or
    /// turn flag ==1) has held for 3 ticks. Harmless for ids never captured, since CheckCoverage
    /// intersects this set with <see cref="_enemyIds"/>.</summary>
    public void MarkFielded((byte lvl, byte br, byte fa, ushort mhp) id) => _fielded.Add(id);

    /// <summary>LW-34 evidence. Called at a slot's dead-edge stamp. Separate from
    /// <see cref="MarkFielded"/> so a kill still counts if the corpse leaves the band first.</summary>
    public void MarkDead((byte lvl, byte br, byte fa, ushort mhp) id) => _died.Add(id);

    /// <summary>True once <see cref="CheckCoverage"/> has seen every evidenced enemy in the band or
    /// dead. BattleCensus uses it as a trigger (docs/RELIQUARY_AC.md P2).</summary>
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
        _pendingConsoleLine = null;
    }

    /// <summary>One onField tick. Capture identities and run the coverage check when due.</summary>
    public void TickField()
    {
        Capture();
        if (!_coverageDone && --_coveragePollsLeft <= 0)
        {
            CheckCoverage();
            _coveragePollsLeft = CoverageInterval;
        }
        // Replay the demoted success line to the console once the armed latch rises.
        if (_coverageDone && _pendingConsoleLine != null && SafeArmed())
        {
            ModLogger.Event(LogVerb.Credit, _pendingConsoleLine);
            _pendingConsoleLine = null;
        }
    }

    /// <summary>Like ScopedLogger.SafeArmed. A throwing predicate must not kill the tick.</summary>
    private bool SafeArmed()
    {
        try { return _armed(); } catch { return false; }
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

    /// <summary>True if a captured identity is currently a valid band entry.</summary>
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

    /// <summary>Coverage check (LW-34 v2). Only evidenced identities count. A MarkDead id counts as
    /// found (the corpse may have left the band); a MarkFielded-only id must still be band-visible
    /// or the unseen Warn fires, deduped. Evidence comes from the same band walk this reads, so the
    /// first clean pass would latch a partial count. It latches (Info line, <see cref="_coverageDone"/>)
    /// only when found == total, total &gt; 0 and the total matches the previous check. A changed or
    /// zero total defers silently.</summary>
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
                // The fingerprint numerics ride a Debug companion.
                _slog.Warn("A fielded enemy is no longer visible in the battle band; its kills may go uncredited.");
                ModLogger.Debug(LogVerb.Trace, $"unseen-enemy detail (level={id.lvl} brave={id.br} faith={id.fa} maximum hit points={id.mhp})");
            }
        }

        if (total == 0) return;   // nothing evidenced yet. Defer silently, retry

        bool stableTotal = total == _prevTotal;
        _prevTotal = total;

        if (found == total)
        {
            if (!stableTotal) return;   // the fielded count just grew. Wait for one more agreeing pass
            string line = $"All {total} enemies are accounted for; kill credit will be reliable this battle.";
            _slog.Info(line);
            // Only the success line gets the console replay.
            if (!SafeArmed()) _pendingConsoleLine = line;
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
            // Retry heartbeat for the file only; the first failure already spoke.
            ModLogger.Debug(LogVerb.Credit, $"coverage retry: only {found} of {total} enemies accounted for so far");
        }
    }
}
