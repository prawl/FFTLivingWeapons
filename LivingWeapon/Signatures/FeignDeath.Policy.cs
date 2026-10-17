namespace LivingWeapon;

/// <summary>
/// The played-dead state machine and the guarded status/HP writes, kept free of battle state so they
/// can be unit-tested. FeignDeath.cs reads the wielder's facts, calls <see cref="Step"/> and applies
/// the result. Ported from a throwaway probe where re-stamping the dead bit through the revive left a
/// hearts/skipped-turn corpse, and never stamping it left no death for Reraise to undo.
/// </summary>
internal sealed partial class FeignDeath
{
    /// <summary>Watching (armed, alive) -> Possum (prone, ignored) -> Finish (finishing blow plus held
    /// Reraise) -> Recover (clear KO state after the revive) -> spent.</summary>
    internal enum Phase { Watching, Possum, Finish, Recover }

    /// <summary>None leaves HP alone; HoldAlive lifts a 0 to 1; ForceKill drives it to 0 so Reraise
    /// has a death to undo.</summary>
    internal enum HpAction { None, HoldAlive, ForceKill }

    /// <summary>One tick's decision. A null bit field leaves that bit alone.</summary>
    internal readonly struct FeignTick
    {
        public Phase Next { get; init; }
        public bool? Dead { get; init; }        // true = set the dead bit, false = clear it, null = leave
        public bool? Invisible { get; init; }
        public bool? Reraise { get; init; }
        public HpAction Hp { get; init; }
        public bool MarkKilled { get; init; }   // latch: the finishing blow has been dealt
        public bool MarkWasDead { get; init; }  // latch: the finish death registered (awaiting revive)
        public bool Spent { get; init; }        // the feign is over -> once-per-battle latch
    }

    /// <summary>Kills count from any hand, but the gift fires from the main hand only
    /// (Wielder.TryResolveMainHand, RRHand-only match).</summary>
    public const bool ActivatesOnMainHandOnly = true;

    /// <summary>Active for a feign-death weapon at or above its tier, same as Rapture.IsActive.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier) =>
        sig is not null && sig.FeignDeath && tier >= sig.AtTier;

    /// <summary>Re-arm after a battle Retry. Retry reloads too fast for the debounced exit edge
    /// (slot9 sticks), so ResetBattle never runs; a spent wielder back at full HP is the signal. A
    /// mid-battle full heal also re-arms, which Wrathblade's missing-HP damage makes rare.</summary>
    public static bool ShouldRearm(bool spent, int hp, int maxHp, bool dead) =>
        spent && !dead && maxHp > 0 && hp >= maxHp;

    /// <summary>Dead last tick, alive now: the engine's Reraise firing. Any revive counts, including
    /// an ally's Phoenix Down that gets there first.</summary>
    public static bool IsReviveEdge(bool wasDead, int hp) => wasDead && hp > 0;

    /// <summary>One possum turn completed: the wielder was the active unit last tick and is not now.
    /// Counted off the active-unit struct (TurnQueue) because the +0x25 CT undercounted and overran
    /// the possum (2026-06-14).</summary>
    public static bool TurnEnded(bool wasActive, bool nowActive) => wasActive && !nowActive;

    /// <summary>A wall-clock window has elapsed (recover window, possum hang-guard cap).</summary>
    public static bool Elapsed(System.DateTime start, System.DateTime now, double seconds) =>
        (now - start).TotalSeconds >= seconds;

