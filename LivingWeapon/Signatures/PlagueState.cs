using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Per-victim tracking for the Plague latch: band address (primary key), fingerprint
/// (validity check), last-seen CT, and addr-based operations for independent per-slot state.
/// Multiple enemies can be poisoned simultaneously (e.g. AoE wielder). The addr key prevents
/// two same-fingerprint units from cross-clobbering each other's CT tracking.
/// Split out of Plague.Policy.cs (LW-92): a separately-nameable, separately-tested type, not a
/// fabricated seam (the pure Plague.* policy statics that stayed behind have no relation to
/// this class's mutable per-victim state).</summary>
internal sealed class PlagueState
{
    private readonly Dictionary<long, PlagueEntry> _held = new();

    // ---- addr-primary API (hot path in Plague.cs) ----

    /// <summary>True when the given band slot address is currently latched.</summary>
    public bool IsHeldAt(long addr) => _held.ContainsKey(addr);

    /// <summary>The fingerprint stored for a held address, or default.</summary>
    public (int mhp, int lvl, int br, int fa) FpAt(long addr)
        => _held.TryGetValue(addr, out var e) ? e.Fp : default;

    /// <summary>Latch a newly poisoned victim at the given band-slot address.
    /// No-ops if the address is already held. Seeds LastCt with the victim's current CT
    /// to prevent a phantom augment on the first tick.</summary>
    public void Latch(long addr, (int mhp, int lvl, int br, int fa) fp, int seedCt = 0)
    {
        if (_held.ContainsKey(addr)) return;
        _held[addr] = new PlagueEntry(Fp: fp, LastCt: seedCt);
    }

    /// <summary>The last CT observation for a held address.</summary>
    public int LastCtAt(long addr)
        => _held.TryGetValue(addr, out var e) ? e.LastCt : 0;

    /// <summary>Update the last CT for a held address.</summary>
    public void UpdateCtAt(long addr, int ct)
    {
        if (_held.TryGetValue(addr, out var e)) _held[addr] = e with { LastCt = ct };
    }

    /// <summary>Re-anchor the fingerprint stored for a held address to newly observed values
    /// (a mid-battle level-up accepted by Plague.SameVictim), preserving the CT tracking state.
    /// No-ops if the address is not held. Keeps the drift budget from accumulating across
    /// repeated level-ups (LW-92): each accepted step re-baselines instead of stacking.</summary>
    public void RelatchFp(long addr, (int mhp, int lvl, int br, int fa) fp)
    {
        if (_held.TryGetValue(addr, out var e)) _held[addr] = e with { Fp = fp };
    }

    /// <summary>Remove the latch for a band-slot address (fingerprint mismatch / unequip).</summary>
    public void ReleaseAt(long addr) => _held.Remove(addr);

    /// <summary>All currently held band-slot addresses (for drive iteration).</summary>
    public IEnumerable<long> HeldAddrs => _held.Keys;

    /// <summary>Clear all latches (battle exit).</summary>
    public void Clear() => _held.Clear();

    // ---- fp-keyed convenience wrappers (test API + Drive drop-check) ----

    /// <summary>True when any held entry has the given fingerprint (linear scan; test convenience).</summary>
    public bool IsHeld((int mhp, int lvl, int br, int fa) fp)
    {
        foreach (var e in _held.Values) if (e.Fp.Equals(fp)) return true;
        return false;
    }

    /// <summary>The band address stored for the first held entry matching the fingerprint, or 0.</summary>
    public long HeldAddr((int mhp, int lvl, int br, int fa) fp)
    {
        foreach (var kv in _held) if (kv.Value.Fp.Equals(fp)) return kv.Key;
        return 0;
    }

    /// <summary>The last CT for the first held entry matching the fingerprint (test convenience).</summary>
    public int LastCt((int mhp, int lvl, int br, int fa) fp)
    {
        foreach (var kv in _held) if (kv.Value.Fp.Equals(fp)) return kv.Value.LastCt;
        return 0;
    }

    /// <summary>Update the last CT for the first held entry matching the fingerprint.</summary>
    public void UpdateCt((int mhp, int lvl, int br, int fa) fp, int ct)
    {
        foreach (var addr in new System.Collections.Generic.List<long>(_held.Keys))
            if (_held[addr].Fp.Equals(fp)) { _held[addr] = _held[addr] with { LastCt = ct }; return; }
    }

    /// <summary>Remove the latch for the first entry matching the fingerprint.</summary>
    public void Release((int mhp, int lvl, int br, int fa) fp)
    {
        foreach (var kv in _held)
            if (kv.Value.Fp.Equals(fp)) { _held.Remove(kv.Key); return; }
    }

    private record struct PlagueEntry((int mhp, int lvl, int br, int fa) Fp, int LastCt);
}
