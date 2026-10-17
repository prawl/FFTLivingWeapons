using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Ame-no-Murakumo "Iai": at +3 each deployed main-hand wielder's Speed is held at field-max + Tuning.IaiSpeedMargin from battle open so it acts first, then reverts to its captured natural after its opening turn.
/// Speed byte is entry + Offsets.ASpeed (0x40 - 0x1C = 0x24); writing 50 showed Speed 50 and the unit went first (2026-07-01).
/// Release (rebuilt 2026-07-01, tools/probes/unitid_probe.py "watch"/"find"; docs/LIVE_LEDGER.md row still Uncertain): ActorPtr arrival or an Acted-edge, matched by roster nameId against the acting frame's nameId (Iai.Policy.ReleaseSignalById).
/// Band seat 28 is a mirror frame that clones real units and makes Wielder.Locate bail, so release and the IaiHoldCapSeconds cap run over every hold, resolved or not.
/// nameId is used because the pointer always names the real frame, never the seat-28 mirror, so it survives churn when the address does not.
/// A hold with no nameId falls back to the address compare (Iai.Policy.ReleaseSignal). An own-CT pull-down was dropped because it read unreliably on the player's own unit.
/// The field-max scan excludes all Iai wielders (F2); natural Speed is captured once before the first boost (F5).
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
    private readonly List<(long entry, (int lvl, int br, int fa) fp, int nameId)> _wielders = new();
    // LW-252 stage 5 (2026-08-17): keyed by nameId, fp fallback (see WielderKeyedStore). Seeded fp-twins get separate holds;
    // a pair where either nameId never resolves still shares one, and two wielders with the same nameId release together early (fails safe).
    private readonly WielderKeyedStore<HoldState> _holds = new();

    // Priming (F5, see Iai.Policy.ReleaseSignal): the first evaluated tick seeds the previous-state values without releasing.
    // ResetBattle clears _primed so a new battle does not reuse stale pointer/Acted state.
    private bool _primed;
    private long _prevActing;
    private bool _prevActed;

    public Iai(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
               IGameMemory? mem = null, NaturalLedger? ledger = null)
    {
        _mem   = mem ?? new LiveMemory();
        _meta  = meta;
        _kills = kills;
        // LW-90: Engine passes the shared ledger; the default is for tests.
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

        // Collect every Iai wielder entry first so the field-max excludes them all (F2); excluding only self lets two wielders ratchet each other up.
        var wielderEntries = new HashSet<long>(_wielders.Count);
        foreach (var (e, _, _) in _wielders) wielderEntries.Add(e);

        int fieldMax = ScanFieldMax(wielderEntries);

        // One pointer and one Acted read per tick; the engine has one acting unit at a time.
        long acting = Band.ActorEntry(_mem);
        bool actedNow = _mem.U8(Offsets.Acted) == 1;
        bool evaluate = _primed;
        if (!_primed) { _prevActing = acting; _prevActed = actedNow; _primed = true; }
        // U16 returns 0 on an unreadable address; ReleaseSignalById requires holdNameId > 0 so 0 never matches 0.
        int actingNameId = acting != 0 ? _mem.U16(acting + Offsets.ANameId) : 0;

        // LW-71 (2026-07-11, flags lane LW-63): the actor pointer parks on struck units, so a hit before the wielder's turn could false-release it; the PSX turn flag names whose turn is actually open.
        bool flagResolved = Band.FlagOwner(_mem, out long flagEntry, out _);
        int flagNameId = flagResolved ? _mem.U16(flagEntry + Offsets.ANameId) : 0;

        ApplyHolds(now, fieldMax);
        SweepReleases(evaluate, acting, actedNow, actingNameId, flagResolved, flagEntry, flagNameId, now);

        _prevActing = acting;
        _prevActed = actedNow;
    }

    /// <summary>Pass 1: arm and hold each resolved wielder (capture natural once, then write the clamped Speed every tick);
    /// released wielders get the LW-90 corrective below.</summary>
    private void ApplyHolds(DateTime now, int fieldMax)
    {
        foreach (var (entry, fp, nameId) in _wielders)
        {
            // LW-252 stage 5: factory runs only on a genuine first arm. null means an ambiguous nameId-0 tick while two seeded fp-twins already hold, so treat it as not located this tick.
            var state = _holds.GetOrCreate(nameId, fp, () =>
            {
                var s = new HoldState { ArmedAt = now };
                s.NameId = Wielder.RosterNameId(_mem, AmeNoMurakumoId, fp);
                ModLogger.Event(LogVerb.Signature, $"The Ame-no-Murakumo wielder's Speed is held above the whole field so they act first this battle (level {fp.lvl}, brave {fp.br}, faith {fp.fa})");
                return s;
            });
            if (state == null) continue;

            // Refresh every tick so the release/cap pass has an address even when mirror churn drops the wielder from _wielders.
            state.LastEntry = entry;

            long spAddr = entry + Offsets.ASpeed;
            if (!_mem.Writable(spAddr, 1)) continue;

            if (state.Released)
            {
                // LW-90 corrective (observed 2026-07-21, owner repro 11:03-11:08 log, docs/LIVE_LEDGER.md row still Uncertain): the engine's per-turn normalize re-paints a baseline that captured our boost (Iai arms ~100ms after battle open), so the boost returns after release.
                // Watch BakedResidue (restart case) and LastTarget (fresh battle) for the rest of the battle; a real Speed buff landing on either value gets overwritten.
                // Safe inside the resolved loop because the D3 tier-1 tie-break resolves nameId-verified mirror copies (IaiTests.Post_release_corrective_survives_mirror_churn).
                int cur0 = _mem.U8(spAddr);
                bool ours = OursResidue(cur0, state.BakedResidue, state.LastTarget, state.NaturalSpeed);
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
                // Capture before boosting (F5); skip if the read is not sane.
                if (cur < SpeedSaneMin || cur > SpeedSaneMax) continue;
                // LW-90: the ledger discards a first sight equal to a target recorded for this unit in the previous attempt (restart residue).
                state.NaturalSpeed = _ledger.FilterCapture(state.NameId, StatLane.Speed, cur, fp.lvl, out int baked);
                state.BakedResidue = baked;
                if (baked > 0)
                    ModLogger.EventWithTrace(LogVerb.Signature,
                        $"A battle restart carried the wielder's held Speed boost over as if it were natural; the true value is restored (level {fp.lvl}, brave {fp.br}, faith {fp.fa}).",
                        $"iai: restart residue corrected at capture (read {baked}, natural {state.NaturalSpeed})");
            }

            int raw     = Target(state.NaturalSpeed, fieldMax, Tuning.IaiSpeedMargin);
            int clamped = raw < SpeedSaneMin ? SpeedSaneMin : raw > SpeedSaneMax ? SpeedSaneMax : raw;
            // LW-90: record each held target so a restart recognizes it as residue; LastTarget feeds the post-release corrective.
            _ledger.RecordWrite(state.NameId, StatLane.Speed, clamped);
            state.LastTarget = clamped;
            _mem.W8(spAddr, (byte)clamped);
        }
    }

    /// <summary>Pass 2: release and cap over every unreleased hold, not just this tick's _wielders,
    /// so a mirror-churned wielder can still release and still hit the wall-clock cap (N6, 2026-07-01).</summary>
    private void SweepReleases(bool evaluate, long acting, bool actedNow, int actingNameId,
        bool flagResolved, long flagEntry, int flagNameId, DateTime now)
    {
        foreach (var (state, fp, _) in _holds.All)
        {
            if (state.Released) continue;

            bool identity = state.NameId > 0;
            bool legacy = identity
                ? ReleaseSignalById(_prevActing, acting, _prevActed, actedNow, actingNameId, state.NameId)
                : ReleaseSignal(_prevActing, acting, _prevActed, actedNow, state.LastEntry);
            // LW-71: corroborate the legacy signal against the turn-flag owner. Identity path compares nameId (0 is never a match); address path compares the owner's entry to LastEntry.
            // Known corner: a stale LastEntry on an address-fallback hold can be falsely Refused on the real turn; the cap bounds it.
            FlagVerdict flags = identity
                ? FlagCorroboration(flagResolved, ownerIdentityKnown: flagNameId > 0, ownerIsWielder: flagNameId == state.NameId)
                : FlagCorroboration(flagResolved, ownerIdentityKnown: state.LastEntry != 0, ownerIsWielder: flagEntry == state.LastEntry);
            bool released = evaluate && ReleaseDecision(legacy, flags);
            bool capFired = !released && (now - state.ArmedAt).TotalSeconds >= Tuning.IaiHoldCapSeconds;
            if (!released && !capFired) continue;

            // Restore address (LW-71): a flags-confirmed release restores to the flag owner's entry since the pointer may be parked elsewhere;
            // an identity release uses the pointer's entry; address-fallback and cap use LastEntry (stale beats leaving the unit fast). LastEntry 0 skips the write.
            var (restoreAddr, confirmed) = RestoreAddress(released, identity, flags, flagEntry, acting, state.LastEntry);
            if (restoreAddr != 0 && state.NaturalSpeed >= SpeedSaneMin && _mem.Writable(restoreAddr, 1))
                _mem.W8(restoreAddr, (byte)state.NaturalSpeed);

            state.Released = true;
            string why = !released ? "the wall-clock cap" : confirmed ? "the turn flags" : "the actor pointer";
            ModLogger.Event(LogVerb.Signature, $"The wielder's opening-turn Speed boost is removed (level {fp.lvl}, brave {fp.br}, faith {fp.fa}; released by {why})");
        }
    }

    /// <summary>Max Speed among valid band entries not in <paramref name="wielderEntries"/>, sane-gated to 1..99 so one garbage read cannot pin wielders to the clamp.
    /// Returns 0 when every valid entry is a wielder, so the target falls back to natural.</summary>
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

    /// <summary>Mutable per-wielder hold state, stored in <see cref="_holds"/> (LW-252 stage 5). A class so Tick mutates it in place.</summary>
    private sealed class HoldState
    {
        public DateTime ArmedAt;
        /// <summary>Captured on first sane sight before the first boost; -1 = not yet captured.</summary>
        public int NaturalSpeed = -1;
        /// <summary>LW-90: the residue value a corrected capture read (the mod's own prior boost); 0 = clean. The corrective rewrites NaturalSpeed over any byte reading this.</summary>
        public int BakedResidue;
        /// <summary>Last written target this battle; 0 = never held. The corrective's second token, for fresh battles where BakedResidue is 0 (observed 2026-07-21).</summary>
        public int LastTarget;
        public bool Released;
        /// <summary>Roster nameId captured at arm time (Wielder.RosterNameId); -1 = capture failed, &lt;= 0 also when the slot's nameId read 0. Non-positive routes release to the address fallback.</summary>
        public int NameId = -1;
        /// <summary>Last band entry this hold resolved to: the address-fallback release target and the cap's restore address.</summary>
        public long LastEntry;
    }
}
