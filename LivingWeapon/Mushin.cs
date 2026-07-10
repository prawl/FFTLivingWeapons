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
/// OFFSETS.CS FORBIDDEN THIS ROUND (it carries uncommitted LW-51 work): the three band-relative
/// offsets below are LOCAL consts; promoting them to Offsets.cs is deferred to a later
/// commit-staging round.
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
/// The armed dictionary lives SHARED with GrowthEngine (constructed once in Engine.cs, passed to
/// both), keyed by wielder fingerprint (lvl,br,fa) like every other per-wielder signature hold,
/// so two Kiku-ichimonji wielders never cross-arm or cross-consume each other.
/// </summary>
internal sealed partial class Mushin : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);

    private const int KikuIchimonjiId = 45;

    /// <summary>Band-relative turn-flag offsets (round 5; LOCAL consts, see this class's own doc
    /// comment for why Offsets.cs is forbidden this round). frame +0x1B8/+0x1B9/+0x1BA minus
    /// Offsets.BandEntry (0x1C) = band +0x19C/+0x19D/+0x19E.</summary>
    private const int TurnFlagOffset = 0x19C;
    private const int MovedOffset = 0x19D;
    private const int ActedOffset = 0x19E;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly Dictionary<(int lvl, int br, int fa), int> _armed;
    private readonly List<(long entry, (int lvl, int br, int fa) fp)> _wielders = new();

    /// <summary>Per-wielder previous TURN FLAG value. Absence means "not yet primed": the very
    /// next observed value is captured WITHOUT deciding (a mid-turn prime is safe, the flags reset
    /// at that turn's own open, so priming mid-window can never fabricate a phantom edge).</summary>
    private readonly Dictionary<(int lvl, int br, int fa), int> _prevTurnFlag = new();

    private readonly ScopedLogger _slog;   // armed gate: a benched/below-tier Kiku must not narrate on console
    private bool _wasActive;

    public Mushin(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
                  Dictionary<(int lvl, int br, int fa), int> armed, IGameMemory? mem = null)
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
        if (active != _wasActive)
        {
            _wasActive = active;
            _slog.Info(active
                ? "Kiku-ichimonji at tier three is wielded on the field; a full wait charges the wielder's next strike."
                : "Kiku-ichimonji's Mushin is no longer active.");
        }
        if (!active) return;

        foreach (var (entry, fp) in _wielders)
            TickWielder(entry, fp);
    }

    private void TickWielder(long entry, (int lvl, int br, int fa) fp)
    {
        if (!_mem.Readable(entry + TurnFlagOffset, 1)) return;
        int cur = _mem.U8(entry + TurnFlagOffset);

        if (!_prevTurnFlag.TryGetValue(fp, out int prev))
        {
            _prevTurnFlag[fp] = cur;   // first sight: prime only, never decide
            return;
        }
        _prevTurnFlag[fp] = cur;
        if (prev != 1 || cur != 0) return;   // only the FALLING edge decides

        bool moved = _mem.Readable(entry + MovedOffset, 1) && _mem.U8(entry + MovedOffset) != 0;
        bool acted = _mem.Readable(entry + ActedOffset, 1) && _mem.U8(entry + ActedOffset) != 0;

        if (MushinPolicy.ShouldConsume(turnEnded: true, acted))
        {
            bool wasArmed = _armed.TryGetValue(fp, out int s) && s != 0;
            _armed[fp] = 0;
            ModLogger.Debug(LogVerb.Signature,
                $"Mushin falling edge (level {fp.lvl}, brave {fp.br}, faith {fp.fa}): moved {moved}, acted {acted}, verdict CONSUME{(wasArmed ? "" : " (nothing armed)")}.");
            if (wasArmed)
                ModLogger.Event(LogVerb.Signature,
                    $"The Kiku-ichimonji wielder's charged strike lands; Mushin's boost is spent (level {fp.lvl}, brave {fp.br}, faith {fp.fa}).");
        }
        else if (MushinPolicy.ShouldArm(turnEnded: true, moved, acted))
        {
            _armed[fp] = 1;   // idempotent: an already-armed wielder waiting again simply re-arms 1
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
