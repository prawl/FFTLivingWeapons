using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Kiku-ichimonji's Mushin signature. A full wait turn (no move, no act) arms one charge, and the
/// wielder's next own action spends it.
///
/// History (2026-07-09): rounds 1 to 4 tried other signals for the wait moment, then the enemy
/// scheduler CT cycle (Offsets.ACtSlam), then a latch-then-confirm on KillTracker's PlayerActSeq.
/// None read the wielder's own turn. Round 5 replaced them with the engine's per-unit turn flags.
///
/// Turn-flag mapping (tools/probes/mushin_wait_probe.py, scratchpad/psxflags_watch.log; PSX offsets
/// from FFHacktics, frame offset = PSX + 0x32, band offset = frame offset, Offsets.BandEntry (0x1C)):
///   TURN FLAG  PSX 0x186, frame +0x1B8, band +0x19C 1 while the move/act/wait menu is open.
///   MOVED      PSX 0x187, frame +0x1B9, band +0x19D. Set at the move, reset by the engine at the next turn open.
///   ACTED      PSX 0x188, frame +0x1BA, band +0x19E. Set at the action, same reset.
///   PSX 0x189, frame +0x1BB, band +0x19F is Ability Outcome (0x02 hit-by-ability, 0x01 turn-ended); unused here.
///
/// Per resolved main-hand wielder (Wielder.ResolveDeployedMainHandAll) the previous turn flag is
/// tracked per fingerprint, primed on first sight without deciding (safe mid-turn, since the engine
/// resets MOVED/ACTED at that turn's open). On the falling edge (1 to 0) MOVED and ACTED are read
/// and MushinPolicy decides. Acted consumes the charge, neither moved nor acted arms it, move-only
/// changes nothing. Each decision is logged at Debug. No other signals are used, and every read is
/// per-wielder-entry and guarded.
///
/// Offsets. The three band offsets were local consts until LW-55 stage 1 moved them to Offsets.cs
/// (ATurnFlag/AMoved/AActed); the earlier deferral was due to uncommitted LW-51 work (bf351db).
///
/// Accepted residuals (1) a reaction during the wielder's own menu window sets ACTED, turning a
/// wait into a no-arm or consume, which fails safe; (2) auto-battle and charmed turns are untested;
/// (3) twin/mirror frames carry frozen flags, but the twin-filtered Wielder.Locate prefers the real entry.
///
/// The armed store is shared with GrowthEngine (built once in Engine.cs). LW-252 stage 5 keyed it
/// via <see cref="WielderKeyedStore{TState}"/> (nameId primary, fp fallback) so two seeded
/// Kiku-ichimonji wielders sharing (lvl,br,fa) no longer cross-arm; a mixed pair still shares one
/// count. _prevTurnFlag uses its own private store.
/// </summary>
internal sealed partial class Mushin : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);

    private const int KikuIchimonjiId = 45;

    /// <summary>Aliases onto the Offsets.cs trio (LW-55 stage 1); see Offsets.cs for the PSX provenance.</summary>
    private const int TurnFlagOffset = Offsets.ATurnFlag;
    private const int MovedOffset = Offsets.AMoved;
    private const int ActedOffset = Offsets.AActed;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly WielderKeyedStore<Box<int>> _armed;
    private readonly List<(long entry, (int lvl, int br, int fa) fp, int nameId)> _wielders = new();

    /// <summary>Per-wielder previous TURN FLAG, boxed for WielderKeyedStore (LW-252 stage 5; private,
    /// GrowthEngine never reads it). Value -1 means not yet primed, so the first observed value is
    /// captured without deciding and a legitimate 0 stays distinguishable.</summary>
    private readonly WielderKeyedStore<Box<int>> _prevTurnFlag = new();

    private readonly ScopedLogger _slog;   // armed gate. A benched/below-tier Kiku must not narrate on console
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

        // LW-252 stage 5. Keyed via WielderKeyedStore (nameId primary, fp fallback). null means an
        // ambiguous nameId-0 tick for a fp two seeded twins already hold; skip this wielder this tick.
        var flag = _prevTurnFlag.GetOrCreate(nameId, fp, () => new Box<int> { Value = -1 });
        if (flag == null) return;

        if (flag.Value == -1)
        {
            flag.Value = cur;   // first sight. Prime only, never decide
            return;
        }
        int prev = flag.Value;
        flag.Value = cur;
        if (prev != 1 || cur != 0) return;   // only the FALLING edge decides

        bool moved = _mem.Readable(entry + MovedOffset, 1) && _mem.U8(entry + MovedOffset) != 0;
        bool acted = _mem.Readable(entry + ActedOffset, 1) && _mem.U8(entry + ActedOffset) != 0;

        // Same (nameId, fp) key as _prevTurnFlag, so both resolve to the same twin; GrowthEngine.Mushin.cs shares this store.
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
            arm.Value = 1;   // idempotent. An already-armed wielder waiting again simply re-arms 1
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
