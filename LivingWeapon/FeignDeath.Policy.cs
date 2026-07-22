namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Feign Death -- the played-dead state machine and the guarded status/HP
/// writes -- with no battle state of their own, so they're unit-tested directly. The stateful
/// orchestrator (wielder locate, timer bookkeeping, the once-per-battle latch) lives in FeignDeath.cs
/// and does nothing but read the wielder's facts, call <see cref="Step"/>, and apply the actions it
/// returns. Keeping the transitions pure is deliberate: the dead-bit / revive-edge interplay is
/// exactly what went wrong in the throwaway probe this is ported from -- re-stamping the dead bit
/// through the revive fought the engine and left a hearts/skipped-turn corpse; never stamping it meant
/// no death for Reraise to undo, so nothing stood back up -- so it earns direct tests.
/// </summary>
internal sealed partial class FeignDeath
{
    /// <summary>The played-dead lifecycle. Watching (armed, alive) -> Possum (flopped: prone but
    /// acting, ignored) -> Finish (the finishing blow + held Reraise) -> Recover (post-revive KO-state
    /// cleanup) -> spent (once per battle).</summary>
    internal enum Phase { Watching, Possum, Finish, Recover }

    /// <summary>What a tick wants done to the wielder's HP. None = leave it; HoldAlive = if it reads 0,
    /// lift it to 1 (stay mechanically alive while prone); ForceKill = drive it to 0 (the real death
    /// the engine's Reraise then undoes).</summary>
    internal enum HpAction { None, HoldAlive, ForceKill }

    /// <summary>The decision for one tick: the next phase plus the bit/HP intents and the latches to
    /// flip. A null bit field means "leave that bit alone this tick".</summary>
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

    /// <summary>A Living Weapon earns kills in any hand but commands its gift only from the main hand:
    /// Feign Death resolves the wielder via Wielder.TryResolveMainHand (RRHand-only match).</summary>
    public const bool ActivatesOnMainHandOnly = true;

    /// <summary>Active iff this is a feign-death weapon (signature present + flag set) at or above its
    /// tier. Same gate shape as Rapture.IsActive.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier) =>
        sig is not null && sig.FeignDeath && tier >= sig.AtTier;

    /// <summary>Re-arm the once-per-battle feign. A battle RESTART ("Retry") reloads the same battle
    /// too fast for the debounced exit edge to fire (slot9 sticks), so ResetBattle never runs and the
    /// spent latch persists into the reloaded fight. A restart puts every unit back at FULL HP, so a
    /// spent wielder reading hp >= maxHp is the re-arm signal. (A mid-battle full heal also re-arms,
    /// but Wrathblade's missing-HP damage means you almost never top the wielder off -- so in practice
    /// this stays once-per-battle while still resetting cleanly on a restart.)</summary>
    public static bool ShouldRearm(bool spent, int hp, int maxHp, bool dead) =>
        spent && !dead && maxHp > 0 && hp >= maxHp;

    /// <summary>The corpse just stood back up: last tick dead (HP==0), this tick alive (HP>0). That
    /// 0 -> positive edge is the engine's Reraise firing -- the feign is spent for the battle. (Any
    /// revive counts: an ally's Phoenix Down that beats Reraise to the corpse still ends the
    /// once-per-battle window cleanly, which is the correct behaviour.)</summary>
    public static bool IsReviveEdge(bool wasDead, int hp) => wasDead && hp > 0;

    /// <summary>One possum turn completed: the wielder WAS the engine's active unit last tick and is
    /// not now -- its turn ended. Counted off the active-unit struct (TurnQueue) instead of the noisy
    /// +0x25 CT, which undercounted and overran the possum (2026-06-14). Reliable: the active struct
    /// cleanly identifies who is acting.</summary>
    public static bool TurnEnded(bool wasActive, bool nowActive) => wasActive && !nowActive;

    /// <summary>A wall-clock window has elapsed. Used for the recover window and the possum
    /// hang-guard cap.</summary>
    public static bool Elapsed(System.DateTime start, System.DateTime now, double seconds) =>
        (now - start).TotalSeconds >= seconds;

