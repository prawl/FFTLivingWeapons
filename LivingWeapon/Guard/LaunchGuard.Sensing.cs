namespace LivingWeapon;

/// <summary>
/// The sensing lane, a third FingerprintGuard instance shaped like the kit lane (LaunchGuard.KitLane.cs).
/// <see cref="PlaythroughReset"/> archives and empties kills.json after a one second hold of the event id and battle
/// mode sentinels, so a game update that moved those bytes could wipe a real tally (docs/research/WRITE_SITE_AUDIT_2026-10.md item 3).
/// Once per launch, out of battle, this lane checks that all six sentinels read the way this build holds them. It steps from the first
/// tick once the build stamp was read, and holds a mismatch as unreadable for the first SensingBootGraceSteps steps in case the game is still starting.
/// Engine passes <see cref="SensingArmed"/> to the detector, which clears nothing until it is true. A stand-down only switches the detector off, as a warn with no box.
/// In battle (mode 1..5, or BattleState.InLiveBattle) the verdict is Unreadable and never counts, since a resumed autosave can
/// arm the guards mid-battle. Out of battle it matches the shape in docs/LIVE_LEDGER.md [sentinel-out-of-battle-tuple]
/// (owner tape 2026-10-16) and [slot0-battle-phase-values]. Anything else is a Mismatch carrying the observed tuple.
/// An all-zero tuple is a Mismatch via the slot9 rule and the event-zero exclusion, pinned by
/// LaunchGuardTests.Sensing_out_of_battle_rules_match_or_mismatch. Unlike FingerprintGuard.ByteSignature's boot window,
/// zeros after arming mean the address moved, same as the kit lane.
/// </summary>
internal sealed partial class LaunchGuard
{
    // Declared here, assigned from the ctor in LaunchGuard.cs (partial-class fields may live in any part).
    private readonly FingerprintGuard _sensingGuard;
    private readonly uint _expectedSlot9;
    private readonly bool _forceSensingMismatch;

    // Provenance for every value below. Docs/LIVE_LEDGER.md [sentinel-out-of-battle-tuple] (owner
    // tape 2026-10-16, tools/probes/tapes/sentinel_watch_20261016_163725.log) and
    // [slot0-battle-phase-values].
    /// <summary>slot0 and slot9 both read this from the title screen through the Orbonne opener until the first battle.</summary>
    internal const uint SensingSentinelIdle = 0xFFFFFFFFu;
    /// <summary>The event id word reads this while no event is playing.</summary>
    internal const ushort SensingEventIdle = 0xFFFF;
    /// <summary>Above the highest event id seen so far (401), so real ids pass but a foreign read of a moved address
    /// rarely lands in 1..this. Zero is excluded as the likeliest foreign read.</summary>
    internal const ushort MaxPlausibleEventId = 0x3FF;
    /// <summary>Battle mode before the session's first battle.</summary>
    internal const byte SensingModePreFirstBattle = 255;
    /// <summary>Battle mode on the world map after a battle.</summary>
    internal const byte SensingModeWorldMap = 0;
    /// <summary>slot0's small phase values (3, 5, 0x11 at battle edges and post-victory) fit a byte. The post-quit
    /// value is unverified, hence the width.</summary>
    internal const uint SensingSlot0SmallPhaseMax = 0xFF;

    /// <summary>True once the sensing lane verified the battle sentinels. Engine passes it to
    /// <see cref="PlaythroughReset.Observe"/>.</summary>
    public bool SensingArmed => _sensingGuard.Armed;

    /// <summary>Test/diagnostic seam, mirrors <see cref="State"/>.</summary>
    internal GuardState SensingState => _sensingGuard.State;

    /// <summary>Steps the lane while it is Verifying, once the image was readable (the stamp latch).
    /// Engine calls it each tick beside StepKitLane.</summary>
    public void StepSensing()
    {
        if (!_stampRead) return;
        if (_sensingGuard.State != GuardState.Verifying) return;
        _sensingSteps++;
        _sensingGuard.Step();
    }

