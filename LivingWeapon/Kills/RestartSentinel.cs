using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Outcome of presenting one slot's revive event to RestartSentinel. <see cref="Refuse"/>:
/// nothing here (no credit to reverse, no retry evidence, or grace still holds) -- the caller does
/// nothing. <see cref="UncreditNow"/>: the latch is open (just now, or already) -- the caller calls
/// KillTracker.UncreditKills immediately. <see cref="Stashed"/> (LW-233 fix): a null is currently
/// mid-flight but has not yet qualified -- the caller does nothing THIS tick; RestartSentinel.Tick's
/// return value drains it later, either into an uncredit (the null qualified in time) or into
/// nothing at all (the null broke first).</summary>
internal enum RevivePresentResult { Refuse, UncreditNow, Stashed }

/// <summary>
/// LW-233: the checkpoint-retry detector. Losing a battle and picking retry rewinds the game IN
/// PLACE with NO exit edge (docs/LIVE_LEDGER.md row [battle-retry-rewind-fingerprint], on `main` as
/// of 438b173 -- not yet merged onto this branch, see this arc's own PR notes), so KillTracker's
/// ordinary state-based death detection re-credits every revived enemy's kill a second time when
/// it is re-killed -- permanent kill-tally inflation. This class watches two signals
/// KillTracker.Corpses.cs already has for free (the engine actor pointer's raw-null state, via
/// ActorRegister.RawNullThisTick, and each slot's own revive events) and opens a LATCH the tracker
/// consults before paying out a phantom re-credit or a phantom re-kill.
///
/// NO memory access here (unlike most of this runtime's modules) -- every input arrives as a plain
/// value from the caller, so this class is unit-tested directly with no fake memory at all
/// (RestartSentinelTests.cs). The pure ShouldOpenLatch/ShouldStash decision tables, INCLUDING each
/// constant's own tape provenance and the deferred-verdict (0ms-join) and out-of-live-rearm
/// mechanism writeups, live in RestartSentinel.Policy.cs -- this class doc stays an index, not a
/// second copy (a prior version duplicated that history here; it drifted out of date and is gone).
/// THIS FILE is the core tick/latch machinery (Tick's null-streak/battle-age/out-of-live
/// bookkeeping, PresentRevive, LogOpenEdge); RestartSentinel.Stash.cs holds the deferred-verdict
/// stash's own per-tick TTL/drain bookkeeping (ProcessStash) -- split once the stash mechanism
/// pushed this file past the 200-line refactor trigger.
///
/// THE TWO TICK INPUTS, per Tick(): rawActorNull (true only on a literal raw-zero actor pointer
/// read, never a shape-fail or an unreadable page -- see ActorRegister.RawNullThisTick's own doc)
/// and inLiveish (the same OnField input KillTracker.Poll already threads through). PresentRevive is
/// called zero or more times per tick, inline from KillTracker.Corpses.cs's single-pass band scan,
/// once per slot that just came back alive from a credited death.
///
/// LATCH RULE (asymmetric, per-event, plan v2): opens the moment a revive presented with
/// wasCredited &amp;&amp; healedFromZero both true lands while a QUALIFIED null (persisted
/// <see cref="RestartSentinelPolicy.NullPersistTicks"/> ticks) occurred at-or-before it, within
/// <see cref="RestartSentinelPolicy.JoinWindowTicks"/>, past EITHER the battle's own opening
/// <see cref="RestartSentinelPolicy.GraceTicks"/> OR a matching revived identity -- see
/// RestartSentinel.Policy.cs's ShouldOpenLatch doc for why grace alone cannot discriminate a real
/// retry from LW-108's starved-bracket hole, and see this file's PresentRevive for what "matching"
/// means post-FINDING-0 (2026-08-17: BOTH the (lvl,br,fa,maxHp) tuple AND a nonzero nameId must
/// agree, not the tuple alone -- docs/LIVE_LEDGER.md row [party-nameid-unique-key] proves the tuple
/// alone has collided between real, distinct units). Once open, ANY later wasCredited+healedFromZero
/// revive re-arms the full <see cref="RestartSentinelPolicy.LatchTicks"/> duration without needing
/// its own fresh null-join. A status-death revive (healedFromZero false) never opens, extends, or
/// stashes -- the named precision-first residual (a miss, never a false positive).
/// </summary>
internal sealed partial class RestartSentinel
{
    private readonly Action<string, string>? _recorder;

    private int _tick;
    private int _nullStreakTicks;
    /// <summary>The tick of the most recent QUALIFIED null (persisted long enough); 0 = none since
    /// the last ResetBattle/out-of-live re-arm.</summary>
    private int _lastQualifiedNullTick;
    private int _latchOpenTicksRemaining;
    private int _battleAgeTicks;
    private int _outOfLiveStreakTicks;

    // The stash's own state (_stash, _drainedThisTick) lives in RestartSentinel.Stash.cs beside
    // ProcessStash, the logic that owns it -- Tick() (below) and PresentRevive still reach into it
    // (clear/add/return), same cross-file field access this codebase already uses throughout
    // KillTracker's own partial-class split (e.g. KillTracker.Corpses.cs's _creditedWeapons, read
    // and written from KillTracker.cs's CreditKill/UncreditKills).

