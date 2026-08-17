using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-193 owner-AC round: the refund reconcile, WATCH-ONLY per the adversarial plan review's
/// verdict (the armed write-back rule was rejected; the owner accepted interim phantom inflation
/// over the risk of an armed rule destroying a player's REAL pistol on a false positive). This
/// replaces the menu-open evaporation edge as the duplication defense now that the twin stays
/// visibly stamped through every menu (GunSlinger.MenuGate.cs's class doc).
///
/// Detects the tape-proven shape (tools/probes/tapes/lw193_watch_20260817_055131.log, 05:52-
/// 05:56: a lawful equip action performed while OUR twin sits in the off-hand makes the game
/// "return" the conjured item -- sack rises by one phantom): per row, when snap.HasOff was true
/// AND last pass's raw off-hand read held OUR twin id AND this pass's raw off-hand read does NOT
/// AND the twin's sack count (InventoryCountBase + id, guarded u8) rose since last pass, this
/// method emits ONE plain-language Info log line (never Warn -- watch-mode, nothing broke) plus
/// a "twin-refund" flight record carrying the full window: slot, weapon, sack before/after,
/// whether the row's own main hand ALSO changed in the same window, which OTHER roster rows
/// touch the same twin id right now, and inBattle. Per review ruling 3, mainHandChanged and the
/// cross-row context are RECORDED, never used to filter the detection -- watch-only has no
/// destructive consequence to guard against, so there is nothing to be paranoid about excluding.
/// ZERO memory writes anywhere in this file: no sack write, no snapshot mutation. The
/// sack-invariant integration test's premise (GunSlingerConsentTests.cs) stays true this round.
///
/// Lifecycle (review blocker 5): all last-pass memory here is in-memory only (never persisted)
/// and is cleared (a) wholesale, the instant PrepRoster's inBattle param flips (edge-detected
/// here, since GunSlinger has no ResetBattle of its own -- the param edge-detect IS the
/// mechanism); (b) per-row, when that slot's level reads 0 or its nameId changes (a different
/// unit now occupies the slot -- ReconcileForgetRow, called from PrepRoster's own level==0/
/// nameId==0 continues, plus the nameId-mismatch check inside ReconcileRow itself); (c) per twin
/// id, on any pass where that id's sack read is invalid (Readable fails) -- the cached last-known
/// value is dropped rather than compared across an unknown gap.
///
/// Sack reads are gated `!inBattle` throughout (Offsets.InventoryCountBase's own documented
/// no-mid-battle contract) -- ReconcilePrepare returns null while inBattle, which structurally
/// starves the detection's sack-rise condition without a separate guard needed at the call site.
/// </summary>
internal sealed partial class GunSlinger
{
    private sealed class ReconcileRowMemory
    {
        public ushort NameId;
        public ushort MainHand;
        public ushort OffTwinId;   // 0 if last pass's raw off-hand read did NOT hold a flagged twin id
        public bool HasOffAtEnd;   // snap.HasOff as of the END of last pass's processing (post-ApplyOffHand)
    }

    private readonly Dictionary<int, ReconcileRowMemory> _reconcileRows = new();
    private readonly Dictionary<int, byte> _reconcileSack = new();   // twin id -> last validly-read sack count
    private bool? _reconcileLastInBattle;   // null until the first PrepRoster call ever observes one

    /// <summary>Call ONCE per PrepRoster pass, before the roster loop. Handles the inBattle-flip
    /// lifecycle clear, then returns this pass's fresh sack reads for every flagged twin id
    /// (null while inBattle -- the no-mid-battle contract). An id that fails Readable this pass
    /// is simply absent from the returned dictionary, not zero-filled.</summary>
    private Dictionary<int, byte>? ReconcilePrepare(bool inBattle)
    {
        if (_reconcileLastInBattle.HasValue && _reconcileLastInBattle.Value != inBattle)
        {
            _reconcileRows.Clear();
            _reconcileSack.Clear();
        }
        _reconcileLastInBattle = inBattle;

        if (inBattle) return null;

        var sack = new Dictionary<int, byte>();
        foreach (int id in _twinIds)
        {
            long addr = Offsets.InventoryCountBase + id;
            if (_mem.Readable(addr, 1)) sack[id] = _mem.U8(addr);
        }
        return sack;
    }

    /// <summary>Per-row lifecycle clear (blocker 5): called from PrepRoster's own level==0 and
    /// nameId==0 continues, so a slot that just went empty never feeds a stale comparison once it
    /// (or a different unit) reoccupies that index later.</summary>
    private void ReconcileForgetRow(int slot) => _reconcileRows.Remove(slot);

    /// <summary>Compares THIS pass's raw facts for one row against what was captured at the end
    /// of last pass, logs+records on a would-fire, then unconditionally re-baselines the row's
    /// memory for next pass. <paramref name="passRows"/> supplies cross-row context (built by the
    /// caller across the whole roster this same pass) -- see the class doc for exactly what gets
    /// recorded vs. what gets checked.</summary>
    private void ReconcileRow(int slot, ushort nameId, ushort mainH, ushort offH, GunSlingerSnap snap,
        Dictionary<int, byte>? currentSack,
        IReadOnlyList<(int slot, ushort nameId, ushort mainH, ushort offH, GunSlingerSnap snap)> passRows,
        bool inBattle)
    {
        if (_reconcileRows.TryGetValue(slot, out var prev) && prev.NameId == nameId
            && prev.HasOffAtEnd && prev.OffTwinId != 0 && offH != prev.OffTwinId
            && currentSack != null
            && _reconcileSack.TryGetValue(prev.OffTwinId, out byte before)
            && currentSack.TryGetValue(prev.OffTwinId, out byte after)
            && after > before)
        {
            bool mainHandChanged = mainH != prev.MainHand;   // recorded, never filters (review ruling 3)
            var others = new List<int>();
            foreach (var row in passRows)
                if (row.slot != slot && (row.mainH == prev.OffTwinId || row.offH == prev.OffTwinId))
                    others.Add(row.slot);

            ModLogger.Event(LogVerb.Signature,
                "The game returned the conjured twin to the inventory; a phantom copy now exists - watch-mode, nothing was changed");
            _recorder?.Invoke("twin-refund",
                $"slot={slot} weapon={prev.OffTwinId} sack {before}->{after} mainHandChanged={mainHandChanged} otherRows=[{string.Join(",", others)}] inBattle={inBattle}");
        }

        _reconcileRows[slot] = new ReconcileRowMemory
        {
            NameId = nameId,
            MainHand = mainH,
            OffTwinId = _twinIds.Contains(offH) ? offH : (ushort)0,
            HasOffAtEnd = snap.HasOff,
        };
    }

    /// <summary>Call ONCE per pass, after every row's ReconcileRow has run. Commits this pass's
    /// sack reads as next pass's comparison baseline; an id absent from <paramref
    /// name="currentSack"/> (an invalid read, or inBattle) drops out of the cache entirely rather
    /// than being compared across an unknown gap (blocker 5).</summary>
    private void ReconcileCommitSack(Dictionary<int, byte>? currentSack, bool inBattle)
    {
        if (inBattle) return;
        foreach (int id in _twinIds)
        {
            if (currentSack != null && currentSack.TryGetValue(id, out byte v)) _reconcileSack[id] = v;
            else _reconcileSack.Remove(id);
        }
    }
}
