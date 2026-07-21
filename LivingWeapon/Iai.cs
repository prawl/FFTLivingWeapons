using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Ame-no-Murakumo's "Iai" signature: opening-turn Speed hold. At +3, every deployed
/// main-hand wielder's Speed is held at field-max + Tuning.IaiSpeedMargin from battle-open
/// so it goes first (turn order = Speed; ties lose). After the wielder takes its opening
/// turn, Speed reverts to the captured natural.
///
/// Speed byte: entry + Offsets.ASpeed (= CSpeed 0x40 - BandEntry 0x1C = 0x24).
/// LIVE-VERIFIED 2026-07-01: write-50 to entry+0x24 -> displayed Speed 50 + unit goes first.
///
/// Release detection (REBUILT 2026-07-01, positional -- the own-CT apparatus is DEAD): the
/// engine's own ActorPtr global (Offsets.ActorPtr, resolved to a band-entry ADDRESS via
/// Band.ActorEntry) is compared against each wielder's own band entry. Fires on arrival (the
/// pointer transitions to the wielder's entry) OR an Acted-edge match (the flag rises 0-&gt;1
/// while the pointer already equals the wielder's entry) -- see Iai.Policy.ReleaseSignal for the
/// pure decision and its priming contract. The prior own-CT pull-down (entry+ACtSlam read,
/// ClassifyCt, IaiCtHigh/IaiCtReleased) is DELETED: it re-derived the acting unit's own scheduler
/// CT, which read cleanly on ENEMY turns (CharmLock, Maim both still use it) but INCONSISTENTLY
/// on the PLAYER'S OWN actively-managed unit -- live 2026-07-01's 3-unit repro (two Ame-no-
/// Murakumo wielders + a faster Ninja) showed one of the two wielders never observing its own
/// CT pull-down, so it never released and stayed pinned at fieldMax+1 until the 90s wall-clock
/// cap. ActorPtr sidesteps the read-reliability problem: it is the engine's OWN notion of "whose
/// turn is it right now", observed 2026-07-01 by tools/probes/unitid_probe.py "watch" (see
/// docs/LIVE_LEDGER.md's Uncertain row -- flip pending Patrick's live-verify of THIS rebuild).
///
/// RELEASE v2 -- IDENTITY-MATCH (same day, 2026-07-01, tools/probes/unitid_probe.py "watch"/
/// "find"): the ADDRESS-compare release above still starves. Root cause: band seat 28 is a
/// revolving engine MIRROR frame that clones different real units over time WITH real positions
/// (observed carrying the Samurai's identity, then later the Ninja's, in the same battle). When
/// it mirrors an Iai wielder, Wielder.Locate sees TWO real-position entries sharing
/// (weapon,brave,faith) and ambiguity-bails (returns 0) -- the wielder drops out of the per-tick
/// _wielders list. Two consequences of the OLD design, both fixed here: (a) the wielder's real
/// arrival (its actual turn) is consumed unseen, because the release check lived INSIDE the
/// resolved-_wielders loop and _prevActing advanced regardless; (b) the wall-clock cap ALSO lived
/// inside that loop, so a never-resolving wielder never even capped -- a starved hold with no
/// backstop. Fix: every unreleased HoldState now captures its roster nameId ONCE at arm time
/// (Wielder.RosterNameId, from the roster copy) and identity-matches it against the ACTING
/// entry's frame nameId back-reference (Offsets.ANameId, one guarded read per tick) --
/// Iai.Policy.ReleaseSignalById. The frame nameId survives the mirror churn even when the
/// wielder's own band-entry ADDRESS does not, because the ACTOR POINTER always names the real
/// frame (mirrors never become the acting unit's pointer target). Release AND cap now run in one
/// pass over every unreleased hold, AFTER the wielder-resolve/boost loop, regardless of whether
/// that hold's wielder resolved THIS tick -- closing both (a) and (b). A hold whose nameId
/// capture failed (Wielder.RosterNameId returned zero or negative) falls back to the original
/// address-compare ReleaseSignal against the hold's last-known entry (HoldState.LastEntry) --
/// degraded to v1 behavior for that hold, never worse.
///
/// No fingerprint re-identification for release: each wielder's band entry ADDRESS is compared
/// directly, so two wielders with identical (maxHP,hp,level) release independently.
///
/// Field-max scan excludes ALL resolved Iai wielders (not just self) so two wielders do not
/// ratchet each other's Speed upward every tick (F2). fieldMax is computed once per tick.
///
/// Capture-before-write: NaturalSpeed is captured once on first sane sight before the first
/// boost. Release writes the original capture back, so even many boosted ticks leave no residue.
///
/// Backstop: wall-clock IaiHoldCapSeconds releases even when the pointer never resolves to a
/// wielder's entry (safety terminator -- a permanent fast unit all battle is the worst-case miss).
/// Fires for every unreleased hold independent of _wielders resolution (see RELEASE v2 above);
/// its restore is always best-effort via HoldState.LastEntry (a stale address is still better
/// than leaving a wielder permanently fast for the rest of the battle).
///
/// KNOWN LIMITATION (pre-existing, unchanged by this rebuild): two wielders sharing an IDENTICAL
/// roster fingerprint (lvl,br,fa) collide on the fp-keyed <see cref="_holds"/> dictionary and
/// share one HoldState. Vanishingly rare (the live repro's two wielders had distinct
/// fingerprints); the release SIGNAL itself no longer depends on the fingerprint at all -- only
/// the arming/hold bookkeeping does. Now ALSO covers a shared/ambiguous roster-nameId capture:
/// Wielder.RosterNameId returns -1 when it can't uniquely identify a nameId for a hold's fp,
/// which just routes that hold to the address-fallback path above -- never a crash, never a
/// silent starve. And the inverse corner: two wielders with DIFFERENT fingerprints whose roster
/// slots carry the SAME nameId (duplicated roster identities) would both identity-match the same
/// acting unit and release together on the first one's turn -- premature-release direction only
/// (both lose the boost early; nobody is left permanently fast), so it fails safe.
///
/// LW-90 post-release corrective. Its premise was OBSERVED live 2026-07-21 (owner repro
/// session) but is NOT proven: docs/LIVE_LEDGER.md files that row under Uncertain, and only
/// the owner flips a row to Proven, so treat what follows as the working theory the corrective
/// is built on. The theory: the engine's per-turn normalize re-paints the unit's baseline after
/// our release, and that baseline captured our boost (Iai arms at battle open, before the
/// snapshot), so the released boost came back in every Iai battle observed so far unless
/// re-corrected: fresh battles ride the hold's own
/// LastTarget, restarted ones the ledger's BakedResidue; the corrective watches both. Accepted
/// corner: it rewrites natural over ANY byte reading exactly one of those two values for the
/// rest of the battle, with no foreign-value discrimination -- a real Speed buff landing the
/// wielder exactly on such a value is repeatedly stomped (guarded, natural-derived, one battle,
/// needs the exact collision).
///
/// LW-71 FLAGS CORROBORATION (2026-07-11): the ActorPtr release above still parks on STRUCK
/// units, so an enemy hitting the wielder before its own opening turn can false-release it via a
/// parked-pointer arrival mid-enemy-turn. Every release is now corroborated against the per-unit
/// PSX turn flag (<see cref="Band.FlagOwner"/>, LW-63, proven live manual + auto-battle
/// 2026-07-11), which names whichever unit's turn is structurally OPEN. Iai.Policy.FlagCorroboration
/// verdicts: CONFIRM (flag owner IS the wielder) releases alone, regardless of the legacy signal;
/// REFUSE (flag owner is someone else) blocks release even against a firing legacy signal, the
/// actual fix; INDETERMINATE (unresolved, e.g. a genuine zero-t battle-opening record, or an
/// uncomparable identity) falls through to the legacy signal so release never starves. A
/// CONFIRMED release restores Speed to the FLAG OWNER's entry, never the pointer's, since the
/// pointer may be parked elsewhere at that exact instant. Degraded corner (cap-bounded, no worse
/// than the pre-existing v1 address-fallback lane): a STALE LastEntry on an address-fallback hold
/// can be falsely Refused during the wielder's real turn, because the fresh flag-owner address
/// never matches the stale one.
/// </summary>
internal sealed partial class Iai : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField, ctx.Now);
    private const int AmeNoMurakumoId = 42;
    private const int SpeedSaneMin = 1;
    private const int SpeedSaneMax = Tuning.IaiSpeedSaneMax;   // 99

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly NaturalLedger _ledger;
    private readonly List<(long entry, (int lvl, int br, int fa) fp)> _wielders = new();
    private readonly Dictionary<(int lvl, int br, int fa), HoldState> _holds = new();

    // Priming (F5, load-bearing -- see Iai.Policy.ReleaseSignal's doc comment): both
    // previous-state values are seeded from the CURRENT read on the first evaluated tick
    // WITHOUT evaluating a release that tick. ResetBattle clears _primed so a new battle
    // re-primes instead of reusing a stale prior battle's pointer/Acted state.
    private bool _primed;
    private long _prevActing;
    private bool _prevActed;

    public Iai(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
               IGameMemory? mem = null, NaturalLedger? ledger = null)
    {
        _mem   = mem ?? new LiveMemory();
        _meta  = meta;
        _kills = kills;
        // LW-90: Engine passes the ONE shared instance (mushinArmed precedent); the default is
        // only for tests, where a private ledger is inert unless the test drives it.
        _ledger = ledger ?? new NaturalLedger();
    }

    public void ResetBattle()
    {
        _holds.Clear();
        _primed = false;
    }

    public void Tick(bool onField, DateTime now)
    {
        if (!onField) return;
        if (!_meta.TryGetValue(AmeNoMurakumoId, out var m) || m.Signature is null || !m.Signature.Iai) return;

        int tier = Tuning.TierOf(_kills, AmeNoMurakumoId);
        if (!ShouldHold(tier, m.Signature.AtTier)) return;

        Wielder.ResolveDeployedMainHandAll(_mem, AmeNoMurakumoId, _wielders);

        // Collect all Iai wielder entries before the scan so the field-max excludes them all (F2):
        // excluding only self makes two wielders ratchet each other upward to the sane clamp.
        var wielderEntries = new HashSet<long>(_wielders.Count);
        foreach (var (e, _) in _wielders) wielderEntries.Add(e);

        int fieldMax = ScanFieldMax(wielderEntries);

        // One pointer + one Acted read per tick (not per wielder -- the engine has ONE acting
        // unit at a time).
        long acting = Band.ActorEntry(_mem);
        bool actedNow = _mem.U8(Offsets.Acted) == 1;
        bool evaluate = _primed;
        if (!_primed) { _prevActing = acting; _prevActed = actedNow; _primed = true; }
        // One guarded read for the acting entry's frame nameId (Offsets.ANameId). U16 fail-safes
        // to 0 on an unreadable/invalid address, which the ReleaseSignalById holdNameId>0 guard
        // never matches (the 0==0 trap) -- see Iai.Policy.cs.
        int actingNameId = acting != 0 ? _mem.U16(acting + Offsets.ANameId) : 0;

        // LW-71: one guarded FlagOwner walk + one guarded nameId read per tick (not per wielder),
        // corroborating every hold's release against the per-unit PSX turn flag (structurally
        // names whichever unit's turn is OPEN, immune to the parked-pointer bug the pointer/Acted
        // signal above is exposed to, see the class doc comment).
        bool flagResolved = Band.FlagOwner(_mem, out long flagEntry, out _);
        int flagNameId = flagResolved ? _mem.U16(flagEntry + Offsets.ANameId) : 0;

        foreach (var (entry, fp) in _wielders)
        {
            if (!_holds.TryGetValue(fp, out var state))
            {
                state = new HoldState { ArmedAt = now };
                state.NameId = Wielder.RosterNameId(_mem, AmeNoMurakumoId, fp);
                _holds[fp] = state;
                ModLogger.Event(LogVerb.Signature, $"The Ame-no-Murakumo wielder's Speed is held above the whole field so they act first this battle (level {fp.lvl}, brave {fp.br}, faith {fp.fa})");
            }

            // Refresh the last-known-resolved entry every tick this wielder appears in _wielders,
            // whether or not it is still held -- the release+cap pass below needs it even on
            // ticks where Locate ambiguity-bails (mirror churn) and _wielders comes up empty.
            state.LastEntry = entry;

            long spAddr = entry + Offsets.ASpeed;
            if (!_mem.Writable(spAddr, 1)) continue;

            if (state.Released)
            {
                // LW-90 post-release corrective hold. Premise OBSERVED live 2026-07-21 (the
                // owner's repro session, 11:03-11:08 log), NOT proven: the LIVE_LEDGER row is
                // still Uncertain, owner flip pending. The theory: the engine's per-turn
                // normalize re-paints the unit's baseline AFTER our release, and that baseline
                // captured our boost (Iai arms within ~100ms of battle open, before the game
                // snapshots it), so in every Iai battle observed so far, restarted or fresh,
                // the released boost came back
                // unless re-corrected. Watch BOTH known-ours values for the rest of the
                // battle: the ledger-flagged restart residue (BakedResidue) AND the hold's
                // own last written target (LastTarget; the fresh-battle case, which the
                // residue-only corrective missed while the owner watched Speed ride at 13).
                // Living in the resolved-wielder loop is safe for an identified wielder: the
                // D3 tier-1 tie-break resolves nameId-verified mirror copies instead of
                // bailing (pinned by IaiTests.Post_release_corrective_survives_mirror_churn).
                int cur0 = _mem.U8(spAddr);
                bool ours = (state.BakedResidue > 0 && cur0 == state.BakedResidue)
                         || (state.LastTarget > 0 && cur0 == state.LastTarget
                             && cur0 != state.NaturalSpeed);
                if (ours && state.NaturalSpeed >= SpeedSaneMin)
                {
                    _mem.W8(spAddr, (byte)state.NaturalSpeed);
                    ModLogger.Debug(LogVerb.Signature,
                        $"iai: normalized boost re-corrected post-release ({cur0} -> {state.NaturalSpeed})");
                }
                continue;
            }

            // Hold: capture natural once, then write the clamped target every tick.
            int cur = _mem.U8(spAddr);
            if (state.NaturalSpeed < SpeedSaneMin)
            {
                // First sight: capture before boosting (F5). Skip if cur is not sane.
                if (cur < SpeedSaneMin || cur > SpeedSaneMax) continue;
                // LW-90: the ledger sees through the mod's own restart residue (a first sight
                // that exactly equals a target recorded for this unit in the previous battle
                // attempt); everything else passes through and refreshes the entry.
                state.NaturalSpeed = _ledger.FilterCapture(state.NameId, StatLane.Speed, cur, fp.lvl, out int baked);
                state.BakedResidue = baked;
                if (baked > 0)
                    ModLogger.EventWithTrace(LogVerb.Signature,
                        $"A battle restart carried the wielder's held Speed boost over as if it were natural; the true value is restored (level {fp.lvl}, brave {fp.br}, faith {fp.fa}).",
                        $"iai: restart residue corrected at capture (read {baked}, natural {state.NaturalSpeed})");
            }

            int raw     = Target(state.NaturalSpeed, fieldMax, Tuning.IaiSpeedMargin);
            int clamped = raw < SpeedSaneMin ? SpeedSaneMin : raw > SpeedSaneMax ? SpeedSaneMax : raw;
            // LW-90: record every held target per evaluation (the ledger dedups), so a restarted
            // battle's first sight of this exact value is recognized as the mod's own residue;
            // LastTarget feeds the post-release corrective (the engine appears to normalize our
            // boost back after release: observed 2026-07-21, LIVE_LEDGER row still Uncertain).
            _ledger.RecordWrite(state.NameId, StatLane.Speed, clamped);
            state.LastTarget = clamped;
            _mem.W8(spAddr, (byte)clamped);
        }

        // Release + cap: one pass over EVERY unreleased hold, not just this tick's resolved
        // _wielders (N6 FIX, 2026-07-01) -- a mirror-churned wielder that ambiguity-bails out of
        // _wielders must still be able to release (identity path) and must still be able to cap
        // (wall-clock backstop, previously starved alongside it).
        foreach (var (fp, state) in _holds)
        {
            if (state.Released) continue;

            bool identity = state.NameId > 0;
            bool legacy = identity
                ? ReleaseSignalById(_prevActing, acting, _prevActed, actedNow, actingNameId, state.NameId)
                : ReleaseSignal(_prevActing, acting, _prevActed, actedNow, state.LastEntry);
            // LW-71: corroborate the legacy pointer signal against the turn-flags owner. Identity
            // path compares nameId (0 on either side is an unidentifiable owner, never a match,
            // the same 0==0 trap ReleaseSignalById guards against); address path compares the
            // flag owner's entry directly against this hold's last-known address.
            FlagVerdict flags = identity
                ? FlagCorroboration(flagResolved, ownerIdentityKnown: flagNameId > 0, ownerIsWielder: flagNameId == state.NameId)
                : FlagCorroboration(flagResolved, ownerIdentityKnown: state.LastEntry != 0, ownerIsWielder: flagEntry == state.LastEntry);
            bool released = evaluate && ReleaseDecision(legacy, flags);
            bool capFired = !released && (now - state.ArmedAt).TotalSeconds >= Tuning.IaiHoldCapSeconds;
            if (!released && !capFired) continue;

            // Restore address (LW-71): a flags-CONFIRMED release can fire while the pointer is
            // parked on a DIFFERENT unit entirely, so it restores to the FLAG OWNER's entry (the
            // wielder's real frame at this instant), never the pointer's. A legacy identity
            // release still restores to the pointer-provided REAL entry (authoritative at exactly
            // this instant); a legacy address-fallback release and the cap both restore to the
            // hold's last-known entry (best-effort: a stale address is still better than leaving
            // the wielder permanently fast for the rest of the battle). LastEntry is 0 only if a
            // hold was somehow never armed via the _wielders loop (defensive; skip the write).
            bool confirmed = released && flags == FlagVerdict.Confirm;
            long restoreAddr = confirmed ? flagEntry + Offsets.ASpeed
                              : (released && identity) ? acting + Offsets.ASpeed
                              : state.LastEntry != 0 ? state.LastEntry + Offsets.ASpeed : 0;
            if (restoreAddr != 0 && state.NaturalSpeed >= SpeedSaneMin && _mem.Writable(restoreAddr, 1))
                _mem.W8(restoreAddr, (byte)state.NaturalSpeed);

            state.Released = true;
            string why = !released ? "the wall-clock cap" : confirmed ? "the turn flags" : "the actor pointer";
            ModLogger.Event(LogVerb.Signature, $"The wielder's opening-turn Speed boost is removed (level {fp.lvl}, brave {fp.br}, faith {fp.fa}; released by {why})");
        }

        _prevActing = acting;
        _prevActed = actedNow;
    }

    /// <summary>Max Speed among all valid band entries NOT in <paramref name="wielderEntries"/>,
    /// sane-gated to 1..99 so one garbage-high read cannot pin wielders to the clamp.
    /// Returns 0 when every valid entry is an Iai wielder (all-Iai battle; target falls
    /// back to max(natural, 0+margin) = natural, which is safe).</summary>
    private int ScanFieldMax(HashSet<long> wielderEntries)
    {
        int max = 0;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (wielderEntries.Contains(e)) continue;
            if (!Band.IsValid(_mem, e)) continue;
            int spd = _mem.U8(e + Offsets.ASpeed);
            if (spd >= SpeedSaneMin && spd <= SpeedSaneMax && spd > max) max = spd;
        }
        return max;
    }

    /// <summary>Mutable per-wielder hold state, keyed by roster fingerprint (lvl,br,fa) in
    /// <see cref="_holds"/>. A reference type so Tick mutates the SAME instance in place across ticks.</summary>
    private sealed class HoldState
    {
        public DateTime ArmedAt;
        /// <summary>Captured on first sane sight before the first boost; -1 = not yet captured.</summary>
        public int NaturalSpeed = -1;
        /// <summary>LW-90: the residue value a corrected capture read (the mod's own prior
        /// boost, baked into the restarted battle's byte); 0 = the capture was clean. While
        /// set, the post-release corrective hold re-writes NaturalSpeed over any byte reading
        /// exactly this value.</summary>
        public int BakedResidue;
        /// <summary>The hold's own last written target this battle; 0 = never held. The
        /// post-release corrective's second token: the engine's normalize appears to re-paint
        /// OUR boost after release in fresh battles too (observed 2026-07-21, LIVE_LEDGER row
        /// still Uncertain), where BakedResidue is 0 because the capture was clean.</summary>
        public int LastTarget;
        public bool Released;
        /// <summary>Roster nameId captured ONCE at arm time (Wielder.RosterNameId); -1 = capture
        /// failed (ambiguous or unmatched) -- also &lt;= 0 when a single matched roster slot's own
        /// nameId read 0 (unseeded/invalid). Either way the caller's guard is "&gt; 0"; a
        /// non-positive value routes release/cap restore through the address-fallback path.</summary>
        public int NameId = -1;
        /// <summary>The most recent band entry this hold's wielder resolved to (updated every tick
        /// it appears in _wielders). The address-fallback release target AND the cap's best-effort
        /// restore address -- both survive a tick (or many) where the wielder fails to resolve.</summary>
        public long LastEntry;
    }
}
