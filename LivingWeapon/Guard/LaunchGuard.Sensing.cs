namespace LivingWeapon;

/// <summary>
/// LW-395 seam: the SENSING lane, a third independent FingerprintGuard instance (the kit lane's
/// exact shape, LaunchGuard.KitLane.cs). Why it exists: the new-game detector
/// (<see cref="PlaythroughReset"/>) archives and EMPTIES kills.json after a one second hold of
/// two battle sentinels (event id and battle mode). If a game update moved those bytes and the
/// new bytes happened to look like "new game", a real tally would be wiped for nothing
/// (docs/research/WRITE_SITE_AUDIT_2026-10.md item 3). This lane checks, once per launch and only
/// out of battle, that all six battle sentinels read the way this build is known to hold them;
/// Engine hands <see cref="SensingArmed"/> to the detector, which refuses to clear anything until
/// it is true. A stand-down here switches the DETECTOR off for the session and nothing else: it
/// is a WARN, never the full-stand-down wording, and raises no OS message box. LW-393 will read
/// <see cref="SensingArmed"/> for its own write sites.
///
/// THREE FAMILIES of reading (the probe's verdict order):
///   (a) IN-BATTLE (battle mode 1..5, or BattleState.InLiveBattle's marker-plus-excuse frame;
///       the bare stuck marker alone never counts): Unreadable, a silent
///       retry that never counts toward the debounce. A Continue can resume a mid-battle autosave,
///       so the main guard (and this lane one tick behind it) can arm with a battle already live;
///       the lane must wait for the first out-of-battle tick rather than stand down a healthy
///       build. slot9 is deliberately not consulted here: it can legitimately drop mid-battle
///       (BattleState.cs).
///   (b) OUT-OF-BATTLE: Match only when every sentinel fits the shape in docs/LIVE_LEDGER.md
///       [sentinel-out-of-battle-tuple] (owner tape 2026-10-16) and [slot0-battle-phase-values].
///   (c) anything else: Mismatch, with the full observed tuple in the detail.
///
/// ALL-ZERO RULE: an all-zero tuple lands in (c) as a Mismatch. There is no separate check: it
/// is rejected by family (b)'s slot9 rule and its event-zero exclusion, and the all-zero row of
/// LaunchGuardTests.Sensing_out_of_battle_rules_match_or_mismatch pins it, so widening either
/// rule must keep that row green. Post-arm these addresses are mapped, initialized .data, so
/// zeros mean the address moved. This diverges from
/// FingerprintGuard.ByteSignature's boot-window rule (all zero reads Unreadable because the table
/// may not be built yet) for the same reason LaunchGuard.KitLane.cs diverges: the lane never runs
/// before the main guard armed, so the boot window is over.
/// </summary>
internal sealed partial class LaunchGuard
{
    // Declared here, assigned from the ctor in LaunchGuard.cs (partial-class fields may live in any part).
    private readonly FingerprintGuard _sensingGuard;
    private readonly uint _expectedSlot9;
    private readonly bool _forceSensingMismatch;

    // Provenance for every value below: docs/LIVE_LEDGER.md [sentinel-out-of-battle-tuple] (owner
    // tape 2026-10-16, tools/probes/tapes/sentinel_watch_20261016_163725.log) and
    // [slot0-battle-phase-values].
    /// <summary>slot0 and slot9 both read this from the title screen through the Orbonne opener until the first battle.</summary>
    internal const uint SensingSentinelIdle = 0xFFFFFFFFu;
    /// <summary>The event id word reads this while no event is playing.</summary>
    internal const ushort SensingEventIdle = 0xFFFF;
    /// <summary>A guess ABOVE the highest event id seen so far (401): a real id is accepted, but a
    /// foreign read of a moved .data address is unlikely to land inside 1..this. Zero is excluded
    /// on purpose (the likeliest foreign read of a moved address).</summary>
    internal const ushort MaxPlausibleEventId = 0x3FF;
    /// <summary>Battle mode before the session's first battle.</summary>
    internal const byte SensingModePreFirstBattle = 255;
    /// <summary>Battle mode on the world map after a battle.</summary>
    internal const byte SensingModeWorldMap = 0;
    /// <summary>slot0's small phase values (3, 5, 0x11 seen at battle edges and post-victory) all
    /// fit a byte; the post-QUIT value is unverified, hence the width.</summary>
    internal const uint SensingSlot0SmallPhaseMax = 0xFF;

