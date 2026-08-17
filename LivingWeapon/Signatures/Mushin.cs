using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Kiku-ichimonji's "Mushin" signature: a full WAIT turn (no move, no act) arms ONE charge; the
/// wielder's next own action spends it.
///
/// PRIOR ROUNDS (2026-07-09, all same-day, compressed; the full narrative of each lived in this
/// file's own history until round 5 replaced it):
///   ROUND 1: the card's literal reading, but the wait moment itself proved invisible to every
///     signal tried that night (no per-unit footprint at all for a genuine full wait).
///   ROUND 2: rebuilt on the ENEMY side's scheduler CT cycle (Offsets.ACtSlam) instead, which
///     worked live, but armed off OTHER units' turns, never the wielder's own wait.
///   ROUND 3 (~18:00): swapped the CT-cycle aggregation from median to second-highest (an oracle
///     over-count bug could lock a median at 0 forever), same CT-cycle foundation.
///   ROUND 4 (~19:21): rebuilt CONSUME as a two-stage latch-then-confirm on KillTracker's
///     PlayerActSeq plus the wielder's own action record, closing a cursor-parked false-latch;
///     the ARM half still never read the wielder's own turn.
///   ROUND 5 (THIS FILE, owner decision: replace the whole apparatus): the literal design,
///     finally buildable on the engine's OWN per-unit turn bookkeeping, mapped live tonight.
///
/// THE PSX TURN-FLAG MAPPING (live-mapped 2026-07-09, tools/probes/mushin_wait_probe.py,
/// scratchpad/psxflags_watch.log; PSX struct offsets from FFHacktics, frame offset = PSX + 0x32;
/// band offset = frame offset - Offsets.BandEntry (0x1C), matching the AArec/ANameId convention
/// every other frame-window field in this codebase already uses):
///   TURN FLAG  PSX 0x186 -&gt; frame +0x1B8 -&gt; band +0x19C: 1 while the unit's move/act/wait menu
///     is open; 0-&gt;1 at turn open, 1-&gt;0 at turn end. Tape: rose at 4.50s/34.45s/73.84s, fell at
///     32.74s/41.37s/77.01s.
///   MOVED      PSX 0x187 -&gt; frame +0x1B9 -&gt; band +0x19D: 0-&gt;1 at the move (tape 40.32s, exact
///     step). Reset to 0 by the ENGINE at the NEXT turn open (tape 73.84s).
///   ACTED      PSX 0x188 -&gt; frame +0x1BA -&gt; band +0x19E: 0-&gt;1 at the action (tape 75.28s). Same
///     reset-at-open.
///   (PSX 0x189 -&gt; frame +0x1BB -&gt; band +0x19F, "Ability Outcome": decoded 0x02 = hit-by-ability,
///     0x01 = turn-ended. NOT consumed by this trigger; this closes the old +0x1BB candidate's
///     mystery from round 1's failed probes, documented here for the record only.)
///
/// TRIGGER (per resolved main-hand wielder, Wielder.ResolveDeployedMainHandAll, unchanged): track
/// the TURN FLAG's own previous value per wielder fingerprint (primed on first sight WITHOUT
/// deciding, safe even mid-turn, since the engine resets MOVED/ACTED at that turn's own open).
/// On the FALLING edge (prev 1 -&gt; now 0), read MOVED and ACTED at that same tick (both persist
/// until the next turn open, so edge-tick read timing is not critical) and decide via
/// MushinPolicy.ShouldConsume / ShouldArm: acted -&gt; CONSUME (armed 1-&gt;0, SPENT logged only if it
/// was actually armed); acted==0 &amp;&amp; moved==0 -&gt; FULL WAIT, arm (idempotent at 1, BANK logged);
/// acted==0 &amp;&amp; moved==1 -&gt; move-only, NOTHING (an armed charge SURVIVES untouched, the
/// original card's rule). A Debug line records every falling-edge decision (moved/acted values +
/// verdict), file-only, every time: this round's heartbeat.
///
/// NO OTHER SIGNALS: no KillTracker, no TurnTracker, no CT clocks, no static-array oracle, no
/// PlayerActSeq, no action-record confirm, no global Acted byte. Every read is per-wielder-entry
/// only, guarded (Readable pre-filter).
///
/// OFFSETS.CS PROMOTION (LW-55 stage 1): round 5 originally kept the three band-relative offsets
/// below as LOCAL consts (Offsets.cs carried uncommitted LW-51 work that round, so touching it was
/// deferred). That staging concern is moot since bf351db; the trio now lives in Offsets.cs
/// (ATurnFlag/AMoved/AActed) with the full PSX provenance, and this class simply reads it.
///
/// RESIDUALS (accepted, documented, not fixed tonight):
///   (1) a reaction by the wielder DURING its own open menu window (e.g. an enemy's charged spell
///       resolving mid-window) sets ACTED and turns a genuine wait into a no-arm or a consume:
///       the fail-safe direction (never mis-arms a charge that wasn't earned), rare, accepted.
///   (2) these flags are engine bookkeeping for the move/act/wait MENU; auto-battle and a
///       charmed wielder's turns should exercise them identically but are UNTESTED tonight,
///       live verification covers normal manual turns only.
///   (3) twin/mirror frames carry frozen flags; the twin-filtered Wielder.Locate underneath
///       ResolveDeployedMainHandAll already prefers the real (non-frozen) entry.
///
/// The armed store lives SHARED with GrowthEngine (constructed once in Engine.cs, passed to
/// both). LW-252 stage 5: re-keyed via <see cref="WielderKeyedStore{TState}"/> (nameId primary,
/// fp fallback -- see its class doc), closing the fp-twin collision the old fp-only dictionary
/// carried: two SEEDED Kiku-ichimonji wielders never cross-arm or cross-consume each other, even
/// sharing (lvl,br,fa); a mixed pair (either member's nameId never resolves) still shares one
/// arm count, matching yesterday's behavior for that pair. _prevTurnFlag rides its OWN private
/// store (not shared with GrowthEngine, which never reads it).
/// </summary>
internal sealed partial class Mushin : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);

    private const int KikuIchimonjiId = 45;

    /// <summary>Local aliases onto the Offsets.cs trio (LW-55 stage 1 promoted them out of this
    /// class; see Offsets.cs for the full PSX provenance). Kept as names here rather than inlined
    /// at every call site below, unchanged from round 5's own shape.</summary>
    private const int TurnFlagOffset = Offsets.ATurnFlag;
    private const int MovedOffset = Offsets.AMoved;
    private const int ActedOffset = Offsets.AActed;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly WielderKeyedStore<Box<int>> _armed;
    private readonly List<(long entry, (int lvl, int br, int fa) fp, int nameId)> _wielders = new();

    /// <summary>Per-wielder previous TURN FLAG value, boxed so it can ride WielderKeyedStore
    /// (LW-252 stage 5; private to this class -- GrowthEngine never reads it, unlike
    /// <see cref="_armed"/>). Box.Value == -1 means "not yet primed": the very next observed
    /// value is captured WITHOUT deciding (a mid-turn prime is safe, the flags reset at that
    /// turn's own open, so priming mid-window can never fabricate a phantom edge) -- -1 rather
    /// than a missing dictionary entry, since the store always returns a live Box once created;
    /// the sentinel is what distinguishes "never primed" from a legitimately observed 0.</summary>
    private readonly WielderKeyedStore<Box<int>> _prevTurnFlag = new();

    private readonly ScopedLogger _slog;   // armed gate: a benched/below-tier Kiku must not narrate on console
    private bool _wasActive;

    public Mushin(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
                  WielderKeyedStore<Box<int>> armed, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _armed = armed;
        _slog = ModLogger.For(LogVerb.Signature, () => Wielder.AnyDeployedMainHand(_mem, KikuIchimonjiId));
    }

    public void ResetBattle()
    {
        _armed.Clear();
        _prevTurnFlag.Clear();
        _wasActive = false;
    }

    public void Tick(bool onField)
    {
        if (!onField) return;   // band reads are unsafe off-field; prev-flag state simply freezes

        if (!_meta.TryGetValue(KikuIchimonjiId, out var m) || m.Signature is null || !m.Signature.Mushin) return;
        int tier = Tuning.TierOf(_kills, KikuIchimonjiId);

        Wielder.ResolveDeployedMainHandAll(_mem, KikuIchimonjiId, _wielders);

        bool active = tier >= m.Signature.AtTier && _wielders.Count > 0;
        if (ActivationEdge.Step(ref _wasActive, active))
        {
            _slog.Info(active
                ? "Kiku-ichimonji at tier three is wielded on the field; a full wait charges the wielder's next strike."
                : "Kiku-ichimonji's Mushin is no longer active.");
        }
        if (!active) return;

        foreach (var (entry, fp, nameId) in _wielders)
            TickWielder(entry, fp, nameId);
    }

    private void TickWielder(long entry, (int lvl, int br, int fa) fp, int nameId)
    {
        if (!_mem.Readable(entry + TurnFlagOffset, 1)) return;
        int cur = _mem.U8(entry + TurnFlagOffset);

        // LW-252 stage 5: keyed via WielderKeyedStore (nameId primary, fp fallback -- see its
        // class doc). null = an ambiguous nameId-0 tick for a fp two SEEDED twins already
        // registered under: SKIP this wielder entirely this tick (no prime, no decide, no
        // create) -- treat it exactly as "not located this tick".
        var flag = _prevTurnFlag.GetOrCreate(nameId, fp, () => new Box<int> { Value = -1 });
        if (flag == null) return;

        if (flag.Value == -1)
        {
            flag.Value = cur;   // first sight: prime only, never decide
            return;
        }
        int prev = flag.Value;
        flag.Value = cur;
        if (prev != 1 || cur != 0) return;   // only the FALLING edge decides

        bool moved = _mem.Readable(entry + MovedOffset, 1) && _mem.U8(entry + MovedOffset) != 0;
        bool acted = _mem.Readable(entry + ActedOffset, 1) && _mem.U8(entry + ActedOffset) != 0;

        // Same store, same (nameId, fp) key as _prevTurnFlag above -- both derive their key
        // through the identical resolution law, so they can never disagree on which twin this
        // tick's wielder is (the SAME helper instance rule Part C requires for the pair sharing
        // data across Mushin.cs and GrowthEngine.Mushin.cs).
        var arm = _armed.GetOrCreate(nameId, fp, () => new Box<int>());
        if (arm == null) return;

        if (MushinPolicy.ShouldConsume(turnEnded: true, acted))
        {
            bool wasArmed = arm.Value != 0;
            arm.Value = 0;
            ModLogger.Debug(LogVerb.Signature,
                $"Mushin falling edge (level {fp.lvl}, brave {fp.br}, faith {fp.fa}): moved {moved}, acted {acted}, verdict CONSUME{(wasArmed ? "" : " (nothing armed)")}.");
            if (wasArmed)
                ModLogger.Event(LogVerb.Signature,
                    $"The Kiku-ichimonji wielder's charged strike lands; Mushin's boost is spent (level {fp.lvl}, brave {fp.br}, faith {fp.fa}).");
        }
        else if (MushinPolicy.ShouldArm(turnEnded: true, moved, acted))
        {
            arm.Value = 1;   // idempotent: an already-armed wielder waiting again simply re-arms 1
            ModLogger.Debug(LogVerb.Signature,
                $"Mushin falling edge (level {fp.lvl}, brave {fp.br}, faith {fp.fa}): moved {moved}, acted {acted}, verdict ARM.");
            ModLogger.Event(LogVerb.Signature,
                $"The Kiku-ichimonji wielder stands perfectly still through its turn; the next strike is charged (level {fp.lvl}, brave {fp.br}, faith {fp.fa}).");
        }
        else
        {
            ModLogger.Debug(LogVerb.Signature,
                $"Mushin falling edge (level {fp.lvl}, brave {fp.br}, faith {fp.fa}): moved {moved}, acted {acted}, verdict NO-CHANGE (move-only; any armed charge survives).");
        }
    }
}
