using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Tracks the saved JobCommand record (never-re-save invariant) and the slot used for
/// injection. The save is keyed by record id; only the first save is honored (a re-save would
/// overwrite the original bytes with injected state, making restore restore the injection).</summary>
internal sealed class BarrageState
{
    private readonly Dictionary<int, byte[]> _saved = new();
    private int _slotIdx = -1;   // 0-indexed slot used for injection (-1 = not injected)

    /// <summary>True if we have saved the original record bytes for this record id.</summary>
    public bool HasSaved(int recId) => _saved.ContainsKey(recId);

    /// <summary>Save the original record bytes. No-ops if already saved (never-re-save).</summary>
    public void Save(int recId, byte[] record)
    {
        if (_saved.ContainsKey(recId)) return;
        var copy = new byte[record.Length];
        System.Array.Copy(record, copy, record.Length);
        _saved[recId] = copy;
    }

    /// <summary>The saved bytes for the given record id, or null.</summary>
    public byte[]? GetSaved(int recId) => _saved.TryGetValue(recId, out var b) ? b : null;

    /// <summary>The 0-indexed slot currently used for injection, or -1.</summary>
    public int SlotIdx
    {
        get => _slotIdx;
        set => _slotIdx = value;
    }

    /// <summary>Clear saved state (on grant end / job change).</summary>
    public void Clear() { _saved.Clear(); _slotIdx = -1; }
}
