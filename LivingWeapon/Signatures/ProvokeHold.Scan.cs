using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// ProvokeHold's band walks. Find the marked enemy, re-locate a seen identity, list hide targets.
/// Split out of ProvokeHold.cs to keep it under the 200-line trigger.
/// Identity is the (nameId, maxHp, level, brave, faith) tuple that <see cref="Band.FlagOwner"/> and
/// <see cref="Wielder"/> also use. A nonzero nameId on both sides must agree; otherwise the
/// fingerprint decides, and unseeded twins resolve to the first real-position match.
/// </summary>
internal sealed partial class ProvokeHold
{
    /// <summary>Latch for ScrubPlayerSideMarks' refused-write warn.</summary>
    private bool _scrubRefusedLogged;

    /// <summary>Debounce counter for ScrubUnarmableMark.</summary>
    private int _unarmableTicks;

    /// <summary>Identity tuple of one band entry.</summary>
    private static (int nameId, int mhp, int lvl, int br, int fa) ReadIdentity(IGameMemory mem, long e) =>
        (mem.U16(e + Offsets.ANameId), mem.U16(e + Offsets.AMaxHp), mem.U8(e + Offsets.ALevel),
         mem.U8(e + Offsets.ABrave), mem.U8(e + Offsets.AFaith));

    /// <summary>The seat's board tile, for log lines (criterion 15).</summary>
    private static (int gx, int gy) ReadTile(IGameMemory mem, long e) =>
        (mem.U8(e + Offsets.AGx), mem.U8(e + Offsets.AGy));

    /// <summary>Same fingerprint, and the same nameId unless either side reads 0.</summary>
    private static bool SameIdentity((int nameId, int mhp, int lvl, int br, int fa) a,
                                     (int nameId, int mhp, int lvl, int br, int fa) b)
    {
        if (a.mhp != b.mhp || a.lvl != b.lvl || a.br != b.br || a.fa != b.fa) return false;
        return a.nameId == 0 || b.nameId == 0 || a.nameId == b.nameId;
    }

    /// <summary>Decision 6. Enemy iff the friend/foe bit (band +0x1D2 bit 0x10) is set.</summary>
    private static bool IsEnemySide(IGameMemory mem, long e) =>
        (mem.U8(e + Offsets.AFriendFoe) & Offsets.AFriendFoeEnemyBit) != 0;

    /// <summary>Does the engine's actor pointer (Band.ActorEntry) name an entry with the marked
    /// enemy's identity? Reading the enemy's own ATurnFlag was flaky on 2026-07-22 (hid 0 units once,
    /// missed the turn-done edge once, then the 30s watchdog fired). The pointer is reliable on both
    /// teams (LIVE_LEDGER "actor pointer names the acting unit", 2026-07-01). Like Iai.cs, it skips
    /// Band.IsValid on the pointed-to entry. Callers must also check <see cref="PlayerSideOwnsTurn"/>
    /// , since the pointer parks on struck victims; see ProvokeHoldTests
    /// Actor_pointer_parked_on_the_marked_enemy_during_a_player_turn_does_not_count_as_its_turn.</summary>
    private static bool MarkedIsActor(IGameMemory mem, (int nameId, int mhp, int lvl, int br, int fa) markedId)
    {
        long actorEntry = Band.ActorEntry(mem);
        return actorEntry != 0 && SameIdentity(ReadIdentity(mem, actorEntry), markedId);
    }

    /// <summary>true iff some player-side seat owns the turn flag (<see cref="Band.FlagOwner"/>),
    /// i.e. it is still the player's turn. Replaces the Offsets.TqTeam read, which flickered to enemy
    /// during the player's own action. The flag is the band +0x19C menu-open flag (docs/LIVE_LEDGER.md,
    /// turn/moved/acted row, 2026-07-09); a 2026-07-21 session (not owner-flipped) saw it hold across a
    /// status-screen detour, and the hide/reveal arc banked a probe. The tick census saw FlagOwner
    /// miss a player seat on all 804 enemy-turn ticks (flag-bridge-fail 804). It fails open. No owner,
    /// or two disagreeing owners, returns false and the gate opens.</summary>
    private static bool PlayerSideOwnsTurn(IGameMemory mem) =>
        Band.FlagOwner(mem, out long owner, out _) && !IsEnemySide(mem, owner);

    /// <summary>Ghost-seat gate (criterion 7): combat +0x01 reading 0xFF is a cutscene/ghost seat
    /// that <see cref="Band.IsValid"/> lets through.</summary>
    private static bool OnField(IGameMemory mem, long e) => mem.U8(e + Offsets.AGateByte) != Offsets.AGateHiddenValue;

