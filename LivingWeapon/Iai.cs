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
/// turn is it right now", live-proven 2026-07-01 by tools/probes/unitid_probe.py "watch" (see
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
               IGameMemory? mem = null)
    {
        _mem   = mem ?? new LiveMemory();
        _meta  = meta;
        _kills = kills;
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

            if (state.Released) continue;

            // Hold: capture natural once, then write the clamped target every tick.
            long spAddr = entry + Offsets.ASpeed;
            if (!_mem.Writable(spAddr, 1)) continue;

            int cur = _mem.U8(spAddr);
            if (state.NaturalSpeed < SpeedSaneMin)
            {
                // First sight: capture before boosting (F5). Skip if cur is not sane.
                if (cur < SpeedSaneMin || cur > SpeedSaneMax) continue;
                state.NaturalSpeed = cur;
            }

            int raw     = Target(state.NaturalSpeed, fieldMax, Tuning.IaiSpeedMargin);
            int clamped = raw < SpeedSaneMin ? SpeedSaneMin : raw > SpeedSaneMax ? SpeedSaneMax : raw;
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
            bool released = evaluate && (identity
                ? ReleaseSignalById(_prevActing, acting, _prevActed, actedNow, actingNameId, state.NameId)
                : ReleaseSignal(_prevActing, acting, _prevActed, actedNow, state.LastEntry));
            bool capFired = !released && (now - state.ArmedAt).TotalSeconds >= Tuning.IaiHoldCapSeconds;
            if (!released && !capFired) continue;

            // Identity release restores to the pointer-provided REAL entry (authoritative at
            // exactly this instant); the address-fallback release and the cap both restore to the
            // hold's last-known entry (best-effort -- a stale address is still better than leaving
            // the wielder permanently fast for the rest of the battle). LastEntry is 0 only if a
            // hold was somehow never armed via the _wielders loop (defensive; skip the write).
            long restoreAddr = (released && identity) ? acting + Offsets.ASpeed
                              : state.LastEntry != 0 ? state.LastEntry + Offsets.ASpeed : 0;
            if (restoreAddr != 0 && state.NaturalSpeed >= SpeedSaneMin && _mem.Writable(restoreAddr, 1))
                _mem.W8(restoreAddr, (byte)state.NaturalSpeed);

            state.Released = true;
            string why = released ? "the actor pointer" : "the wall-clock cap";
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
