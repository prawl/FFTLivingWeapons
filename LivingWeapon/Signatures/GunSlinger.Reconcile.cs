using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-193 refund reconcile, watch-only (an armed write-back was rejected in plan review, since a false
/// positive could destroy a real pistol). Replaces the menu-open evaporation edge as the duplication
/// defense. Shape from tools/probes/tapes/lw193_watch_20260817_055131.log. An equip while our twin is
/// off-hand makes the game return it, so its sack count (InventoryCountBase + id) rises. Logs one Info
/// line plus a "twin-refund" flight record. Main-hand change and other rows holding the twin are
/// recorded, not used to filter. No memory writes (GunSlingerConsentTests.cs relies on that).
/// </summary>
internal sealed partial class GunSlinger
{
    private sealed class ReconcileRowMemory
    {
        public ushort NameId;
        public ushort MainHand;
        public ushort OffTwinId;   // 0 if last pass's raw off-hand read did not hold a flagged twin id
        public bool HasOffAtEnd;   // snap.HasOff as of the END of last pass's processing (post-ApplyOffHand)
    }

    // Last-pass memory, in-memory only. Cleared when inBattle flips, per row when the slot's level or
    // nameId changes, and per twin id when its sack read is invalid.
    private readonly Dictionary<int, ReconcileRowMemory> _reconcileRows = new();
    private readonly Dictionary<int, byte> _reconcileSack = new();   // twin id -> last validly-read sack count
    private bool? _reconcileLastInBattle;   // null until the first PrepRoster call ever observes one

    /// <summary>Once per PrepRoster pass, before the roster loop. Clears memory on an inBattle flip and
    /// returns fresh sack reads per twin id, or null while inBattle. An unreadable id is absent.</summary>
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
        long bagBase = _bagCountBase();   // LW-368 round 2. The relocated page once armed, else the vanilla block
        foreach (int id in _twinIds)
        {
            long addr = bagBase + id;
            if (_mem.Readable(addr, 1)) sack[id] = _mem.U8(addr);
        }
        return sack;
    }

    /// <summary>Clears one slot's memory when it goes empty (called from PrepRoster's level==0 and
    /// nameId==0 paths).</summary>
    private void ReconcileForgetRow(int slot) => _reconcileRows.Remove(slot);

    /// <summary>Compares this pass's facts for one row with last pass's, logs on a would-fire, then
    /// re-baselines the row. <paramref name="passRows"/> gives cross-row context.</summary>
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

    /// <summary>Once per pass after every ReconcileRow. Stores this pass's sack reads as the next
    /// baseline; an id with no valid read is dropped rather than compared across a gap.</summary>
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
