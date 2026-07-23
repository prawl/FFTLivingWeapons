using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// ProvokeHold's band-address-space half: locate the marked enemy, re-locate any previously-seen
/// identity, and enumerate who counts as a hide target. The real seam that keeps ProvokeHold.cs
/// under the 200-line refactor trigger -- every raw band walk lives here, nowhere else.
///
/// Identity is the SAME (nameId, maxHp, level, brave, faith) tuple <see cref="Band.FlagOwner"/> and
/// <see cref="Wielder"/> already key on elsewhere in this codebase: nameId narrows when it reads
/// nonzero on both sides, the (maxHp,level,brave,faith) fingerprint is the fallback that also lets
/// two nameId-unseeded twins still resolve deterministically to the FIRST real-position match.
/// </summary>
internal sealed partial class ProvokeHold
{
    /// <summary>Every valid, on-field seat's identity tuple, read straight off its own band entry.</summary>
    private static (int nameId, int mhp, int lvl, int br, int fa) ReadIdentity(IGameMemory mem, long e) =>
        (mem.U16(e + Offsets.ANameId), mem.U16(e + Offsets.AMaxHp), mem.U8(e + Offsets.ALevel),
         mem.U8(e + Offsets.ABrave), mem.U8(e + Offsets.AFaith));

    /// <summary>Same identity: the (maxHp,level,brave,faith) fingerprint must match; a nameId that
    /// reads nonzero on BOTH sides must also agree (an unseeded/zero nameId on either side trusts the
    /// fingerprint alone, the same veto shape <see cref="Wielder"/>'s locate layer uses).</summary>
    private static bool SameIdentity((int nameId, int mhp, int lvl, int br, int fa) a,
                                     (int nameId, int mhp, int lvl, int br, int fa) b)
    {
        if (a.mhp != b.mhp || a.lvl != b.lvl || a.br != b.br || a.fa != b.fa) return false;
        return a.nameId == 0 || b.nameId == 0 || a.nameId == b.nameId;
    }

    /// <summary>Decision 6: enemy iff the friend/foe bit (band +0x1D2 bit 0x10) is set. Read-only.</summary>
    private static bool IsEnemySide(IGameMemory mem, long e) =>
        (mem.U8(e + Offsets.AFriendFoe) & Offsets.AFriendFoeEnemyBit) != 0;

    /// <summary>Does the engine's own actor pointer (Offsets.ActorPtr, via Band.ActorEntry) currently
    /// name a band entry sharing the marked enemy's identity? Replaces reading the marked enemy's own
    /// ATurnFlag byte, observed FLAKY live 2026-07-22 (hid 0 units one attempt, missed the turn-done
    /// edge another -> 30s watchdog); the actor pointer names the acting unit reliably on both teams
    /// (LIVE_LEDGER "actor pointer names the acting unit" 2026-07-01). No Band.IsValid gate on the
    /// pointed-to entry -- mirrors Iai's own actor-nameId read (Iai.cs), which trusts Band.ActorEntry's
    /// own pointer-shape validation and skips content sanity, since a garbage/zeroed entry can only
    /// coincidentally collide with an already-validated marked identity. Callers must ALSO gate on
    /// TqTeam==1 (an enemy turn): the pointer PARKS ON STRUCK VICTIMS, so during a PLAYER turn it can
    /// name the marked enemy the instant it gets hit without that ever being its turn (see
    /// ProvokeHoldTests' Actor_pointer_parked_on_the_marked_enemy_during_a_player_turn_does_not_count_as_its_turn).</summary>
    private static bool MarkedIsActor(IGameMemory mem, (int nameId, int mhp, int lvl, int br, int fa) markedId)
    {
        long actorEntry = Band.ActorEntry(mem);
        return actorEntry != 0 && SameIdentity(ReadIdentity(mem, actorEntry), markedId);
    }

    /// <summary>Decision 6's ghost-seat gate (criterion 7): combat +0x01 (band entry + AGateByte)
    /// reading 0xFF marks a cutscene/ghost seat that <see cref="Band.IsValid"/> alone lets through.</summary>
    private static bool OnField(IGameMemory mem, long e) => mem.U8(e + Offsets.AGateByte) != Offsets.AGateHiddenValue;

    /// <summary>Structurally dead (regardless of HP): HP reads 0, or the Dead status bit is set --
    /// the same combined test KillTracker uses, so a status-death the 33ms poll never sees HP for is
    /// still caught.</summary>
    internal static bool IsAlive(IGameMemory mem, long e) =>
        mem.U16(e + Offsets.AHp) > 0 && (mem.U8(e + Offsets.ADeadStatus) & Offsets.ADeadBit) == 0;

    /// <summary>R2: true iff the entry's COMPOSED status carries any id in <paramref name="disablingIds"/>
    /// (Tuning.ProvokeDisablingStatusIds) -- the provoked enemy can no longer carry out its provoked
    /// turn (Petrify, Stop, Sleep, ...), so the hold should release rather than linger.</summary>
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

    /// <summary>Scan the whole band for a valid, on-field, ENEMY-side seat carrying the mark
    /// (composed +0x45 bit 0x80). Prefers a real-position entry; returns a frozen (0,0) twin only
    /// when no real-position candidate carries the mark. False (entry 0, identity default) when
    /// nobody is marked.</summary>
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

    /// <summary>Re-locate ANY previously-identified band entry by its captured identity: an exact
    /// nonzero nameId match wins outright (real position or not, since nameId already proves it is
    /// the same unit); otherwise the first real-position fingerprint match, falling back to a frozen
    /// (0,0) twin only when no real-position candidate exists. Side-agnostic by design -- used both
    /// to re-locate the marked enemy each armed tick and to re-locate a flagged ally at reveal time.</summary>
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

    /// <summary>WINDOW mode's own turn-owner read (decision 3's fallback): the condensed TurnQueue
    /// struct's sanity + team, fed straight into Policy.ActionFor. A garbage/insane read biases to
    /// Hide (ActionFor's own contract); this method only does the reading.</summary>
    private HideAction WindowAction()
    {
        int mhp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        int lvl = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        bool queueSane = mhp is >= 1 and <= 1999 && lvl is >= 1 and <= 99;
        int team = _mem.U16(Offsets.TurnQueue + Offsets.TqTeam);
        return ActionFor(queueSane, team);
    }

    /// <summary>Every valid, on-field, PLAYER-side seat except the bearer's own identity (criteria
    /// 4/8/9). A guest counts as player-side (the friend/foe bit is guest-complete, decision 6) and
    /// is hidden alongside the party; the bearer is excluded by identity, never by seat position.</summary>
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
}