    /// <summary>True once the sensing lane verified the battle sentinels. Engine hands this to
    /// <see cref="PlaythroughReset.Observe"/>; LW-393 reads it for its own gate.</summary>
    public bool SensingArmed => _sensingGuard.Armed;

    /// <summary>Test/diagnostic seam, mirrors <see cref="State"/> for the sensing lane.</summary>
    internal GuardState SensingState => _sensingGuard.State;

    /// <summary>Steps the sensing lane, but ONLY while the main guard is Armed and the lane is
    /// still Verifying (same ordering argument as <see cref="StepKitLane"/>). Engine calls it once
    /// per tick beside StepKitLane.</summary>
    public void StepSensing()
    {
        if (_guard.State != GuardState.Armed) return;
        if (_sensingGuard.State != GuardState.Verifying) return;
        _sensingGuard.Step();
    }

    private LandmarkReading ProbeSensingTuple()
    {
        // TryReadBytes, never U8/U16/U32: a failed or short read must be Unreadable, not a zero.
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

        // Family (a): a battle is live (or a mid-battle autosave resumed); wait, never count.
        // Two parts. The MODE byte (1..5) is the plain signal. BattleState.InLiveBattle is the
        // exact predicate Engine trusts on this same tick: it adds the marker-only frames that
        // carry an EXCUSE for mode reading 0 (a paused screen, or a real event id = mid-battle
        // dialogue), which would otherwise reach family (b) with a legitimately dropped slot9 or an
        // event word aliasing a nameId above the plausible ceiling and stand a healthy lane down.
        // The bare marker is deliberately NOT enough: after a victory the in-battle marker
        // (0x10/0x11) sticks on the world map with mode 0, unpaused, event 0xFFFF
        // (docs/LIVE_LEDGER.md [slot0-battle-phase-values]), InLiveBattle is false there, and the
        // frame falls through to family (b) where the small-phase slot0 range accepts it; keying
        // on the bare marker would make a lane that armed inside a resumed battle and then won
        // wait for the rest of the session. Nothing here can wait forever on a healthy build: a
        // pause or a cutscene ends.
        if ((mode >= 1 && mode <= 5) || BattleState.InLiveBattle(slot0, mode, pause == 1, eventId))
            return LandmarkVerdict.Unreadable;

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
        // Same ordering as KitLaneStandDown: record FIRST so a later flush archives WHY, then the
        // log line, then the flush request LAST. No OS notice: the mod is fine, only the
        // new-game detector stepped back.
        string drillTag = _forceSensingMismatch ? " (drill: LW_FORCE_SENSING_MISMATCH)" : "";
        _recorder?.Invoke("guard", $"sensing stand-down{drillTag} ({diag})");
        string drillNote = _forceSensingMismatch
            ? " This stand-down was forced by the LW_FORCE_SENSING_MISMATCH drill flag (dev builds only)."
            : "";
        // WARNING tier, never Error, and never the phrase tools/scan_logs.py STANDDOWN_MARK looks
        // for: this must not read as a full stand-down of a healthy mod.
        ModLogger.Warn(LogVerb.Startup,
            $"The battle sentinels do not read the way this mod expects ({diag}). The new game detector is switched off for this session so the kill tally can never be cleared by mistake; kill counting and everything else keep working.{HedgeIfUnknownBuild()}{drillNote}");
        _requestFlush?.Invoke("standdown");
    }
}
