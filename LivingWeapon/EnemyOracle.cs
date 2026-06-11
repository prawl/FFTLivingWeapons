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
/// COVERAGE CHECK: once per battle, logs band-vs-array identity coverage for RE validation
/// (pure logging; retries on an interval until it passes cleanly).
/// </summary>
internal sealed class EnemyOracle
{
    private const int CoverageInterval = 150; // ~5s at 33ms; retry until passes once per battle

    private readonly IGameMemory _mem;
    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _enemyIds = new();

    private int _coveragePollsLeft = CoverageInterval;
    private bool _coverageDone;

    public EnemyOracle(IGameMemory mem) => _mem = mem;

    /// <summary>True when the identity was captured from an enemy-side array slot.</summary>
    public bool Contains((byte lvl, byte br, byte fa, ushort mhp) id) => _enemyIds.Contains(id);

    public void ResetBattle()
    {
        _enemyIds.Clear();
        _coveragePollsLeft = CoverageInterval;
        _coverageDone = false;
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

    /// <summary>Coverage invariant: each captured array identity must appear as a valid band
    /// entry. Pure logging. Marks itself done once the check passes cleanly.</summary>
    private void CheckCoverage()
    {
        int total = 0, found = 0;
        foreach (var id in _enemyIds)
        {
            total++;
            bool seen = false;
            for (int s = 0; s < Offsets.BandSlots; s++)
            {
                long addr = Band.Entry(s);
                if (!Band.IsValid(_mem, addr)) continue;
                if (_mem.U8(addr + Offsets.ALevel) == id.lvl &&
                    _mem.U8(addr + Offsets.ABrave) == id.br &&
                    _mem.U8(addr + Offsets.AFaith) == id.fa &&
                    _mem.U16(addr + Offsets.AMaxHp) == id.mhp) { seen = true; break; }
            }
            if (seen) found++;
            else Log.Info($"kill: WARN identity from the roster snapshot has no match in the live battle band (lvl={id.lvl} br={id.br} fa={id.fa} mhp={id.mhp})");
        }
        Log.Info($"kill: identity coverage check -- {found}/{total} enemies matched between roster snapshot and live battle band");
        if (found == total) _coverageDone = true;
    }
}