    private LandmarkReading ProbeSensingTuple()
    {
        // TryReadBytes, never U8/U16/U32, so a failed or short read is Unreadable and not a zero.
        if (!_mem.TryReadBytes(Offsets.Slot0, 4, out var b0) || b0.Length < 4) return LandmarkVerdict.Unreadable;
        if (!_mem.TryReadBytes(Offsets.Slot9, 4, out var b9) || b9.Length < 4) return LandmarkVerdict.Unreadable;
        if (!_mem.TryReadBytes(Offsets.BattleMode, 1, out var bm) || bm.Length < 1) return LandmarkVerdict.Unreadable;
        if (!_mem.TryReadBytes(Offsets.EventId, 2, out var be) || be.Length < 2) return LandmarkVerdict.Unreadable;
        if (!_mem.TryReadBytes(Offsets.PauseFlag, 1, out var bp) || bp.Length < 1) return LandmarkVerdict.Unreadable;
        if (!_mem.TryReadBytes(Offsets.SubmenuFlag, 1, out var bs) || bs.Length < 1) return LandmarkVerdict.Unreadable;

        uint slot0 = Mem.U32(b0, 0);
        uint slot9 = Mem.U32(b9, 0);
        byte mode = bm[0];
        ushort eventId = Mem.U16(be, 0);
        byte pause = bp[0];
        byte submenu = bs[0];

        // Family (a), a battle is live or a mid-battle autosave resumed. Wait, never count. InLiveBattle is the
        // predicate Engine trusts this tick. It adds marker-only frames excused for mode 0 (paused, or an event id
        // during mid-battle dialogue) that would otherwise fail family (b). slot9 can drop mid-battle (BattleState.cs).
        // The bare marker is not enough, since 0x10/0x11 sticks on the world map after a victory with mode 0, unpaused,
        // event 0xFFFF (docs/LIVE_LEDGER.md [slot0-battle-phase-values]) and keying on it would stall the lane.
        if ((mode >= 1 && mode <= 5) || BattleState.InLiveBattle(slot0, mode, pause == 1, eventId))
            return new LandmarkReading(LandmarkVerdict.Unreadable, SensingBattleLive);

        // Family (b): the out-of-battle shape.
        bool ok = (mode == SensingModeWorldMap || mode == SensingModePreFirstBattle)
            && slot9 == _expectedSlot9
            && (slot0 == SensingSentinelIdle || slot0 <= SensingSlot0SmallPhaseMax)
            && (eventId == SensingEventIdle || (eventId >= 1 && eventId <= MaxPlausibleEventId))
            && pause <= 1 && submenu <= 1;
        if (ok) return LandmarkVerdict.Match;

        // Family (c): everything else, with the full observed tuple.
        return new LandmarkReading(LandmarkVerdict.Mismatch, DescribeSensingTuple(slot0, slot9, mode, eventId, pause, submenu));
    }

    private string DescribeSensingTuple(uint slot0, uint slot9, byte mode, ushort eventId, byte pause, byte submenu)
        => $"slot0=0x{slot0:X8} slot9=0x{slot9:X8} mode={mode} event=0x{eventId:X4} pause={pause} submenu={submenu}; "
           + $"expected slot9 0x{_expectedSlot9:X8}, mode 0 or 255, slot0 0xFFFFFFFF or at most 0xFF, "
           + $"event 0xFFFF or 1 to 0x{MaxPlausibleEventId:X}, pause and submenu 0 or 1";

    private void SensingArmedEdge()
    {
        _recorder?.Invoke("guard", "sensing lane armed (battle sentinels match; new-game detector enabled)");
        ModLogger.Event(LogVerb.Startup, "The battle sentinels read as expected; the new game detector is enabled.");
    }

    private void SensingStandDown(string diag)
    {
        // Record first so a later flush archives why, then log, then request the flush (same order as KitLaneStandDown).
        // No OS notice, only the new-game detector stepped back.
        string drillTag = _forceSensingMismatch ? " (drill: LW_FORCE_SENSING_MISMATCH)" : "";
        _recorder?.Invoke("guard", $"sensing stand-down{drillTag} ({diag})");
        string drillNote = _forceSensingMismatch
            ? " This stand-down was forced by the LW_FORCE_SENSING_MISMATCH drill flag (dev builds only)."
            : "";
        // Warning tier, and avoid the phrase tools/scan_logs.py STANDDOWN_MARK looks for, so it does not read
        // as a full stand-down.
        ModLogger.Warn(LogVerb.Startup,
            $"The battle sentinels do not read the way this mod expects ({diag}). The new game detector is switched off for this session so the kill tally can never be cleared by mistake; kill counting and everything else keep working.{HedgeIfUnknownBuild()}{drillNote}");
        _requestFlush?.Invoke("standdown");
    }
}