    /// <summary>Dead by HP 0 or the Dead status bit, same test as KillTracker, so a status-death
    /// the 33ms poll never saw HP for is still caught.</summary>
    internal static bool IsAlive(IGameMemory mem, long e) =>
        mem.U16(e + Offsets.AHp) > 0 && (mem.U8(e + Offsets.ADeadStatus) & Offsets.ADeadBit) == 0;

    /// <summary>True iff the composed status carries any of <paramref name="disablingIds"/>
    /// (Tuning.ProvokeDisablingStatusIds), so the hold releases instead of lingering.</summary>
    internal static bool IsDisabled(IGameMemory mem, long e, int[] disablingIds)
    {
        foreach (int id in disablingIds)
        {
            int by = StatusApply.StatusByte(id);
            byte mask = StatusApply.StatusMask(id);
            if ((mem.U8(e + StatusApply.Composed + by) & mask) != 0) return true;
        }
        return false;
    }

    /// <summary>Find a valid, on-field, enemy-side seat carrying the mark (composed +0x45 bit 0x80).
    /// Prefers a real-position entry over a frozen (0,0) twin. False when nobody is marked.</summary>
    private static bool FindMarkedEnemy(IGameMemory mem, out long entry,
        out (int nameId, int mhp, int lvl, int br, int fa) id)
    {
        entry = 0;
        id = default;
        long twinEntry = 0;
        (int, int, int, int, int) twinId = default;
        bool haveTwin = false;
        byte mask = StatusApply.StatusMask(MarkId);
        int by = StatusApply.StatusByte(MarkId);

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e) || !OnField(mem, e) || !IsEnemySide(mem, e)) continue;
            if ((mem.U8(e + StatusApply.Composed + by) & mask) == 0) continue;

            bool realPos = mem.U8(e + Offsets.AGx) != 0 || mem.U8(e + Offsets.AGy) != 0;
            var candidate = ReadIdentity(mem, e);
            if (realPos) { entry = e; id = candidate; return true; }
            if (!haveTwin) { twinEntry = e; twinId = candidate; haveTwin = true; }
        }
        if (!haveTwin) return false;
        entry = twinEntry;
        id = twinId;
        return true;
    }

    /// <summary>Re-locate a band entry by captured identity. A nonzero nameId match wins; otherwise
    /// the first real-position fingerprint match, then a frozen (0,0) twin. Side-agnostic, used for
    /// the marked enemy each armed tick and for a flagged ally at reveal time.</summary>
    private static long LocateByIdentity(IGameMemory mem, (int nameId, int mhp, int lvl, int br, int fa) id)
    {
        long fpReal = 0, fpAny = 0;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e)) continue;
            var candidate = ReadIdentity(mem, e);
            if (candidate.mhp != id.mhp || candidate.lvl != id.lvl || candidate.br != id.br || candidate.fa != id.fa) continue;
            if (id.nameId != 0 && candidate.nameId == id.nameId) return e;
            bool realPos = mem.U8(e + Offsets.AGx) != 0 || mem.U8(e + Offsets.AGy) != 0;
            if (realPos) { if (fpReal == 0) fpReal = e; }
            else if (fpAny == 0) fpAny = e;
        }
        return fpReal != 0 ? fpReal : fpAny;
    }

    /// <summary>the CT+Speed lookahead evidence for one armed tick, for
    /// ActionForWithReason. It does not pick the action, since that needs <see cref="_engaged"/>,
    /// which TickArmed owns. <paramref name="markedEntry"/> is the marked enemy's entry, or 0.
    /// Ranking is TurnOrder's, enemy seats only, and runs only when Tuning.ProvokeLookahead is on.
    /// <c>markedEta</c> is set whenever the lookahead fires and the marked seat is a candidate, even
    /// if it is already next. It is null when TryEta was skipped or refused, because 0 would read as
    /// "due right now" in the evidence line (LogHideEdge prints a distinct token for null).</summary>
    private (bool lookaheadFired, int leaderEta, int? markedEta, int nextNameId, bool markedIsNext, bool markedIsFarOff)
        GatherLookahead(long markedEntry)
    {
        bool markedIsNext = false;
        bool markedIsFarOff = false;
        bool lookaheadFired = false;
        int leaderEta = 0, nextNameId = 0;
        int? markedEta = null;
        if (Tuning.ProvokeLookahead && TurnOrder.TryNextEnemyToAct(_mem, _turnOrderScratch, out long nextEntry, out int wait))
        {
            lookaheadFired = true;
            leaderEta = wait;
            var nextId = ReadIdentity(_mem, nextEntry);
            nextNameId = nextId.nameId;
            markedIsNext = SameIdentity(nextId, _markedId);
            if (markedEntry != 0 && TurnOrder.TryEta(_mem, markedEntry, out int eta))
            {
                markedEta = eta;
                if (!markedIsNext) markedIsFarOff = eta - leaderEta >= Tuning.ProvokeRevealMarginTicks;
            }
        }
        return (lookaheadFired, leaderEta, markedEta, nextNameId, markedIsNext, markedIsFarOff);
    }

    /// <summary>Every valid, on-field, player-side seat except the bearer (criteria 4/8/9). Guests
    /// count as player-side (the friend/foe bit is guest-complete). The bearer is excluded by identity, not seat position.</summary>
    private static void EnumerateHideTargets(IGameMemory mem,
        (int nameId, int mhp, int lvl, int br, int fa) bearer, List<long> results)
    {
        results.Clear();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e) || !OnField(mem, e) || IsEnemySide(mem, e)) continue;
            if (SameIdentity(ReadIdentity(mem, e), bearer)) continue;
            results.Add(e);
        }
    }

    /// <summary>scrub a mark on an enemy while the hold cannot arm. The mark never expires (Counter 0)
    /// and the engine will not re-apply it, so the enemy would stay Provoked all battle. With
    /// two deployed defenders, Provoke.cs still grants the command off the first roster row holding
    /// id 33, but Wielder.ResolveDeployedMainHand returns 0 for the ambiguous pair. Debounced on
    /// Tuning.ProvokeMarkedMissTicks so one unreadable frame or band rebuild does not eat a mark
    /// that would arm next tick; the counter resets on any tick that arms or sees no mark.</summary>
    private void ScrubUnarmableMark(long entry)
    {
        if (++_unarmableTicks < Tuning.ProvokeMarkedMissTicks) return;
        _unarmableTicks = 0;
        if (!ClearMark(_mem, entry)) return;   // unwritable page. The next run of ticks retries
        var id = ReadIdentity(_mem, entry);
        var (gx, gy) = ReadTile(_mem, entry);
        ModLogger.EventWithTrace(LogVerb.Signature,
            "A provoke mark landed on an enemy while the shout could not take hold, so the runtime scrubbed it off; that enemy can be shouted at again.",
            $"provoke unarmable mark scrubbed (nameId {id.nameId}, tile {gx},{gy})");
        Flight.Record("provoke", $"scrub-unarmable nameId={id.nameId} tile={gx},{gy}");
    }

    /// <summary>Scrub a stray Provoke mark on the player's own side. Runs every live
    /// tick, independent of the hold's state. The bearer is walked too. Uses the mask-scoped
    /// ClearMark (ProvokeHold.Policy.cs), since composed +0x45 also holds Dead/Undead/Charging/Jump
    /// and KillTracker reads it.</summary>
    private void ScrubPlayerSideMarks()
    {
        byte mask = StatusApply.StatusMask(MarkId);
        int by = StatusApply.StatusByte(MarkId);
        bool anyRefused = false;
        int refusedSeat = -1;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e) || !OnField(_mem, e) || IsEnemySide(_mem, e)) continue;
            if ((_mem.U8(e + StatusApply.Composed + by) & mask) == 0) continue;

            if (ClearMark(_mem, e))
            {
                var id = ReadIdentity(_mem, e);
                int gx = _mem.U8(e + Offsets.AGx), gy = _mem.U8(e + Offsets.AGy);
                ModLogger.EventWithTrace(LogVerb.Signature,
                    "A stray provoke mark landed on one of your own units; the runtime scrubbed it off.",
                    $"provoke player-side mark scrubbed (nameId {id.nameId}, tile {gx},{gy})");
                Flight.Record("provoke", $"scrub-player-side nameId={id.nameId} tile={gx},{gy}");
            }
            // Keep only the first refusal this tick; one line names one seat.
            else if (!anyRefused) { anyRefused = true; refusedSeat = s; }
        }

        // Signatures.StuckEdge latch (the log flood shape, as in Provoke.Table.cs's
        // _boundsRefusedLogged): one call per sweep so a page that stays unwritable does not
        // re-log this warn every 33ms tick.
        if (Signatures.StuckEdge(ref _scrubRefusedLogged, anyRefused))
            ModLogger.WarnWithTrace(LogVerb.Signature,
                "Could not scrub a provoke mark off one of your own units; the guarded write was refused",
                $"provoke player-side mark-clear refused (seat {refusedSeat})");
    }
}
