namespace LivingWeapon;

/// <summary>
/// Ownership-hold core shared by HoldUltima, HoldMushin and HoldAfterimage
/// (GrowthEngine.Ultima/Mushin/Afterimage.cs). Each captures a stat's natural value on first
/// sight through the NaturalLedger, then owns the byte while cur is its last write, the
/// natural value, or the baked restart residue; anything else is a foreign buff and is left alone.
/// GrowthEngine.Hold records to the ledger before the ownership check, so it stays hand-rolled
/// (GrowthEngineCadenceTests.cs). GrowthEngine.TimedStat.cs is a different machine (mid-battle revert).
/// </summary>
internal static class OwnershipHold
{
    /// <summary>Step outcome. Refused. Byte not writable or not yet in the sane range, so the caller
    /// skips the tick. Captured. First sight, owned this tick. Owned/Foreign. The three-token check result.</summary>
    internal enum Branch { Refused, Captured, Owned, Foreign }

    /// <summary>One Step report. Cur is the value the decision was made against; Natural/Baked are
    /// the capture results on Captured, the caller's record values otherwise, zero on Refused.</summary>
    internal readonly struct Result
    {
        internal readonly Branch Branch;
        internal readonly int Cur;
        internal readonly int Natural;
        internal readonly int Baked;

        internal Result(Branch branch, int cur, int natural, int baked)
        {
            Branch = branch;
            Cur = cur;
            Natural = natural;
            Baked = baked;
        }

        internal static readonly Result Refusal = new(Branch.Refused, 0, 0, 0);
    }

    /// <summary>First-sight capture. Wait for a sane reading (GrowthEngine.StatMin..StatSaneHi), then
    /// consult the NaturalLedger so battle-restart residue is not adopted as natural. Returns
    /// false (outs zeroed) while the byte is out of range. Step checks Writable first.</summary>
    internal static bool TryCapture(IGameMemory mem, NaturalLedger ledger, long addr, StatLane lane,
                                    int rosterNameId, int level, out int natural, out int baked)
    {
        natural = 0;
        baked = 0;
        int cur0 = mem.U8(addr);
        if (cur0 < GrowthEngine.StatMin || cur0 > GrowthEngine.StatSaneHi) return false;
        natural = ledger.FilterCapture(rosterNameId, lane, cur0, level, out baked);
        return true;
    }

    /// <summary>Three-token ownership check. Our last write, the natural baseline, or the baked
    /// restart residue. Dropping the baked clause reads our own residue as foreign (OwnershipHoldTests).</summary>
    internal static bool OwnsCurrentValue(int cur, int lastTarget, int natural, int baked)
        => cur == lastTarget || cur == natural || (baked > 0 && cur == baked);

    /// <summary>Writable gate, capture-or-check and the three-token test in one branch report.
    /// The record fields are ignored when hasRecord is false.</summary>
    internal static Result Step(IGameMemory mem, NaturalLedger ledger, long addr, StatLane lane,
                                int rosterNameId, int level, bool hasRecord,
                                int lastTarget, int natural, int baked)
    {
        if (!mem.Writable(addr, 1)) return Result.Refusal;

        if (!hasRecord)
        {
            if (!TryCapture(mem, ledger, addr, lane, rosterNameId, level, out int capNatural, out int capBaked))
                return Result.Refusal;
            // No write can land between the capture read and this re-read.
            return new Result(Branch.Captured, mem.U8(addr), capNatural, capBaked);
        }

        int cur = mem.U8(addr);
        bool owned = OwnsCurrentValue(cur, lastTarget, natural, baked);
        return new Result(owned ? Branch.Owned : Branch.Foreign, cur, natural, baked);
    }
}
