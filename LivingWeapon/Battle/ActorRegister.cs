namespace LivingWeapon;

using System;

/// <summary>How the current actor-pointer owner matched the roster. Player is exactly one
/// nameId+fingerprint match. Enemy is zero matches, the same verdict
/// <see cref="ActorResolver.TryResolveActingPlayer"/> gives an enemy's acted period. Unknown is
/// more than one match, duplicated roster nameIds that the turn-queue fingerprint fallback has
/// to settle.</summary>
internal enum RosterBridge { Player, Enemy, Unknown }

/// <summary>Verdict of the canonical-signature rescue, which runs only when the nameId+stat loop in
/// <see cref="RosterBridge"/> finds nothing. NotRun and NotCanonical are not recorded in the
/// flight tap. OracleEnemy refused the entry before either key ran because its identity tuple was
/// captured enemy-side at battle start. The fingerprint key yields Unique, WpnMismatch (a unique
/// match whose hands disagree with the band weapon, terminal) or Ambiguous (terminal). Only a
/// zero-match fall-through reaches the weapon key, which yields WeaponUnique, WeaponAmbiguous or
/// NoMatch (nothing found, or the band weapon is unarmed).</summary>
internal enum RescueOutcome
{
    NotRun, NotCanonical, OracleEnemy, Unique, WpnMismatch, Ambiguous, WeaponUnique, WeaponAmbiguous, NoMatch
}

/// <summary>
/// Tracks who owns the engine's own actor pointer (<see cref="Offsets.ActorPtr"/> via
/// <see cref="Band.ActorEntry"/>) and feeds <see cref="ActorResolver"/>'s register-first preamble.
/// The pointer names the true acting unit's frame on both teams and never a mirror copy. Which
/// roster player owns that frame comes from the frame's nameId back-reference
/// (<see cref="Offsets.ANameId"/>). Identity only, no weapon reads, ActorResolver owns those.
///
/// <see cref="Update"/> runs every tick, off-field ticks included, so <see cref="ArrivalTick"/> and
/// KillTracker's corpse birth stamps share one currency and an off-field stretch cannot desync them.
///
/// Priming, same contract as Iai.cs. The first Update after construction or ResetBattle observes
/// the pointer without trusting it, since a stale cross-battle pointer must not be laundered into
/// fresh. Only a later observed transition stamps ArrivalTick and the owner snapshot, and
/// <see cref="StableSince"/> stays false until then. StableSince is strict, a same-tick arrival is
/// exactly the "pointer already moved on" case the gate rejects.
///
/// The corpse anchor check (a pending corpse older than the current ownership) needs per-slot
/// pending state and lives in KillTracker.cs.
///
/// The LastPlayer snapshot exists for KillerStamp.cs, which compares its freshness against the
/// acted-edge latch instead of trusting the latch outright. Known residual. A struck player the
/// pointer parks on bridges Player and can overwrite the snapshot in the one to three tick gap
/// before the death edge, and a bounce X to idle to X re-stamps LastPlayerArrivalTick so a
/// re-arrival can pass the ordering gate without that player having acted. No tape shows either
/// doing harm; the stamp-time flight tap carries nameId and arrival age for when one does.
/// </summary>
internal sealed partial class ActorRegister
{
    private readonly IGameMemory _mem;
    private bool _primed;
    private long _prevEntry;
    // Optional flight tap. KillTracker wires Flight.Record; a null keeps tests silent.
    private readonly Action<string, string>? _recorder;
    // Null means the oracle-exclusion clause is off and RescueCanonical runs unchanged. KillTracker
    // passes a lambda over its EnemyOracle so an entry captured enemy-side at battle start is
    // refused before either rescue key runs.
    private readonly Func<(int lvl, int br, int fa, int maxHp), bool>? _isOracleEnemy;

    /// <summary>One per <see cref="Update"/> call.</summary>
    public int Tick { get; private set; }
    /// <summary>The current owner's band entry address, 0 when there is no trusted owner.</summary>
    public long CurrentEntry { get; private set; }
    /// <summary>The tick the current owner's arrival was observed.</summary>
    public int ArrivalTick { get; private set; }
    /// <summary>True once an observed transition, not the priming read, stamped a real arrival.</summary>
    public bool Trusted { get; private set; }
    /// <summary>The current owner's frame nameId, captured at arrival.</summary>
    public ushort CurrentNameId { get; private set; }
    /// <summary>The matched roster slot base when <see cref="CurrentBridge"/> is Player, else 0.</summary>
    public long CurrentRosterBase { get; private set; }
    /// <summary>The roster bridge verdict captured at the current owner's arrival.</summary>
    public RosterBridge CurrentBridge { get; private set; } = RosterBridge.Unknown;
    /// <summary>The rescue verdict for the current owner's arrival, NotRun whenever the nameId+stat
    /// loop resolved on its own. Reset on every transition and by <see cref="ResetBattle"/>.</summary>
    internal RescueOutcome CurrentRescue { get; private set; } = RescueOutcome.NotRun;

    /// <summary>The nameId of the latest Player-bridged arrival. An Enemy arrival or an idle flicker
    /// leaves it alone; an Unknown arrival clears it so KillerStamp falls back to the latch.</summary>
    public ushort LastPlayerNameId { get; private set; }
    /// <summary>The roster slot base matched at that arrival, 0 when none this battle.</summary>
    public long LastPlayerRosterBase { get; private set; }
    /// <summary>The register tick of that arrival, 0 when none this battle. KillerStamp requires it
    /// to be strictly newer than the latch's own resolve tick.</summary>
    public int LastPlayerArrivalTick { get; private set; }

