using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The Mushin half of GrowthEngine: Kiku-ichimonji's STACKING PA boost hold. For a Mushin
/// weapon, GrowthEngine.Route yields the PA lane entirely to this method (same idiom as
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
        if (!_mem.Writable(addr, 1)) return;

        if (!_mushin.TryGetValue(addr, out var rec))
        {
            int cur0 = _mem.U8(addr);
            if (cur0 < StatMin || cur0 > StatSaneHi) return;   // wait for a sane natural reading
            // LW-90: see through the mod's own restart residue; lastTarget seeds to the byte
            // as-is either way (residue or natural, both are re-owned on the first check).
            int nat0 = _ledger.FilterCapture(rosterNameId, StatLane.Pa, cur0, level, out int baked0);
            if (baked0 > 0)
                ModLogger.Debug(LogVerb.Growth, $"mushin: restart residue corrected at capture (read {baked0}, natural {nat0})");
            rec = (nat0, cur0, baked0);
        }

        var fp = (level, brave, faith);
        int stacks = _mushinArmed.TryGetValue(fp, out int s0) ? s0 : 0;
        int effectiveStacks = MushinPolicy.EffectiveStacks(stacks, tier, m.Signature!.AtTier);
        int target = Clamp(MushinPolicy.PaHeld(rec.natural, tier, Tuning.Factor, effectiveStacks, Tuning.MushinBonus));

        int cur = _mem.U8(addr);
        if (cur == rec.lastTarget || cur == rec.natural
            || (rec.baked > 0 && cur == rec.baked))   // we own it (or the engine normalized -- possibly to the baked residue)
        {
            if (effectiveStacks > 0 && target != rec.lastTarget)
            {
                ModLogger.EventWithTrace(LogVerb.Signature,
                    $"{m.Name}'s Mushin charge holds Physical Attack boosted ({effectiveStacks} of {Tuning.MushinMaxStacks} stack(s), tier {tier}).",
                    $"mushin PA held at {effectiveStacks} stack(s): {rec.natural} -> {target} (tier {tier})");
            }
            _ledger.RecordWrite(rosterNameId, StatLane.Pa, target);   // per evaluation (LW-90)
            if (cur != target) _mem.W8(addr, (byte)target);
            _mushin[addr] = (rec.natural, target, rec.baked);
        }
        else _mushin[addr] = (rec.natural, rec.lastTarget, rec.baked);   // foreign value (buff/debuff): leave the byte
    }
}
