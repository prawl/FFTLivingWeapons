namespace LivingWeapon;

/// <summary>The JobCommand record and slot currently holding our injected ability, kept as one pair.
/// <see cref="Set"/> and <see cref="Clear"/> move RecId and SlotIdx together so a slot index is never
/// applied to a different record than the one it was found empty in. Slot-scoped release
/// (Barrage.Policy.ReleaseSlot) needs no record snapshot, so the old Save/HasSaved/GetSaved API is gone.</summary>
internal sealed class BarrageState
{
    private int _recId = -1;
    private int _slotIdx = -1;   // 0-indexed slot used for injection

    /// <summary>The record id the current slot was found in, or -1 if nothing is injected.</summary>
    public int RecId => _recId;

    /// <summary>The 0-indexed injected slot, or -1 if none. Only meaningful with <see cref="RecId"/>.</summary>
    public int SlotIdx => _slotIdx;

    /// <summary>Record the slot just found empty and injected into. Both fields move together.</summary>
    public void Set(int recId, int slotIdx) { _recId = recId; _slotIdx = slotIdx; }

    /// <summary>Forget the injection: the grant ended, the wielder changed job records, or a release
    /// was refused. Called on every grant-end path so the next grant re-runs FindEmptySlot.</summary>
    public void Clear() { _recId = -1; _slotIdx = -1; }
}
