namespace LivingWeapon;

using System;

/// <summary>Tri-state result of matching the current actor-pointer owner against the roster:
/// <see cref="Player"/> (exactly one nameId+fingerprint match -- a real player unit), <see cref="Enemy"/>
/// (zero matches -- authoritative "not a player", the same verdict <see cref="ActorResolver.TryResolveActingPlayer"/>
/// returns for an enemy's acted-period today), <see cref="Unknown"/> (more than one match --
/// duplicated roster nameIds, an ambiguity the TQ-fingerprint fallback must resolve instead; V5
/// of the kill-attribution plan).</summary>
internal enum RosterBridge { Player, Enemy, Unknown }

/// <summary>Outcome of the canonical-signature rescue (LW-56 fault 2), reached only when
/// <see cref="RosterBridge"/>'s own nameId+stat loop finds ZERO matches. <see cref="NotRun"/>: the
/// rescue never ran this transition (the nameId loop already resolved, or the pointer is idle).
/// <see cref="NotCanonical"/>: not the scripted signature, the ordinary authoritative-enemy case.
/// Neither is recorded in the flight tap. The rest are the rescue's own verdicts, each recorded:
/// <see cref="OracleEnemy"/> (the identity tuple was captured as enemy-side at battle start,
/// refused before either key runs). Then the fingerprint key: <see cref="Unique"/> (accepted),
/// <see cref="WpnMismatch"/> (a unique fingerprint match whose hands disagree with the band
/// weapon, terminal, never falls through), <see cref="Ambiguous"/> (more than one fingerprint
/// match, terminal). Then, only on a ZERO-fingerprint-match fall-through, the weapon key (round 2):
/// <see cref="WeaponUnique"/> (accepted), <see cref="WeaponAmbiguous"/> (more than one row holds
/// the band weapon within drift), <see cref="NoMatch"/> (neither key found anything, or the band
/// weapon itself is unarmed).</summary>
internal enum RescueOutcome
{
    NotRun, NotCanonical, OracleEnemy, Unique, WpnMismatch, Ambiguous, WeaponUnique, WeaponAmbiguous, NoMatch
}