    /// <summary>True only when the raw U64 at <see cref="Offsets.ActorPtr"/> is literally zero this
    /// tick. <see cref="Band.ActorEntry"/> folds a zero pointer and a bad-shape pointer into the same
    /// 0 return, which suits "who is acting" but not RestartSentinel's retry signal, where a garbage
    /// pointer must read false and an unreadable page must not read as null. Hence its own guarded
    /// read every tick, independent of the transition bookkeeping.</summary>
    public bool RawNullThisTick { get; private set; }

    public ActorRegister(IGameMemory mem, Action<string, string>? recorder = null,
                          Func<(int lvl, int br, int fa, int maxHp), bool>? isOracleEnemy = null)
    {
        _mem = mem;
        _recorder = recorder;
        _isOracleEnemy = isOracleEnemy;
    }

    /// <summary>Ticks since the current owner arrived. The corpse anchor no longer uses it, since an
    /// onField-only age desynced against this every-tick counter. Kept as a staleness metric with
    /// its own test.</summary>
    public int OwnershipAge => Tick - ArrivalTick;

    public void ResetBattle()
    {
        Tick = 0;
        CurrentEntry = 0;
        ArrivalTick = 0;
        Trusted = false;
        CurrentNameId = 0;
        CurrentRosterBase = 0;
        CurrentBridge = RosterBridge.Unknown;
        CurrentRescue = RescueOutcome.NotRun;
        _primed = false;
        _prevEntry = 0;
        LastPlayerNameId = 0;
        LastPlayerRosterBase = 0;
        LastPlayerArrivalTick = 0;
        RawNullThisTick = false;
    }

    /// <summary>One tick. Read the engine's own actor pointer and, on an observed transition past
    /// the priming read, stamp a fresh trusted arrival and its owner snapshot.</summary>
    public void Update()
    {
        Tick++;
        // RawNullThisTick gets its own guarded read, see its doc for why it cannot share
        // Band.ActorEntry's folded zero.
        RawNullThisTick = _mem.Readable(Offsets.ActorPtr, 8) && _mem.U64(Offsets.ActorPtr) == 0;
        long entry = Band.ActorEntry(_mem);

        if (!_primed)
        {
            // Priming read, observed but never trusted.
            _primed = true;
            _prevEntry = entry;
            return;
        }

        if (entry != _prevEntry)
        {
            CurrentRescue = RescueOutcome.NotRun;   // reset before Bridge (or an idle arrival) runs
            CurrentEntry = entry;
            ArrivalTick = Tick;
            Trusted = entry != 0;
            if (entry != 0)
            {
                CurrentNameId = _mem.U16(entry + Offsets.ANameId);
                CurrentBridge = Bridge(entry, CurrentNameId, out long rosterBase);
                CurrentRosterBase = CurrentBridge == RosterBridge.Player ? rosterBase : 0;
                if (CurrentBridge == RosterBridge.Player)
                {
                    // A confident Player arrival stamps the snapshot KillerStamp reads.
                    LastPlayerNameId = CurrentNameId;
                    LastPlayerRosterBase = rosterBase;
                    LastPlayerArrivalTick = Tick;
                }
                else if (CurrentBridge == RosterBridge.Unknown)
                {
                    // Duplicated roster nameIds, so drop the snapshot and let KillerStamp use the latch.
                    LastPlayerNameId = 0;
                    LastPlayerRosterBase = 0;
                    LastPlayerArrivalTick = 0;
                }
                // Enemy leaves the snapshot alone. A struck enemy victim's frame bridges Enemy too
                // and must not blow away a real snapshot.
            }
            else
            {
                CurrentNameId = 0;
                CurrentBridge = RosterBridge.Unknown;
                CurrentRosterBase = 0;
                // An idle flicker leaves the last-player snapshot alone too.
            }
            // Transition tap, on change only. A rescue-judged transition also carries the verdict,
            // the fp it judged (live off CurrentFp) and the raw band weapon, sentinels unnormalized.
            // The filter is a negative list so a future verdict is tapped by default.
            string rescueTap = CurrentRescue is not (RescueOutcome.NotRun or RescueOutcome.NotCanonical)
                ? $" rescue={CurrentRescue} fp=L{CurrentFp.lvl}B{CurrentFp.br}F{CurrentFp.fa} wpn={_mem.U16(entry + Offsets.AWeapon)}"
                : "";
            _recorder?.Invoke("actor", $"pointer transition 0x{_prevEntry:X} -> 0x{entry:X} nameId={CurrentNameId} bridge={CurrentBridge}{rescueTap}");
        }
        _prevEntry = entry;
    }

    /// <summary>True only when the current owner's arrival strictly predates
    /// <paramref name="periodStartTick"/>, so the action belongs to an owner who held the seat
    /// before the period began.</summary>
    public bool StableSince(int periodStartTick) => Trusted && ArrivalTick < periodStartTick;

    /// <summary>The current owner's (level,brave,faith) read live off the band so a mid-turn
    /// level-up cannot desync it from TrackDelayed's fingerprint compare.</summary>
    public (int lvl, int br, int fa) CurrentFp => CurrentEntry != 0
        ? (_mem.U8(CurrentEntry + Offsets.ALevel), _mem.U8(CurrentEntry + Offsets.ABrave), _mem.U8(CurrentEntry + Offsets.AFaith))
        : default;
}
