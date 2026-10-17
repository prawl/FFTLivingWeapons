using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Swiftedge's Speed system. For an afterimage weapon <see cref="GrowthEngine.Routes"/> gives the Speed lane to this
/// method, so one writer owns the byte. It holds per-tier growth at every tier and, once +3 is earned, adds
/// <see cref="Tuning.AfterimageSpeedPerTurn"/> per completed wielder turn (capped at <see cref="Tuning.AfterimageSpeedCap"/>),
/// resetting to zero when the wielder takes damage. Swiftedge damage is Speed x WP (formula 99).
/// Ownership follows Hold / HoldTimedStat. Capture natural, re-apply our target each turn, leave foreign values alone.
/// Writes are Mem.Writable-guarded and natural-derived, so a wrong guess lasts at most one battle.
/// </summary>
internal sealed partial class GrowthEngine
{
    // speed addr -> (natural, ramp state, last value we wrote as the ownership token, baked restart residue)
    private readonly Dictionary<long, (int natural, AfterimageState st, int lastTarget, int baked)> _afterimage = new();

    /// <summary>True when the weapon's signature is Afterimage. It owns Speed at every tier, so Route declines that lane.</summary>
    internal static bool OwnsSpeed(WeaponMeta m) => m.Signature is { Afterimage: true };

    /// <summary>Hold Swiftedge's Speed. Tier growth plus the +3 Afterimage ramp. Main-hand only, guarded, fail-safe.
    /// rosterNameId feeds ReadHp's locate and the NaturalLedger. Internal for the ledger seam tests.</summary>
    internal void HoldAfterimage(long s, WeaponMeta m, int tier, int level, int brave, int faith, int rosterNameId)
    {
        if (!OwnsSpeed(m)) return;
        long addr = s + Offsets.CSpeed;

        // Shared via OwnershipHold. Afterimage's record advances on the FOREIGN branch too,
        // so the core reports the branch and this lane merges `next` itself.
        bool hadRecord = _afterimage.TryGetValue(addr, out var rec0);
        var step = OwnershipHold.Step(_mem, _ledger, addr, StatLane.Speed, rosterNameId, level,
                                      hadRecord, rec0.lastTarget, rec0.natural, rec0.baked);
        if (step.Branch == OwnershipHold.Branch.Refused) return;

        var rec = step.Branch == OwnershipHold.Branch.Captured
            ? (natural: step.Natural, st: AfterimageState.Empty, lastTarget: step.Cur, baked: step.Baked)
            : rec0;
        if (step.Branch == OwnershipHold.Branch.Captured && step.Baked > 0)
            ModLogger.Debug(LogVerb.Growth, $"afterimage: restart residue corrected at capture (read {step.Baked}, natural {step.Natural})");

        AfterimageState next;
        if (AfterimagePolicy.IsActive(m.Signature, tier))
        {
            // Key by the same rosterNameId TurnTracker credits under. A mismatch would freeze the ramp,
            // so never use the bare fp overload.
            int turns = _turns.Turns(rosterNameId, level, brave, faith);
            int hp = ReadHp(_mem, level, brave, faith, rosterNameId).hp;   // 0 if no band match -> policy treats as unreadable
            next = AfterimagePolicy.Step(rec.st, turns, hp, Tuning.AfterimageSpeedCap);
        }
        else next = AfterimageState.Empty;                       // below +3. Ramp dormant, growth only

        int growth = Clamp((int)Math.Round(rec.natural * (1 + Tuning.SpeedFactor[tier])));
        int target = Clamp(growth + AfterimagePolicy.SpeedBonus(next, Tuning.AfterimageSpeedPerTurn));

        if (step.Branch == OwnershipHold.Branch.Foreign)
        {
            _afterimage[addr] = (rec.natural, next, rec.lastTarget, rec.baked);   // foreign value. Advance state, leave byte
            return;
        }

        if (next.Stacks != rec.st.Stacks)                    // log each ramp STEP (and the hit-reset back to 0)
            ModLogger.Debug(LogVerb.Signature, $"stepped Afterimage: {m.Name} wielder Speed {rec.natural} -> {target} (stacks {next.Stacks} of {Tuning.AfterimageSpeedCap})");
        _ledger.RecordWrite(rosterNameId, StatLane.Speed, target);   // per evaluation
        if (step.Cur != target) _mem.W8(addr, (byte)target);
        _afterimage[addr] = (rec.natural, next, target, rec.baked);
    }
}
