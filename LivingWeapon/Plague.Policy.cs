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

    /// <summary>Bound on how much a held victim's maxHp may grow between re-verify checks and
    /// still count as the SAME unit rather than a different one occupying the slot. The
    /// 2026-07-14 live capture (LW-92) showed +4 maxHp for a single level (95 to 96, brave/faith
    /// unchanged); sized generously against Band.MaxLevelDrift's whole allowed level budget at
    /// 25 maxHp per level of drift, while staying far below the gap between two DIFFERENT
    /// units' typical maxHp.</summary>
    public const int MaxHpGrowthPerLatch = Band.MaxLevelDrift * 25;

    /// <summary>True when a re-verified fingerprint still identifies the SAME held victim, even
    /// though a mid-battle level-up has drifted its level and maxHp upward (LW-92, live capture
    /// 2026-07-14: level 95 to 96, maxHp 449 to 453, brave/faith unchanged). Brave and faith
    /// never drift, so they stay an exact match; level is bounded by Band.MaxLevelDrift (up-only,
    /// the same rule the credit path already uses) and maxHp by MaxHpGrowthPerLatch (up-only).
    /// An identical tuple always satisfies this, so exact-match callers can use it unconditionally.</summary>
    public static bool SameVictim((int mhp, int lvl, int br, int fa) captured, (int mhp, int lvl, int br, int fa) current)
        => current.br == captured.br && current.fa == captured.fa
           && Band.LevelMatchesRoster(captured.lvl, current.lvl)
           && current.mhp >= captured.mhp && current.mhp - captured.mhp <= MaxHpGrowthPerLatch;

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
