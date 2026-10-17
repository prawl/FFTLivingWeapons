namespace LivingWeapon;

/// <summary>Pure decisions for Venombolt's Plague signature. The latch, hold loop and augment writes are in Plague.cs.</summary>
internal sealed partial class Plague
{
    /// <summary>True for an enemy; allies are never latched.</summary>
    public static bool ShouldLatch(bool isEnemy) => isEnemy;

    /// <summary>Latch an unheld enemy when the poison edge and the wielder's acted window both fall
    /// within the grace, in either order. Poison lands during attack resolution, which can come
    /// before the acted window (actor lag) or after it (animation tail). Third-party and
    /// pre-existing poison never latch.</summary>
    public static bool ShouldLatchNow(bool isEnemy, bool held, long lastEdgeMs, long lastActiveMs,
                                      long now, long graceMs)
        => isEnemy && !held && WithinGrace(lastEdgeMs, now, graceMs)
           && WithinGrace(lastActiveMs, now, graceMs);

    /// <summary>True when the event is within the grace. The "never happened" sentinel timestamps fail.</summary>
    public static bool WithinGrace(long eventMs, long now, long graceMs)
        => eventMs > long.MinValue / 4 && now - eventMs <= graceMs;

    /// <summary>Largest maxHp growth that still counts as the same victim. The 2026-07-14 capture
    ///  saw +4 for one level; this allows 25 per level across Band.MaxLevelDrift.</summary>
    public const int MaxHpGrowthPerLatch = Band.MaxLevelDrift * 25;

    /// <summary>True when the fingerprint still matches the held victim after a mid-battle level-up
    /// (2026-07-14. Level 95 to 96, maxHp 449 to 453). Brave and faith match exactly; level
    /// is bounded by Band.MaxLevelDrift and maxHp by MaxHpGrowthPerLatch, both up-only.</summary>
    public static bool SameVictim((int mhp, int lvl, int br, int fa) captured, (int mhp, int lvl, int br, int fa) current)
        => current.br == captured.br && current.fa == captured.fa
           && Band.LevelMatchesRoster(captured.lvl, current.lvl)
           && current.mhp >= captured.mhp && current.mhp - captured.mhp <= MaxHpGrowthPerLatch;

    /// <summary>True when the timer has dropped below its initial value and needs re-pinning.</summary>
    public static bool ShouldRepin(int timer, byte init) => timer < init;

    /// <summary>Returns <paramref name="hp"/> reduced by mhp*3/32 (at least 1), floored at 1 HP so the augment cannot kill.</summary>
    public static int AugmentDamage(int mhp, int hp)
    {
        int dmg = mhp * Tuning.PlagueExtraDamageNum / Tuning.PlagueExtraDamageDen;
        if (dmg < 1) dmg = 1;
        int next = hp - dmg;
        return next < 1 ? 1 : next;
    }

    /// <summary>True when mhp looks like a real combat unit. Used by EnemyFingerprints only; the
    /// band loop in Plague.cs:96 keeps its own inline copy of the bound (the dedup pass left this seam
    /// alone). Whether mhp 2000 is valid is an open row in docs/LIVE_LEDGER.md
    /// (Band.EnemyFingerprints accepts it, this rejects it), so do not align them yet.</summary>
    public static bool IsValidEnemyMhp(int mhp) => mhp >= 1 && mhp <= 1999;

    /// <summary>Re-sets the poison bit and re-pins the timer for one held victim. No writes when
    /// <paramref name="inLive"/> is false. Exposed for pinned-buffer tests.</summary>
    public static void DriveOne(IGameMemory mem, long addr, (int mhp, int lvl, int br, int fa) fp,
                                PlagueState state, bool inLive = true)
    {
        // Same-unit check. Level drift is handled earlier by the caller's VerifyOrReanchor.
        if (!Band.SameUnitAtExact(mem, addr, fp)) return;

        if (!inLive) return;   // A3. No writes during debounce tail / post-battle

        long poisonAddr = addr + Offsets.APoison;
        if (mem.Writable(poisonAddr, 1))
        {
            int cur = mem.U8(poisonAddr);
            if ((cur & Offsets.APoisonBit) == 0)
                mem.W8(poisonAddr, (byte)(cur | Offsets.APoisonBit));
        }

        long timerAddr = addr + Offsets.APoisonTimer;
        if (mem.Readable(timerAddr, 1) && ShouldRepin(mem.U8(timerAddr), Tuning.PoisonTimerInit))
        {
            if (mem.Writable(timerAddr, 1))
                mem.W8(timerAddr, Tuning.PoisonTimerInit);
        }
    }

    /// <summary>Writes the augmented HP as one 2-byte WriteBytes so the engine never reads a torn value.
    /// Exposed for pinned-buffer tests.</summary>
    public static void ApplyAugment(IGameMemory mem, long addr, (int mhp, int lvl, int br, int fa) fp)
    {
        long hpAddr = addr + Offsets.AHp;
        if (!mem.Readable(hpAddr, 2)) return;
        int hp = mem.U16(hpAddr);
        if (hp <= 0) return;   // already KO'd
        int next = AugmentDamage(fp.mhp, hp);
        if (!mem.Writable(hpAddr, 2)) return;
        mem.WriteBytes(hpAddr, new byte[] { (byte)(next & 0xFF), (byte)((next >> 8) & 0xFF) });
    }
}
