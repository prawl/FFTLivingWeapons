namespace LivingWeapon;

/// <summary>Tracks the JobCommand record+slot currently holding our injected ability, as ONE
/// atomic pair -- never a bare slot index. RecId and SlotIdx are set together (<see cref="Set"/>)
/// and cleared together (<see cref="Clear"/>) so a slot index can never be read back and applied
/// against a DIFFERENT record than the one it was found empty in.
///
/// Prior shape: a bare int SlotIdx plus a full-record snapshot dictionary keyed by recId. The
/// snapshot existed only to feed the old whole-record restore; slot-scoped release (see
/// Barrage.Policy.ReleaseSlot) needs no snapshot at all -- it puts the one slot FindEmptySlot found
/// empty back to empty, verifying at release time that it still holds our ability. The snapshot API
/// (Save/HasSaved/GetSaved) is retired with it.</summary>
internal sealed class BarrageState
{
    private int _recId = -1;
    private int _slotIdx = -1;   // 0-indexed slot used for injection

    /// <summary>The record id the current slot was found in, or -1 if nothing is injected.</summary>
    public int RecId => _recId;

    /// <summary>The 0-indexed slot currently used for injection, or -1 if nothing is injected.
    /// Only meaningful together with <see cref="RecId"/> -- never apply this to a different record.</summary>
    public int SlotIdx => _slotIdx;

    /// <summary>Record the slot just found empty and injected into. Both fields move together.</summary>
    public void Set(int recId, int slotIdx) { _recId = recId; _slotIdx = slotIdx; }

    /// <summary>Forget the injection: the grant ended, the wielder's job changed records, or a
    /// release was refused (meaning our bookkeeping is already stale). Both fields move together,
    /// unconditionally -- called on EVERY grant-end path so the next grant always re-runs
    /// FindEmptySlot against fresh memory instead of trusting an old index.</summary>
    public void Clear() { _recId = -1; _slotIdx = -1; }
}