/// <summary>
/// Tick-driven ownership tracker for the engine's own actor pointer (<see cref="Offsets.ActorPtr"/>,
/// resolved via <see cref="Band.ActorEntry"/>). Feeds <see cref="ActorResolver"/>'s register-first
/// preamble: the pointer names the TRUE acting unit's frame, both teams, mirror-safe (Proven ledger
/// row, ActorPtr) -- unlike a fingerprint walk, it never resolves to a revolving mirror copy. But
/// WHICH roster player owns that frame is not mirror-safe on its own; the frame nameId
/// back-reference (<see cref="Offsets.ANameId"/>, Proven ledger row) bridges pointer to roster.
///
/// The register does NOT read weapons -- identity only (single responsibility; weapon semantics
/// stay in ActorResolver, which owns the tracked-weapon set).
///
/// <see cref="Update"/> runs every tick, including OFF-FIELD ticks (menu-flicker arrivals). This
/// is safe for <see cref="StableSince"/> on its own (a menu-flicker arrival can never be "stable
/// since before the acted period" unless it genuinely predates the period) AND for its interaction
/// with KillTracker's corpse anchor: the anchor compares <c>KillTracker._pendingBirthTick</c>
/// against <see cref="ArrivalTick"/>, both stamped in this same register-tick currency (Tick
/// advances every Update(), on- or off-field alike), so an off-field stretch cannot desync the two
/// sides of that comparison the way it would a duration built from an onField-only counter.
///
/// PRIMING (ported from Iai's shipped priming contract, Iai.cs): the FIRST <see cref="Update"/>
/// after construction or <see cref="ResetBattle"/> observes the pointer WITHOUT stamping a trusted
/// arrival -- there is no way to know how old that ownership is, so a stale cross-battle pointer
/// must never be laundered into "fresh". Only a SUBSEQUENT OBSERVED TRANSITION (a real change from
/// the previously observed reading) stamps <see cref="ArrivalTick"/> and captures the owner
/// snapshot (nameId + roster bridge). Until the first trusted arrival, <see cref="StableSince"/>
/// is unconditionally false.
///
/// STABILITY (strict): <see cref="StableSince"/> is true only when the current owner's arrival
/// predates the caller's period-start tick -- a same-tick arrival is exactly the ambiguous
/// "the pointer already moved to the next actor" case the gate exists to reject.
///
/// CORPSE ANCHOR: this class does not itself refuse an answer for a pending corpse older than the
/// current ownership -- that check needs <c>KillTracker</c>'s per-slot pending state and stays in
/// <c>KillTracker.cs</c> (see <see cref="ArrivalTick"/>, the comparand its register-tick birth
/// stamps are checked against).
///
/// LAST-PLAYER SNAPSHOT (2026-07-05 death-edge culprit stamp): <see cref="LastPlayerNameId"/>/
/// <see cref="LastPlayerRosterBase"/>/<see cref="LastPlayerArrivalTick"/> exist for
/// <c>KillTracker</c>'s death-edge culprit stamp (<c>KillerStamp.cs</c>), which compares this
/// snapshot's freshness against the acted-edge latch instead of trusting the latch unconditionally.
/// DOCUMENTED RESIDUAL: a struck PLAYER (an ally hit by a player AoE, or a counter-struck attacker)
/// parked on by the pointer bridges Player and can overwrite this snapshot in the ~1-3 tick
/// damage-to-edge gap -- the stamp-time flight tap instruments it. Same residual family: a pointer
/// bounce X -&gt; idle -&gt; X (or X -&gt; victim -&gt; X) is a fresh Player-bridged transition, so it
/// RE-STAMPS <see cref="LastPlayerArrivalTick"/> to now -- a re-arrival can therefore pass
/// KillerStamp's ordering gate without that player having acted since the latch resolve; the
/// harmful geometry is equally narrow (no tape shows it) and the same stamp-time taps
/// (nameId + arrival age) instrument it.
/// </summary>
internal sealed partial class ActorRegister
{
    private readonly IGameMemory _mem;
    private bool _primed;
    private long _prevEntry;
    // Flight recorder tap (optional; null/no-op default keeps every existing test green
    // unmodified). KillTracker wires this to Flight.Record when it constructs the register.
    private readonly Action<string, string>? _recorder;
    // LW-56 A1: the oracle-exclusion clause's truth source. Null (every pre-round-2 call site,
    // and every test that omits it) means the clause is OFF: RescueCanonical runs exactly as it
    // did before this was wired. KillTracker passes a lambda over its own EnemyOracle so a
    // canonical entry whose identity was captured as enemy-side at battle start is refused before
    // either rescue key runs.
    private readonly Func<(int lvl, int br, int fa, int maxHp), bool>? _isOracleEnemy;

    /// <summary>Monotonically-incrementing tick counter, one per <see cref="Update"/> call.</summary>
    public int Tick { get; private set; }
    /// <summary>The current owner's band-entry address; 0 = no trusted owner.</summary>
    public long CurrentEntry { get; private set; }
    /// <summary>The tick at which the current owner's arrival was observed.</summary>
    public int ArrivalTick { get; private set; }
    /// <summary>True once an OBSERVED TRANSITION (not the priming read) has stamped a real arrival.</summary>
    public bool Trusted { get; private set; }
    /// <summary>The current owner's frame nameId back-reference, captured at arrival.</summary>
    public ushort CurrentNameId { get; private set; }
    /// <summary>The matched roster slot base when <see cref="CurrentBridge"/> is <see cref="RosterBridge.Player"/>; 0 otherwise.</summary>
    public long CurrentRosterBase { get; private set; }
    /// <summary>The roster-bridge classification captured at the current owner's arrival.</summary>
    public RosterBridge CurrentBridge { get; private set; } = RosterBridge.Unknown;
    /// <summary>The fingerprint rescue's outcome for the current owner's arrival (LW-56 fault 2);
    /// NotRun whenever the nameId+stat loop already resolved. Reset at the top of every transition
    /// and by <see cref="ResetBattle"/>.</summary>
    internal RescueOutcome CurrentRescue { get; private set; } = RescueOutcome.NotRun;

    /// <summary>The nameId of the most recent Player-bridged arrival (see <see cref="LastPlayerArrivalTick"/>
    /// for the freshness contract KillerStamp gates on). Untouched by an Enemy-bridged arrival or an
    /// idle (entry==0) flicker; CLEARED by an Unknown-bridged arrival (an ambiguous non-idle arrival
    /// invalidates the snapshot -- fail safe to the latch).</summary>
    public ushort LastPlayerNameId { get; private set; }
    /// <summary>The roster slot base matched at that arrival; 0 = none this battle (or invalidated).</summary>
    public long LastPlayerRosterBase { get; private set; }
    /// <summary>The register tick of that arrival; 0 = none this battle (or invalidated). KillerStamp
    /// gates its hypothesis on this being STRICTLY newer than the latch's own resolve tick.</summary>
    public int LastPlayerArrivalTick { get; private set; }