    /// <summary>True while a credited-corpse revive should be uncredited and a fresh dead-edge
    /// should go pending instead of crediting -- see KillTracker.Corpses.cs's two consult sites.</summary>
    internal bool LatchOpen => _latchOpenTicksRemaining > 0;

    internal RestartSentinel(Action<string, string>? recorder = null) => _recorder = recorder;

    /// <summary>Full reset on battle enter/exit, mirroring every other per-battle state machine in
    /// this tracker (KillTracker.ResetBattle calls this alongside _register.ResetBattle()).</summary>
    internal void ResetBattle()
    {
        _tick = 0;
        _nullStreakTicks = 0;
        _lastQualifiedNullTick = 0;
        _latchOpenTicksRemaining = 0;
        _battleAgeTicks = 0;
        _outOfLiveStreakTicks = 0;
        _stash.Clear();
        _drainedThisTick.Clear();
    }

    /// <summary>One tick, called once per KillTracker.Poll (mirrors ActorRegister.Update's
    /// every-tick cadence) BEFORE that same tick's band scan presents any revives, so a null
    /// observed this tick can join a revive detected later in the same tick. Returns every stash
    /// entry that drained THIS tick (empty on almost every call) -- the caller (KillTracker.Poll)
    /// applies UncreditKills to each and folds the drain into its own changed-flag return.</summary>
    internal IReadOnlyList<(int Slot, List<int> Weapons, bool ViaFallback)> Tick(bool rawActorNull, bool inLiveish)
    {
        _tick++;
        _battleAgeTicks++;
        _drainedThisTick.Clear();
        bool justQualified = false;

        if (rawActorNull)
        {
            _nullStreakTicks++;
            if (_nullStreakTicks == RestartSentinelPolicy.NullPersistTicks) justQualified = true;
            if (RestartSentinelPolicy.NullQualifies(_nullStreakTicks))
                _lastQualifiedNullTick = _tick;
        }
        else
        {
            _nullStreakTicks = 0;
        }

        if (inLiveish)
        {
            _outOfLiveStreakTicks = 0;
        }
        else if (++_outOfLiveStreakTicks >= RestartSentinelPolicy.OutOfLiveRearmTicks)
        {
            _latchOpenTicksRemaining = 0;
            _nullStreakTicks = 0;
            _lastQualifiedNullTick = 0;
            _battleAgeTicks = 0;
            _outOfLiveStreakTicks = 0;
            _stash.Clear();   // LW-233 fix: a stale stash must not survive the starved-bracket re-arm either
            justQualified = false;
        }

        ProcessStash(justQualified);

        if (_latchOpenTicksRemaining > 0) _latchOpenTicksRemaining--;

        return _drainedThisTick;
    }

