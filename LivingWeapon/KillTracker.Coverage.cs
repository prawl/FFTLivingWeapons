using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Enemy-identity capture (team oracle) and band-vs-array coverage logging.
/// Partial class split from KillTracker.Corpses.cs to stay under the 200-line limit.
///
/// IDENTITY CAPTURE: static-array slots s &lt;= EnemySlotMax supply (lvl,brave,faith,maxHp) into
/// _enemyIds each onField tick. The array is live at battle start; a restart freezes it but the
/// capture already happened. Reinforcements append while the array lives; post-restart
/// reinforcements are a logged, accepted gap.
///
/// COVERAGE CHECK: once per battle, logs band vs array identity coverage for RE validation.
/// </summary>
internal sealed partial class KillTracker
{
    private const int CoverageInterval = 150; // ~5s at 33ms; retry until passes once per battle

    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _enemyIds = new(); // team oracle

    private int _coveragePollsLeft = CoverageInterval;
    private bool _coverageDone;

    private void ResetBattleCoverage()
    {
        _enemyIds.Clear();
        _coveragePollsLeft = CoverageInterval;
        _coverageDone = false;
    }

    /// <summary>Collect identities from static-array enemy slots, by SANE FIELDS -- NOT the
    /// inBattle flag. Live (2026-06-09): that u16 pulses 0/1 per unit mid-battle (half the live
    /// enemies read 0 at any instant), so gating on it dropped those enemies from the oracle and
    /// their kills were refused. The slot-sign (s &lt;= EnemySlotMax) carries the team semantics;
    /// the bounds exclude the junk slots. Called each onField tick; additive: never removes
    /// during a battle (a restart freezes the array but the capture already happened).</summary>
    private void CaptureEnemyIds()
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

    /// <summary>Coverage invariant: each inb==1 array identity must appear as a valid band entry.
    /// Pure logging (no behavior change). Sets _coverageDone once the check passes cleanly.</summary>
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