    /// <summary>The pure played-dead transition for one tick. Mirrors the observed live probe exactly:
    /// flop -> hold prone+invisible for the window -> finishing blow (HP 0 AND the dead bit, so the
    /// engine's Reraise sees a real death) held until the revive edge -> clear the KO state for the
    /// recover window -> spent.</summary>
    public static FeignTick Step(Phase phase, int hp, bool dead, bool sawAlive,
                                 bool possumDone, bool recoverElapsed,
                                 bool finishKilled, bool finishWasDead, bool otherAllyAlive = true,
                                 bool upNext = true)
    {
        switch (phase)
        {
            case Phase.Watching:
                // Arm only after the wielder has been seen alive this battle -- don't feign a unit
                // that was already a corpse the first time we located it.
                return (sawAlive && (hp == 0 || dead))
                    ? new FeignTick { Next = Phase.Possum }
                    : new FeignTick { Next = Phase.Watching };

            case Phase.Possum:
                // Prone but alive (dead bit cleared, HP held at 1) and ignored (Invisible re-stamped;
                // it breaks the moment the unit acts). Hold for the window, then the finishing blow.
                return new FeignTick
                {
                    Next = possumDone ? Phase.Finish : Phase.Possum,
                    Dead = false,
                    Invisible = true,
                    Hp = HpAction.HoldAlive,
                };

            case Phase.Finish:
                // Drop Invisible and hold Reraise through the death for the whole phase.
                if (!finishKilled)
                {
                    if (!otherAllyAlive)
                        // Last party member standing: force-killing now would be a party wipe (game
                        // over) before Reraise could fire ~18s later. Degrade gracefully -- drop the
                        // ignored state, keep the wielder alive at 1 HP, end the feign. No dramatic
                        // stand-up, but survival beats a loss, and it is still strictly better than the
                        // vanilla lethal hit they already took.
                        return new FeignTick { Next = Phase.Finish, Invisible = false,
                                               Hp = HpAction.HoldAlive, Spent = true };
                    if (!upNext)
                        // Not up next yet: keep playing dead -- prone, alive (HP held), ignored. We
                        // strike only when the wielder has climbed back to "up next" in the queue, so
                        // the corpse is dead-and-scheduled for only a sliver before its turn fires the
                        // Reraise. A long dead-but-scheduled wait crashed the engine.
                        return new FeignTick { Next = Phase.Finish, Dead = false, Invisible = true,
                                               Hp = HpAction.HoldAlive };
                    // The finishing blow: HP -> 0 AND set the dead bit. The engine does NOT flag dead
                    // on a memory HP write, so without this set the Reraise has no death to undo and
                    // nothing stands up (the probe regression). With it, the revive fires at CT 100.
                    return new FeignTick { Next = Phase.Finish, Invisible = false, Reraise = true,
                                           Hp = HpAction.ForceKill, Dead = true, MarkKilled = true };
                }
                if (hp == 0)
                    // Still a corpse: hold Reraise (re-stamped) and wait. Do NOT re-stamp the dead bit
                    // -- a memory force-kill has no death-commit to clear it, so it stays set on its own,
                    // and re-setting it at the corpse's CT-100 turn fought the engine's auto-Reraise and
                    // left a dead-but-active "stuck turn" (hearts, can't Wait). Let the engine own it.
                    return new FeignTick { Next = Phase.Finish, Invisible = false, Reraise = true,
                                           MarkWasDead = true };
                if (finishWasDead && hp > 0)
                    // Revive edge: the engine raised the wielder. Hand off to Recover -- crucially do
                    // NOT re-stamp the dead bit here; Recover clears it (leaving it set = the hearts).
                    return new FeignTick { Next = Phase.Recover, Invisible = false, Reraise = true };
                // Killed, alive again, but the death was never observed (shouldn't happen): just hold.
                return new FeignTick { Next = Phase.Finish, Invisible = false, Reraise = true };

            case Phase.Recover:
            default:
                // Post-revive: drop the spent Reraise, HOLD the dead/KO bit cleared (no hearts, turn
                // not skipped) and never let HP slip back to 0. After the window, the feign is spent.
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

    /// <summary>OR/AND the Reraise bit (+0x47/0x20). on=true holds it (re-stamped through the death
    /// that clears it); on=false drops it after the revive (spent).</summary>
    internal static void SetReraise(IGameMemory mem, long entry, bool on) =>
        SetStatusBit(mem, entry + Offsets.AReraise, Offsets.AReraiseBit, on);

    /// <summary>OR/AND the Invisible bit (+0x47/0x10). Held re-stamped through the possum window so the
    /// AI keeps ignoring the prone wielder; dropped for the finishing blow.</summary>
    internal static void SetInvisible(IGameMemory mem, long entry, bool on) =>
        SetStatusBit(mem, entry + Offsets.AInvisible, Offsets.AInvisibleBit, on);

    /// <summary>OR/AND the Dead bit (+0x45/0x20). Set at the finishing blow so Reraise sees a death;
    /// cleared in Recover so the revived wielder carries no KO state.</summary>
    internal static void SetDead(IGameMemory mem, long entry, bool on) =>
        SetStatusBit(mem, entry + Offsets.ADeadStatus, Offsets.ADeadBit, on);

    /// <summary>OR/AND a single status bit, guarded by Writable; writes only when the bit must change
    /// (no churn). Shared by the Reraise / Invisible / Dead holds.</summary>
    internal static void SetStatusBit(IGameMemory mem, long addr, int mask, bool on)
    {
        if (!mem.Writable(addr, 1)) return;
        int cur = mem.U8(addr);
        int want = on ? (cur | mask) : (cur & ~mask);
        if (cur != want) mem.W8(addr, (byte)want);
    }

    /// <summary>Hold the wielder mechanically alive: if HP reads 0, lift it to 1 (one guarded 2-byte
    /// little-endian write so the engine never reads a torn value). Used in Possum and Recover.</summary>
    internal static void HoldAlive(IGameMemory mem, long entry)
    {
        long hpAddr = entry + Offsets.AHp;
        if (!mem.Readable(hpAddr, 2) || mem.U16(hpAddr) != 0) return;
        if (mem.Writable(hpAddr, 2)) mem.WriteBytes(hpAddr, new byte[] { 1, 0 });
    }

    /// <summary>The finishing blow: drive HP to 0 (one guarded 2-byte write). Paired with SetDead so
    /// the engine's Reraise sees a real death to undo.</summary>
    internal static void ForceKill(IGameMemory mem, long entry)
    {
        long hpAddr = entry + Offsets.AHp;
        if (!mem.Readable(hpAddr, 2) || mem.U16(hpAddr) == 0) return;
        if (mem.Writable(hpAddr, 2)) mem.WriteBytes(hpAddr, new byte[] { 0, 0 });
    }

    /// <summary>True iff the Reraise bit is set on the entry's status byte (read-only).</summary>
    internal static bool HasReraise(IGameMemory mem, long entry) =>
        (mem.U8(entry + Offsets.AReraise) & Offsets.AReraiseBit) != 0;

    /// <summary>True iff the Invisible bit is set on the entry's status byte (read-only).</summary>
    internal static bool HasInvisible(IGameMemory mem, long entry) =>
        (mem.U8(entry + Offsets.AInvisible) & Offsets.AInvisibleBit) != 0;
}