    public ActorRegister(IGameMemory mem, Action<string, string>? recorder = null,
                          Func<(int lvl, int br, int fa, int maxHp), bool>? isOracleEnemy = null)
    {
        _mem = mem;
        _recorder = recorder;
        _isOracleEnemy = isOracleEnemy;
    }

    /// <summary>Ticks since the current owner's arrival. NOTE: KillTracker's corpse anchor no
    /// longer consumes this -- a duration built from _pendingAge (onField-only ticks) desynced
    /// against this (every-tick) counter across off-field stretches, under-counting corpse age.
    /// The anchor instead compares register-tick birth stamps directly against
    /// <see cref="ArrivalTick"/> (same currency, no duration math). Kept as a general staleness
    /// metric; still exercised by its own direct test.</summary>
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
    }

    /// <summary>One tick: read the engine's own actor pointer and, on an observed transition past
    /// the priming read, stamp a fresh trusted arrival and its owner snapshot.</summary>
    public void Update()
    {
        Tick++;
        long entry = Band.ActorEntry(_mem);

        if (!_primed)
        {
            // Priming read: observed, but never trusted (there's no way to know how stale this is).
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
                    // A confident Player arrival -- stamp the last-player snapshot KillerStamp reads.
                    LastPlayerNameId = CurrentNameId;
                    LastPlayerRosterBase = rosterBase;
                    LastPlayerArrivalTick = Tick;
                }
                else if (CurrentBridge == RosterBridge.Unknown)
                {
                    // An ambiguous non-idle arrival (duplicated roster nameIds) invalidates the
                    // snapshot -- fail safe to the latch rather than trust a now-uncertain owner.
                    LastPlayerNameId = 0;
                    LastPlayerRosterBase = 0;
                    LastPlayerArrivalTick = 0;
                }
                // Enemy: leave the snapshot untouched -- a struck enemy victim's own frame bridges
                // Enemy (its nameId matches no roster slot) and must not blow away a real snapshot.
            }
            else
            {
                CurrentNameId = 0;
                CurrentBridge = RosterBridge.Unknown;
                CurrentRosterBase = 0;
                // entry == 0 (idle flicker): leave the last-player snapshot untouched too.
            }
            // Pointer-transition tap (on-change only -- Update() only reaches here when the
            // pointer actually moved): old entry, new entry, and the freshly-resolved nameId.
            // LW-56 round 2: a rescue-judged transition also carries the outcome, the fp it
            // judged (read live off CurrentFp), and the raw band weapon read (wpn=, never
            // normalized: a sentinel rides through as 0xFF/0xFFFF, not 0). The filter is a
            // NEGATIVE list (not NotRun/NotCanonical) on purpose: a future outcome added to the
            // enum is tapped by default instead of silently dropped.
            string rescueTap = CurrentRescue is not (RescueOutcome.NotRun or RescueOutcome.NotCanonical)
                ? $" rescue={CurrentRescue} fp=L{CurrentFp.lvl}B{CurrentFp.br}F{CurrentFp.fa} wpn={_mem.U16(entry + Offsets.AWeapon)}"
                : "";
            _recorder?.Invoke("actor", $"pointer transition 0x{_prevEntry:X} -> 0x{entry:X} nameId={CurrentNameId} bridge={CurrentBridge}{rescueTap}");
        }
        _prevEntry = entry;
    }

    /// <summary>True only when the current owner's arrival STRICTLY predates
    /// <paramref name="periodStartTick"/>: the acted-period's action provably belongs to an owner
    /// who held the seat before the period began, never one who merely arrived at/after it.</summary>
    public bool StableSince(int periodStartTick) => Trusted && ArrivalTick < periodStartTick;

    /// <summary>The current owner's (level,brave,faith), read LIVE off <see cref="CurrentEntry"/>'s
    /// band bytes (not an arrival-time snapshot) so a mid-turn level-up cannot desync it against
    /// TrackDelayed's live fingerprint compare. Default when there is no current owner.</summary>
    public (int lvl, int br, int fa) CurrentFp => CurrentEntry != 0
        ? (_mem.U8(CurrentEntry + Offsets.ALevel), _mem.U8(CurrentEntry + Offsets.ABrave), _mem.U8(CurrentEntry + Offsets.AFaith))
        : default;
}