    /// <summary>One tick of the transition, following the observed probe: flop, hold prone and
    /// invisible, finishing blow (HP 0 and the dead bit) until the revive edge, clear KO state, spent.</summary>
    public static FeignTick Step(Phase phase, int hp, bool dead, bool sawAlive,
                                 bool possumDone, bool recoverElapsed,
                                 bool finishKilled, bool finishWasDead, bool otherAllyAlive = true,
                                 bool upNext = true)
    {
        switch (phase)
        {
            case Phase.Watching:
                // Skip a unit that was already a corpse when first located.
                return (sawAlive && (hp == 0 || dead))
                    ? new FeignTick { Next = Phase.Possum }
                    : new FeignTick { Next = Phase.Watching };

            case Phase.Possum:
                // Prone, alive (dead bit cleared, HP held at 1) and ignored. Invisible breaks when the
                // wielder is hit, not when it acts (corrected 2026-07-22, see Offsets.AInvisible).
                return new FeignTick
                {
                    Next = possumDone ? Phase.Finish : Phase.Possum,
                    Dead = false,
                    Invisible = true,
                    Hp = HpAction.HoldAlive,
                };

            case Phase.Finish:
                // Drop Invisible and hold Reraise through the death.
                if (!finishKilled)
                {
                    if (!otherAllyAlive)
                        // Last party member: a force-kill would be a game over before Reraise fires
                        // ~18s later, so stay alive at 1 HP and end the feign.
                        return new FeignTick
                        {
                            Next = Phase.Finish,
                            Invisible = false,
                            Hp = HpAction.HoldAlive,
                            Spent = true
                        };
                    if (!upNext)
                        // Keep playing dead until the wielder is up next; a long dead-but-scheduled
                        // wait crashed the engine.
                        return new FeignTick
                        {
                            Next = Phase.Finish,
                            Dead = false,
                            Invisible = true,
                            Hp = HpAction.HoldAlive
                        };
                    // Finishing blow. A memory HP write does not flag dead, so the dead bit is set too
                    // or Reraise has nothing to undo (the probe regression). Revive fires at CT 100.
                    return new FeignTick
                    {
                        Next = Phase.Finish,
                        Invisible = false,
                        Reraise = true,
                        Hp = HpAction.ForceKill,
                        Dead = true,
                        MarkKilled = true
                    };
                }
                if (hp == 0)
                    // Still a corpse: hold Reraise. Do not re-stamp the dead bit; doing so at the CT-100
                    // turn fought the auto-Reraise and left a stuck turn (hearts, can't Wait).
                    return new FeignTick
                    {
                        Next = Phase.Finish,
                        Invisible = false,
                        Reraise = true,
                        MarkWasDead = true
                    };
                if (finishWasDead && hp > 0)
                    // Revive edge. Recover clears the dead bit; leaving it set shows the hearts.
                    return new FeignTick { Next = Phase.Recover, Invisible = false, Reraise = true };
                // Killed and alive again without the death being observed: just hold.
                return new FeignTick { Next = Phase.Finish, Invisible = false, Reraise = true };

            case Phase.Recover:
            default:
                // Post-revive: drop Reraise, keep the dead bit cleared and HP above 0 for the window.
                return new FeignTick
                {
                    Next = Phase.Recover,
                    Reraise = false,
                    Dead = false,
                    Hp = HpAction.HoldAlive,
                    Spent = recoverElapsed,
                };
        }
    }

    // ---- guarded writers (exercised against a PinnedBuf in tests; the real RPM/WPM guard runs) ----

    /// <summary>Set or clear the Reraise bit (+0x47/0x20); held through the death that clears it.</summary>
    internal static void SetReraise(IGameMemory mem, long entry, bool on) =>
        SetStatusBit(mem, entry + Offsets.AReraise, Offsets.AReraiseBit, on);

    /// <summary>Set or clear the Invisible bit (+0x47/0x10); held during possum so the AI ignores the wielder.</summary>
    internal static void SetInvisible(IGameMemory mem, long entry, bool on) =>
        SetStatusBit(mem, entry + Offsets.AInvisible, Offsets.AInvisibleBit, on);

    /// <summary>Set or clear the Dead bit (+0x45/0x20); set at the finishing blow, cleared in Recover.</summary>
    internal static void SetDead(IGameMemory mem, long entry, bool on) =>
        SetStatusBit(mem, entry + Offsets.ADeadStatus, Offsets.ADeadBit, on);

    /// <summary>Set or clear one status bit behind a Writable guard, writing only on change.</summary>
    internal static void SetStatusBit(IGameMemory mem, long addr, int mask, bool on)
    {
        if (!mem.Writable(addr, 1)) return;
        int cur = mem.U8(addr);
        int want = on ? (cur | mask) : (cur & ~mask);
        if (cur != want) mem.W8(addr, (byte)want);
    }

    /// <summary>If HP reads 0, lift it to 1 (single 2-byte write so the engine never sees a torn value).</summary>
    internal static void HoldAlive(IGameMemory mem, long entry)
    {
        long hpAddr = entry + Offsets.AHp;
        if (!mem.Readable(hpAddr, 2) || mem.U16(hpAddr) != 0) return;
        if (mem.Writable(hpAddr, 2)) mem.WriteBytes(hpAddr, new byte[] { 1, 0 });
    }

    /// <summary>Drive HP to 0 with a single 2-byte write; paired with SetDead.</summary>
    internal static void ForceKill(IGameMemory mem, long entry)
    {
        long hpAddr = entry + Offsets.AHp;
        if (!mem.Readable(hpAddr, 2) || mem.U16(hpAddr) == 0) return;
        if (mem.Writable(hpAddr, 2)) mem.WriteBytes(hpAddr, new byte[] { 0, 0 });
    }

    /// <summary>True if the Reraise bit is set.</summary>
    internal static bool HasReraise(IGameMemory mem, long entry) =>
        (mem.U8(entry + Offsets.AReraise) & Offsets.AReraiseBit) != 0;

    /// <summary>True if the Invisible bit is set.</summary>
    internal static bool HasInvisible(IGameMemory mem, long entry) =>
        (mem.U8(entry + Offsets.AInvisible) & Offsets.AInvisibleBit) != 0;
}
