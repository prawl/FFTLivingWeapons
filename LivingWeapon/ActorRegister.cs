namespace LivingWeapon;

using System;

/// <summary>Tri-state result of matching the current actor-pointer owner against the roster:
/// <see cref="Player"/> (exactly one nameId+fingerprint match -- a real player unit), <see cref="Enemy"/>
/// (zero matches -- authoritative "not a player", the same verdict <see cref="ActorResolver.TryResolveActingPlayer"/>
/// returns for an enemy's acted-period today), <see cref="Unknown"/> (more than one match --
/// duplicated roster nameIds, an ambiguity the TQ-fingerprint fallback must resolve instead; V5
/// of the kill-attribution plan).</summary>
internal enum RosterBridge { Player, Enemy, Unknown }

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
/// </summary>
internal sealed class ActorRegister
{
    private readonly IGameMemory _mem;
    private bool _primed;
    private long _prevEntry;
    // Flight recorder tap (optional; null/no-op default keeps every existing test green
    // unmodified). KillTracker wires this to Flight.Record when it constructs the register.
    private readonly Action<string, string>? _recorder;

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

    public ActorRegister(IGameMemory mem, Action<string, string>? recorder = null)
    {
        _mem = mem;
        _recorder = recorder;
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
        _primed = false;
        _prevEntry = 0;
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
            CurrentEntry = entry;
            ArrivalTick = Tick;
            Trusted = entry != 0;
            if (entry != 0)
            {
                CurrentNameId = _mem.U16(entry + Offsets.ANameId);
                CurrentBridge = Bridge(entry, CurrentNameId, out long rosterBase);
                CurrentRosterBase = CurrentBridge == RosterBridge.Player ? rosterBase : 0;
            }
            else
            {
                CurrentNameId = 0;
                CurrentBridge = RosterBridge.Unknown;
                CurrentRosterBase = 0;
            }
            // Pointer-transition tap (on-change only -- Update() only reaches here when the
            // pointer actually moved): old entry, new entry, and the freshly-resolved nameId.
            _recorder?.Invoke("actor", $"pointer transition 0x{_prevEntry:X} -> 0x{entry:X} nameId={CurrentNameId} bridge={CurrentBridge}");
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

    /// <summary>Roster bridge: a slot matches iff it is OCCUPIED (RLevel 1..99), its RNameId equals
    /// <paramref name="nameId"/> (already required &gt;0 by the 0==0 trap guard below), AND its
    /// (level,brave,faith) matches the entry's OWN band bytes -- belt-and-suspenders against the
    /// unproven enemy/player nameId-pool overlap (K2 of the kill-attribution plan). Exactly one
    /// match -&gt; Player. Zero, with a POSITIVELY-READ nonzero nameId -&gt; Enemy (authoritative;
    /// enemy nameIds never match a real slot). More than one match, OR <paramref name="nameId"/>
    /// itself reading 0 (the 0==0 trap: a capture failure, not a confident "this is an enemy") --
    /// Unknown (the gate is unsatisfied and callers fall through to the TQ-fingerprint body,
    /// so a genuine player whose nameId capture failed still resolves normally there).</summary>
    private RosterBridge Bridge(long entry, ushort nameId, out long rosterBase)
    {
        rosterBase = 0;
        if (nameId == 0) return RosterBridge.Unknown;   // the 0==0 trap: capture failure, not "confident enemy"
        int lvl = _mem.U8(entry + Offsets.ALevel);
        int br = _mem.U8(entry + Offsets.ABrave);
        int fa = _mem.U8(entry + Offsets.AFaith);
        int matches = 0;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            int rlvl = _mem.U8(b + Offsets.RLevel);
            if (rlvl < 1 || rlvl > 99) continue;                       // unoccupied slot
            if (_mem.U16(b + Offsets.RNameId) != nameId) continue;
            if (!Band.LevelMatchesRoster(rlvl, lvl)) continue;
            if (_mem.U8(b + Offsets.RBrave) != br || _mem.U8(b + Offsets.RFaith) != fa) continue;
            matches++;
            rosterBase = b;
        }
        return matches == 1 ? RosterBridge.Player : matches == 0 ? RosterBridge.Enemy : RosterBridge.Unknown;
    }
}
