using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Venombolt's "Plague" signature -- no memory access.
/// The stateful latch, hold loop, and augment writes live in Plague.cs.
/// </summary>
internal sealed partial class Plague
{
    /// <summary>True when the struck/detected unit is an enemy (never latch allies).</summary>
    public static bool ShouldLatch(bool isEnemy) => isEnemy;

    /// <summary>The full latch decision: an enemy, not already held, whose poison edge and the
    /// wielder's acted window overlap within the grace -- in EITHER order. The engine applies
    /// poison during attack resolution, which can precede the observed acted window (actor
    /// resolution lag) or follow it (animation tail); requiring exact overlap missed real procs
    /// live (a chocobo cleansed a "permanent" poison because the latch never fired). Third-party
    /// poison stays excluded: an edge with no window within the grace never latches, and
    /// pre-existing poison has no edge at all.</summary>
    public static bool ShouldLatchNow(bool isEnemy, bool held, long lastEdgeMs, long lastActiveMs,
                                      long now, long graceMs)
        => isEnemy && !held && WithinGrace(lastEdgeMs, now, graceMs)
           && WithinGrace(lastActiveMs, now, graceMs);

    /// <summary>True when an event timestamp is recent enough to count. Sentinel timestamps
    /// (the "never happened" half-range negatives) always fail.</summary>
    public static bool WithinGrace(long eventMs, long now, long graceMs)
        => eventMs > long.MinValue / 4 && now - eventMs <= graceMs;

    /// <summary>A completed victim turn = its CT was near-full and has since reset notably lower.
    /// Mirrors Maim.IsTurn / CharmLock.IsTurn (same proven probe for both use cases).</summary>
    public static bool IsTurn(int lastCt, int curCt) => lastCt >= 90 && curCt < 70;

    /// <summary>True when the poison timer should be re-pinned (reads below the initial value,
    /// meaning the engine has ticked it down or a cure/expiry is in progress).</summary>
    public static bool ShouldRepin(int timer, byte init) => timer < init;

    /// <summary>Apply the augment: reduce <paramref name="hp"/> by mhp*3/32 (floor 1),
    /// returning the new HP. The augment NEVER kills; the engine owns lethal damage.</summary>
    public static int AugmentDamage(int mhp, int hp)
    {
        int dmg = mhp * Tuning.PlagueExtraDamageNum / Tuning.PlagueExtraDamageDen;
        if (dmg < 1) dmg = 1;
        int next = hp - dmg;
        return next < 1 ? 1 : next;
    }

    /// <summary>True when an mhp value belongs to a real combat unit (shared bound used in
    /// both the band-loop filter and EnemyFingerprints so they stay consistent).</summary>
    public static bool IsValidEnemyMhp(int mhp) => mhp >= 1 && mhp <= 1999;

    /// <summary>Drive the held poison state for one victim: re-OR the poison bit and re-pin
    /// the timer if it has slipped below init. Fingerprint must be verified by the caller
    /// before invoking; mismatches are handled in the main tick loop.
    /// When <paramref name="inLive"/> is false, all writes are suppressed.
    /// Exposed for pinned-buffer unit tests.</summary>
    public static void DriveOne(IGameMemory mem, long addr, (int mhp, int lvl, int br, int fa) fp,
                                PlagueState state, bool inLive = true)
    {
        // Verify fingerprint at the stored address before writing.
        if (!mem.Readable(addr + Offsets.AMaxHp, 2)) return;
        if (mem.U16(addr + Offsets.AMaxHp) != fp.mhp || mem.U8(addr + Offsets.ALevel) != fp.lvl
            || mem.U8(addr + Offsets.ABrave) != fp.br || mem.U8(addr + Offsets.AFaith) != fp.fa) return;

        if (!inLive) return;   // A3: no writes during debounce tail / post-battle

        // Re-OR the poison bit.
        long poisonAddr = addr + Offsets.APoison;
        if (mem.Writable(poisonAddr, 1))
        {
            int cur = mem.U8(poisonAddr);
            if ((cur & Offsets.APoisonBit) == 0)
                mem.W8(poisonAddr, (byte)(cur | Offsets.APoisonBit));
        }

        // Re-pin the timer if it has decayed below init.
        long timerAddr = addr + Offsets.APoisonTimer;
        if (mem.Readable(timerAddr, 1) && ShouldRepin(mem.U8(timerAddr), Tuning.PoisonTimerInit))
        {
            if (mem.Writable(timerAddr, 1))
                mem.W8(timerAddr, Tuning.PoisonTimerInit);
        }
    }

    /// <summary>Write the augment damage to +HP as a single 2-byte little-endian WriteBytes so
    /// the engine can never read a torn (partially-written) HP value. The augment NEVER kills.
    /// Exposed for pinned-buffer unit tests.</summary>
    public static void ApplyAugment(IGameMemory mem, long addr, (int mhp, int lvl, int br, int fa) fp)
    {
        long hpAddr = addr + Offsets.AHp;
        if (!mem.Readable(hpAddr, 2)) return;
        int hp = mem.U16(hpAddr);
        if (hp <= 0) return;   // already dead / KO'd; don't touch
        int next = AugmentDamage(fp.mhp, hp);
        if (!mem.Writable(hpAddr, 2)) return;
        mem.WriteBytes(hpAddr, new byte[] { (byte)(next & 0xFF), (byte)((next >> 8) & 0xFF) });
    }
}

/// <summary>Per-victim tracking for the Plague latch: band address (primary key), fingerprint
/// (validity check), last-seen CT, and addr-based operations for independent per-slot state.
/// Multiple enemies can be poisoned simultaneously (e.g. AoE wielder). The addr key prevents
/// two same-fingerprint units from cross-clobbering each other's CT tracking.</summary>
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