    /// <summary>Present one slot's revive event. See <see cref="RevivePresentResult"/> for what
    /// each outcome means and what the caller should do with it. <paramref name="creditedIdentity"/>
    /// and <paramref name="presentedIdentity"/> are the (lvl,br,fa,maxHp) identity tuples
    /// KillTracker.Corpses.cs already tracks (_creditedIdentity[s] at credit time, slot.Id at this
    /// revive); <paramref name="creditedNameId"/> and <paramref name="presentedNameId"/> (FINDING 0,
    /// 2026-08-17 verifier correction) are the matching nameId half (_creditedNameId[s], slot.NameId)
    /// -- REQUIRED in addition to the tuple, not an alternative to it, because the tuple alone has
    /// PROVEN twins (docs/LIVE_LEDGER.md row [party-nameid-unique-key]). All four values are passed
    /// in rather than duplicated as sentinel-side storage, per this class's own no-duplicate-state
    /// discipline (see the class doc's "NO memory access here" paragraph). See RestartSentinel.
    /// Policy.cs's ShouldOpenLatch doc for why a matching identity narrowly exempts the battle-age
    /// grace floor.</summary>
    internal RevivePresentResult PresentRevive(int slot, List<int> weapons, bool viaFallback, bool healedFromZero,
                                                (byte lvl, byte br, byte fa, ushort mhp) creditedIdentity,
                                                (byte lvl, byte br, byte fa, ushort mhp) presentedIdentity,
                                                ushort creditedNameId, ushort presentedNameId)
    {
        bool wasCredited = weapons.Count > 0;
        if (!wasCredited || !healedFromZero)
        {
            // LW-255: the sentinel used to record nothing on a decline. PresentRevive only runs on
            // an actual revive event (a slot whose _creditedWeapons was non-null going into this
            // alive tick), never every idle tick, so this allocation never lands on Tick()'s own
            // per-33ms hot path.
            _recorder?.Invoke("restart", $"no-open reason={(wasCredited ? "not-healed-from-zero" : "not-credited")} slot={slot}");
            return RevivePresentResult.Refuse;
        }

        // FINDING 0 (2026-08-17 verifier correction): BOTH the tuple AND a nonzero nameId must
        // agree -- see this method's own doc and KillTracker.Corpses.cs's _creditedNameId doc
        // comment for the full rationale. 0 (either side) means "unavailable" (unreadable or
        // genuinely unresolved) and fails closed, same as a tuple mismatch.
        bool identityMatches = creditedIdentity == presentedIdentity
            && creditedNameId != 0 && presentedNameId != 0 && creditedNameId == presentedNameId;
        string identityNote = IdentityTap(creditedIdentity, creditedNameId, presentedIdentity, presentedNameId);

        if (LatchOpen)
        {
            _latchOpenTicksRemaining = RestartSentinelPolicy.LatchTicks;   // re-openable: extend on a fresh qualifying revive
            return RevivePresentResult.UncreditNow;
        }

        bool haveQualifiedNull = _lastQualifiedNullTick != 0;
        int ticksSinceQualifiedNull = haveQualifiedNull ? _tick - _lastQualifiedNullTick : int.MaxValue;
        if (RestartSentinelPolicy.ShouldOpenLatch(wasCredited, healedFromZero, haveQualifiedNull,
                                                    ticksSinceQualifiedNull, _battleAgeTicks, identityMatches))
        {
            string graceNote = identityMatches && _battleAgeTicks <= RestartSentinelPolicy.GraceTicks
                ? ", grace exempted by a matching revived identity" : "";
            // FINDING 5: identity evidence rides every open-edge flight record -- this makes the
            // owner's next live retry drill answer, for free, the open premise FINDING 3's ledger
            // row names (whether nameId and the tuple actually survive a retry).
            LogOpenEdge($"null qualified {ticksSinceQualifiedNull} ticks before this revive, battle age {_battleAgeTicks} ticks{graceNote}, {identityNote}");
            return RevivePresentResult.UncreditNow;
        }

        if (RestartSentinelPolicy.ShouldStash(wasCredited, healedFromZero, _nullStreakTicks, _battleAgeTicks, identityMatches))
        {
            _stash.Add((slot, weapons, viaFallback, RestartSentinelPolicy.NullPersistTicks, identityMatches,
                        creditedIdentity, presentedIdentity, creditedNameId, presentedNameId));
            // Punch-list item 3 (2026-08-17): a stash that never drains used to leave ZERO trace on
            // the flight tape -- the exact silence that made the first live failure take an hour to
            // diagnose. This record (plus RestartSentinel.Stash.cs's drop record) closes that hole;
            // both reuse the frozen "restart" record type and this file's own IdentityTap, per the
            // codebase's flight-vocabulary convention (never invent a new record type).
            _recorder?.Invoke("restart", $"stash slot={slot} {identityNote}");
            return RevivePresentResult.Stashed;
        }

        // LW-255: best-effort refusal classification for the flight tape, not an exhaustive
        // decision tree -- ties break in this priority order. Diagnostic only; the credit path
        // never branches on it.
        string reason = !haveQualifiedNull && _nullStreakTicks == 0 ? "no-qualified-null"
            : haveQualifiedNull && ticksSinceQualifiedNull > RestartSentinelPolicy.JoinWindowTicks ? "outside-join-window"
            : !identityMatches && _battleAgeTicks <= RestartSentinelPolicy.GraceTicks ? "grace-not-cleared"
            : "null-not-yet-qualified";
        _recorder?.Invoke("restart", $"no-open reason={reason} slot={slot}, {identityNote}");
        return RevivePresentResult.Refuse;
    }

    /// <summary>Open the latch and log the one plain-language retry line plus its flight evidence.
    /// Shared by the direct-open path (PresentRevive) and the stash-drain path (ProcessStash) so
    /// the console wording stays a single frozen string regardless of which path fired it.</summary>
    private void LogOpenEdge(string evidence)
    {
        _latchOpenTicksRemaining = RestartSentinelPolicy.LatchTicks;
        ModLogger.Event(LogVerb.Kill,
            "The battle was retried; kills from the abandoned attempt are uncounted so nothing pays out twice.");
        _recorder?.Invoke("restart", $"latch open ({evidence})");
    }

    /// <summary>FINDING 5 (2026-08-17): the shared compact identity-evidence tap, used by every
    /// site that reports a credited/presented identity pair to the flight recorder (the open-edge
    /// evidence string above, the refusal record above, and RestartSentinel.Stash.cs's per-entry
    /// drain record) so the format never drifts between them. L/B/F/H/N mirrors this codebase's
    /// existing terse identity-tap convention (e.g. ActorRegister.cs's `fp=L{lvl}B{br}F{fa}`,
    /// Puppeteer.cs's `nameId={...}`).</summary>
    private static string IdentityTap((byte lvl, byte br, byte fa, ushort mhp) credited, ushort creditedNameId,
                                       (byte lvl, byte br, byte fa, ushort mhp) presented, ushort presentedNameId)
        => $"credited=(L{credited.lvl}B{credited.br}F{credited.fa}H{credited.mhp}N{creditedNameId}) " +
           $"presented=(L{presented.lvl}B{presented.br}F{presented.fa}H{presented.mhp}N{presentedNameId})";
}
