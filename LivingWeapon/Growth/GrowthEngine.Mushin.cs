using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The Mushin half of GrowthEngine: Kiku-ichimonji's STACKING PA boost hold. For a Mushin
/// weapon, GrowthEngine.Routes yields the PA lane entirely to this method (same idiom as
/// HoldUltima), so a single writer owns the byte at every tier. OwnsMushin is tier-independent,
/// exactly like OwnsPa, so a below-tier Kiku-ichimonji's PA growth is still written HERE
/// (transparently, at the plain Tuning.Factor[tier] rate: MushinPolicy.PaHeld's zero-stack case
/// is byte-identical to Route's own formula) rather than fighting Route's writer for the same
/// address.
///
/// Reads the stack-count dictionary SHARED with Mushin.cs (constructor-injected from Engine.cs):
/// each full-wait turn banks one stack (up to Tuning.MushinMaxStacks), cleared the instant the
/// wielder's next own-turn attack lands. Applies MushinPolicy.PaHeld: zero stacks equals
/// byte-identical to normal growth, each banked stack adds Tuning.MushinBonus for that one spent
/// hit (additively: N stacks add N x MushinBonus).
///
/// Ownership idiom identical to HoldUltima/HoldAfterimage: capture natural on first sight,
/// re-apply our target against the engine's per-turn normalize, leave a foreign value (a real
/// buff/debuff) untouched.
/// </summary>
internal sealed partial class GrowthEngine
{
    // PA addr -> (captured natural, the last value WE wrote: our ownership token; LW-90
    // baked = the restart residue a corrected capture read, also recognized).
    private readonly Dictionary<long, (int natural, int lastTarget, int baked)> _mushin = new();

    /// <summary>True when this weapon's signature is Mushin: it owns the wielder's PA lane at
    /// every tier (Route declines it), mirroring OwnsPa/OwnsSpeed.</summary>
    internal static bool OwnsMushin(WeaponMeta m) => m.Signature is { Mushin: true };

    /// <summary>Hold the Kiku-ichimonji wielder's PA at MushinPolicy.PaHeld(natural, tier,
    /// effectiveStacks). Main-hand only (the charge is commanded from the main hand, mirrors
    /// HoldUltima); guarded, fail-safe no-op. rosterNameId feeds the LW-90 NaturalLedger.
    /// Internal for the LW-90 seam tests (LocateIn precedent).</summary>
    internal void HoldMushin(long s, WeaponMeta m, int tier, int level, int brave, int faith, int rosterNameId = 0)
    {
        if (!OwnsMushin(m)) return;
        long addr = s + Offsets.CPa;

        // LW-149 stage A: shared with HoldUltima/HoldAfterimage via OwnershipHold (see its class doc).
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
        // LW-252 stage 5: Probe (read-only, never creates) via the SAME resolution law
        // Mushin.cs's writer side uses -- a null Probe (no stacks armed yet, OR an ambiguous
        // nameId-0 twin transient) degrades to 0 stacks, identical to the old dictionary miss.
        int stacks = _mushinArmed.Probe(rosterNameId, fp)?.Value ?? 0;
        int effectiveStacks = MushinPolicy.EffectiveStacks(stacks, tier, m.Signature!.AtTier);
        int target = Clamp(MushinPolicy.PaHeld(rec.natural, tier, Tuning.Factor, effectiveStacks, Tuning.MushinBonus));

        if (step.Branch == OwnershipHold.Branch.Foreign)
        {
            _mushin[addr] = (rec.natural, rec.lastTarget, rec.baked);   // foreign value (buff/debuff): leave the byte
            return;
        }

        if (effectiveStacks > 0 && target != rec.lastTarget)
        {
            ModLogger.EventWithTrace(LogVerb.Signature,
                $"{m.Name}'s Mushin charge holds Physical Attack boosted ({effectiveStacks} of {Tuning.MushinMaxStacks} stack(s), tier {tier}).",
                $"mushin PA held at {effectiveStacks} stack(s): {rec.natural} -> {target} (tier {tier})");
        }
        _ledger.RecordWrite(rosterNameId, StatLane.Pa, target);   // per evaluation (LW-90)
        if (step.Cur != target) _mem.W8(addr, (byte)target);
        _mushin[addr] = (rec.natural, target, rec.baked);
    }
}
