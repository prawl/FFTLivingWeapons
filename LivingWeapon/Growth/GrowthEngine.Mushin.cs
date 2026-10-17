using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Kiku-ichimonji's stacking PA boost hold. For a Mushin weapon GrowthEngine.Routes gives the PA lane to this method
/// at every tier (as with HoldUltima), so one writer owns the byte. A below-tier weapon is written here at the plain
/// Tuning.Factor[tier] rate, because MushinPolicy.PaHeld with zero stacks equals Route's formula.
/// Stacks come from the dictionary shared with Mushin.cs (injected from Engine.cs). A full-wait turn banks one, up to
/// Tuning.MushinMaxStacks, cleared when the next own-turn attack lands. Each adds Tuning.MushinBonus.
/// Ownership matches HoldUltima/HoldAfterimage (capture natural, re-apply target, leave foreign values alone).
/// </summary>
internal sealed partial class GrowthEngine
{
    // PA addr -> (captured natural, last value we wrote as the ownership token, baked restart residue also recognized).
    private readonly Dictionary<long, (int natural, int lastTarget, int baked)> _mushin = new();

    /// <summary>True when this weapon's signature is Mushin; it owns the PA lane at every tier (Route declines it).</summary>
    internal static bool OwnsMushin(WeaponMeta m) => m.Signature is { Mushin: true };

    /// <summary>Holds the wielder's PA at MushinPolicy.PaHeld(natural, tier, effectiveStacks). Main hand only; guarded, fail-safe no-op.
    /// rosterNameId feeds the NaturalLedger. Internal for the ledger seam tests.</summary>
    internal void HoldMushin(long s, WeaponMeta m, int tier, int level, int brave, int faith, int rosterNameId = 0)
    {
        if (!OwnsMushin(m)) return;
        long addr = s + Offsets.CPa;

        // Shared with HoldUltima/HoldAfterimage via OwnershipHold.
        bool hadRecord = _mushin.TryGetValue(addr, out var rec0);
        var step = OwnershipHold.Step(_mem, _ledger, addr, StatLane.Pa, rosterNameId, level,
                                      hadRecord, rec0.lastTarget, rec0.natural, rec0.baked);
        if (step.Branch == OwnershipHold.Branch.Refused) return;

        var rec = step.Branch == OwnershipHold.Branch.Captured
            ? (natural: step.Natural, lastTarget: step.Cur, baked: step.Baked)
            : rec0;
        if (step.Branch == OwnershipHold.Branch.Captured && step.Baked > 0)
            ModLogger.Debug(LogVerb.Growth, $"mushin: restart residue corrected at capture (read {step.Baked}, natural {step.Natural})");

        var fp = (level, brave, faith);
        // Probe is read-only and resolves like Mushin.cs's writer; null (no stacks yet, or an ambiguous nameId-0 twin) reads as 0.
        int stacks = _mushinArmed.Probe(rosterNameId, fp)?.Value ?? 0;
        int effectiveStacks = MushinPolicy.EffectiveStacks(stacks, tier, m.Signature!.AtTier);
        int target = Clamp(MushinPolicy.PaHeld(rec.natural, tier, Tuning.Factor, effectiveStacks, Tuning.MushinBonus));

        if (step.Branch == OwnershipHold.Branch.Foreign)
        {
            _mushin[addr] = (rec.natural, rec.lastTarget, rec.baked);   // foreign value, leave the byte
            return;
        }

        if (effectiveStacks > 0 && target != rec.lastTarget)
        {
            ModLogger.EventWithTrace(LogVerb.Signature,
                $"{m.Name}'s Mushin charge holds Physical Attack boosted ({effectiveStacks} of {Tuning.MushinMaxStacks} stack(s), tier {tier}).",
                $"mushin PA held at {effectiveStacks} stack(s): {rec.natural} -> {target} (tier {tier})");
        }
        _ledger.RecordWrite(rosterNameId, StatLane.Pa, target);   // per evaluation
        if (step.Cur != target) _mem.W8(addr, (byte)target);
        _mushin[addr] = (rec.natural, target, rec.baked);
    }
}
