using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The Afterimage half of GrowthEngine: Swiftedge's COMPLETE Speed system. For an afterimage
/// weapon, <see cref="GrowthEngine.Route"/> yields the Speed lane entirely to this method, so a
/// single writer owns the byte (no fight with the multiplicative growth hold). It holds Speed at
/// the normal per-tier growth at EVERY tier, plus -- once +3 is earned -- a ramp that adds
/// <see cref="Tuning.AfterimageSpeedPerTurn"/> per completed wielder turn (capped at
/// <see cref="Tuning.AfterimageSpeedCap"/>) and resets to zero the moment the wielder takes damage.
/// Swiftedge's damage is Speed x WP (formula 99), so the ramp directly accelerates its damage.
///
/// The ownership idiom is the proven one (Hold / HoldTimedStat): capture natural on first sight,
/// re-apply our target against the engine's per-turn normalize, and leave a FOREIGN value (a real
/// buff/debuff) untouched. Every write is VirtualQuery-guarded (Mem.Writable) and only ever writes
/// a natural-derived value, so a wrong guess can at worst linger one battle (the fresh per-battle
/// combat struct clears it).
/// </summary>
internal sealed partial class GrowthEngine
{
    // speed addr -> (captured natural, ramp state, the last value WE wrote -- our ownership
    // token; LW-90 baked = the restart residue a corrected capture read, also recognized).
    private readonly Dictionary<long, (int natural, AfterimageState st, int lastTarget, int baked)> _afterimage = new();

    /// <summary>True when this weapon's signature is Afterimage -- it owns the wielder's Speed at
    /// every tier, so Route declines the Speed lane for it.</summary>
    internal static bool OwnsSpeed(WeaponMeta m) => m.Signature is { Afterimage: true };

    /// <summary>Hold Swiftedge's Speed: tier growth at every tier, plus the +3 Afterimage ramp on
    /// top. Main-hand only (the gift commands from the main hand), guarded, fail-safe no-op.
    /// rosterNameId (D7) threads through to ReadHp's two-tier-with-veto locate and the LW-90
    /// NaturalLedger. Internal for the LW-90 seam tests (LocateIn precedent).</summary>
    internal void HoldAfterimage(long s, WeaponMeta m, int tier, int level, int brave, int faith, int rosterNameId)
    {
        if (!OwnsSpeed(m)) return;
        long addr = s + Offsets.CSpeed;

        // LW-149 stage A: shared with HoldUltima/HoldMushin via OwnershipHold (see its class doc).
        // Afterimage is the one lane whose record (AfterimageState) advances on the FOREIGN
        // branch too, which is exactly why the core reports the branch rather than owning the
        // dictionary -- only this lane knows how to merge `next` into its own record shape.
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
            int turns = _turns.Turns(level, brave, faith);
            int hp = ReadHp(_mem, level, brave, faith, rosterNameId).hp;   // 0 if no band match -> policy treats as unreadable
            next = AfterimagePolicy.Step(rec.st, turns, hp, Tuning.AfterimageSpeedCap);
        }
        else next = AfterimageState.Empty;                       // below +3: ramp dormant, growth only

        int growth = Clamp((int)Math.Round(rec.natural * (1 + Tuning.SpeedFactor[tier])));
        int target = Clamp(growth + AfterimagePolicy.SpeedBonus(next, Tuning.AfterimageSpeedPerTurn));

        if (step.Branch == OwnershipHold.Branch.Foreign)
        {
            _afterimage[addr] = (rec.natural, next, rec.lastTarget, rec.baked);   // foreign value: advance state, leave byte
            return;
        }

        if (next.Stacks != rec.st.Stacks)                    // log each ramp STEP (and the hit-reset back to 0)
            ModLogger.Debug(LogVerb.Signature, $"stepped Afterimage: {m.Name} wielder Speed {rec.natural} -> {target} (stacks {next.Stacks} of {Tuning.AfterimageSpeedCap})");
        _ledger.RecordWrite(rosterNameId, StatLane.Speed, target);   // per evaluation (LW-90)
        if (step.Cur != target) _mem.W8(addr, (byte)target);
        _afterimage[addr] = (rec.natural, next, target, rec.baked);
    }
}
