namespace LivingWeapon;

/// <summary>
/// LW-149 stage A: the ownership-hold core shared by HoldUltima (GrowthEngine.Ultima.cs),
/// HoldMushin (GrowthEngine.Mushin.cs), and HoldAfterimage (GrowthEngine.Afterimage.cs) -- three
/// near-clones that each capture a stat's natural value on first sight (consulting the LW-90
/// NaturalLedger to see through the mod's own restart residue), then on every later tick decide
/// whether they still OWN the byte via the same three-token check (cur == lastTarget, our own
/// last write; cur == natural, the untouched baseline; or cur == baked, the recognized restart
/// residue) or a FOREIGN value (a real buff/debuff) has landed and must be left untouched. This
/// core factors ONLY that shared shape: lane-specific target math (UltimaPolicy.PaHeld,
/// MushinPolicy.PaHeld, AfterimagePolicy.Step/SpeedBonus) and each lane's own record dictionary
/// (the value shape differs -- Afterimage's carries an extra ramp state) stay in the lane files,
/// along with their own per-lane logging. Step reports the branch taken rather than owning any
/// dictionary itself, specifically because Afterimage's record must advance on the FOREIGN
/// branch too (see HoldAfterimage): only the lane knows how to merge that.
///
/// GrowthEngine.Hold/WriteTarget (the plain multiplicative-factor lane, GrowthEngine.cs) is
/// DELIBERATELY NOT wrapped here. It calls NaturalLedger.RecordWrite once, UNCONDITIONALLY,
/// before the ownership check even runs (recording the STALE previous target off the cached
/// record), then WriteTarget records again with the FRESH target on the re-apply/first-capture
/// paths -- a pre-and-post, two-call-site shape none of the three lanes above ever have (they
/// record once, only after the ownership check confirms Captured/Owned, with the freshly
/// computed target). GrowthEngineCadenceTests.cs measured the consequence directly: Ultima/
/// Mushin/Afterimage cost 1 RecordWrite call on an owned tick and 0 on a foreign tick; Hold costs
/// 1 on BOTH (owned and foreign are indistinguishable by call count). Folding Hold into this
/// core's single post-decision record point would need a cadence knob threading "record before,
/// with which stale value" through every call site -- convoluted for the one lane that doesn't
/// share the shape. Hold stays hand-rolled (see its own doc comment in GrowthEngine.cs).
///
/// GrowthEngine.TimedStat.cs is excluded too, for a different reason (see the comment at its own
/// hold): it is a genuinely different machine (mid-battle revert, a post-revert corrective
/// sentinel, an unexpected-read latch, record REMOVAL rather than update) that this core's
/// four-branch capture-or-check shape cannot express without losing behavior.
/// </summary>
internal static class OwnershipHold
{
    /// <summary>What a Step evaluation resolved to. Refused: the address isn't writable, or a
    /// first sight isn't in the sane range yet -- caller's whole Hold* method should no-op this
    /// tick, exactly like the pre-extraction lanes' early `return;`. Captured: no record existed
    /// yet and the sane-range capture (+ ledger consult) just succeeded -- trivially owned this
    /// same tick (nothing can have written the byte between the capture read and the report).
    /// Owned / Foreign: a record already existed and the three-token check accepted / rejected
    /// the current byte.</summary>
    internal enum Branch { Refused, Captured, Owned, Foreign }

    /// <summary>One Step evaluation's report. Cur is the byte value the branch decision was made
    /// against (so a caller never needs a second read to act consistently with the decision).
    /// Natural/Baked mirror TryCapture's out params on Captured, or pass the caller's own record
    /// values through unchanged on Owned/Foreign (both zero, unused, on Refused).</summary>
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

    /// <summary>First-sight capture: wait for a sane reading (GrowthEngine.StatMin..StatSaneHi),
    /// then consult the LW-90 NaturalLedger so a battle-restart's baked-in mod residue is
    /// recognized rather than adopted as natural. Returns false (out params zeroed) when the
    /// current byte isn't in range yet -- caller retries next tick, same as the pre-extraction
    /// lanes' `if (cur0 &lt; StatMin || cur0 &gt; StatSaneHi) return;` guard. Does NOT check
    /// Writable -- Step already gates that once, before calling this.</summary>
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

    /// <summary>The three-token ownership check: our own last write, the untouched natural
    /// baseline, or (only when a correction fired at capture) the recognized baked restart
    /// residue. Do NOT drop the baked clause -- a post-restart re-apply that no longer recognizes
    /// its own residue reads FOREIGN forever (LW-90; see OwnershipHoldTests' non-vacuity pin).</summary>
    internal static bool OwnsCurrentValue(int cur, int lastTarget, int natural, int baked)
        => cur == lastTarget || cur == natural || (baked > 0 && cur == baked);

    /// <summary>Compose the writable gate, capture-or-check, and the three-token test into one
    /// branch report. hasRecord/lastTarget/natural/baked are the caller's own dictionary lookup
    /// (pass zeros for the record fields when hasRecord is false -- they're ignored on that
    /// path).</summary>
    internal static Result Step(IGameMemory mem, NaturalLedger ledger, long addr, StatLane lane,
                                int rosterNameId, int level, bool hasRecord,
                                int lastTarget, int natural, int baked)
    {
        if (!mem.Writable(addr, 1)) return Result.Refusal;

        if (!hasRecord)
        {
            if (!TryCapture(mem, ledger, addr, lane, rosterNameId, level, out int capNatural, out int capBaked))
                return Result.Refusal;
            // Trivially owned: no write can have landed between the capture read (inside
            // TryCapture) and this re-read, so the re-read equals the just-captured value.
            return new Result(Branch.Captured, mem.U8(addr), capNatural, capBaked);
        }

        int cur = mem.U8(addr);
        bool owned = OwnsCurrentValue(cur, lastTarget, natural, baked);
        return new Result(owned ? Branch.Owned : Branch.Foreign, cur, natural, baked);
    }
}
