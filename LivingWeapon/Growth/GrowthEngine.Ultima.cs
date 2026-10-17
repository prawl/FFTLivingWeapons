using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Ultima half of GrowthEngine. Holds the Materia Blade wielder's PA at
/// round(naturalPA × UltimaMul[tier][hpBand]), so <see cref="GrowthEngine.Routes"/> gives it the PA lane.
/// Ownership follows HoldAfterimage: capture natural, re-apply our target after the per-turn normalize,
/// leave foreign values alone. Held PA can sit below natural, so a PA-down debuff equal to lastTarget
/// or natural is mistaken for ours and overwritten. Accepted, since writes are guarded and natural-derived,
/// reset next battle, and player PA debuffs are rare in FFT:IC. The LW-90 NaturalLedger is consulted only at capture.
/// </summary>
internal sealed partial class GrowthEngine
{
    // PA addr -> (captured natural, last value we wrote, LW-90 baked restart residue).
    private readonly Dictionary<long, (int natural, int lastTarget, int baked)> _ultima = new();

    /// <summary>True when this weapon's signature is Ultima; Route then declines the PA lane.</summary>
    internal static bool OwnsPa(WeaponMeta m) => m.Signature is { Ultima: true };

    /// <summary>Hold PA at round(naturalPA × UltimaMul[tier][hpBand]). No tier gate, tier only picks the curve.
    /// Main hand only. rosterNameId feeds ReadHp's locate and the LW-90 NaturalLedger. Internal for the LW-90 seam tests.</summary>
    internal void HoldUltima(long s, WeaponMeta m, int tier, int level, int brave, int faith, int rosterNameId)
    {
        if (!OwnsPa(m)) return;
        long addr = s + Offsets.CPa;

        // LW-149 stage A: capture, ledger consult and the ownership check live in OwnershipHold.
        bool hadRecord = _ultima.TryGetValue(addr, out var rec0);
        var step = OwnershipHold.Step(_mem, _ledger, addr, StatLane.Pa, rosterNameId, level,
                                      hadRecord, rec0.lastTarget, rec0.natural, rec0.baked);
        if (step.Branch == OwnershipHold.Branch.Refused) return;

        var rec = step.Branch == OwnershipHold.Branch.Captured
            ? (natural: step.Natural, lastTarget: step.Cur, baked: step.Baked)
            : rec0;
        if (step.Branch == OwnershipHold.Branch.Captured && step.Baked > 0)
            ModLogger.Debug(LogVerb.Growth, $"ultima: restart residue corrected at capture (read {step.Baked}, natural {step.Natural})");

        var (hp, maxHp) = ReadHp(_mem, level, brave, faith, rosterNameId);   // (0,0) when no band match -> policy leaves PA natural
        int target = Clamp(UltimaPolicy.PaHeld(rec.natural, hp, maxHp, tier, Tuning.UltimaMul));

        if (step.Branch == OwnershipHold.Branch.Foreign)
        {
            _ultima[addr] = (rec.natural, rec.lastTarget, rec.baked);   // foreign value (buff/debuff): leave the byte
            return;
        }

        // Captured or Owned: we hold the byte.
        // Log when the held multiplier moves (HP band crossed or tier earned), not every tick.
        if (target != rec.lastTarget)
        {
            int band = UltimaPolicy.Band(hp, maxHp);
            int pct = band < 0 ? 100 : Tuning.UltimaMul[tier][band];
            int hpPct = maxHp > 0 ? (int)(hp * 100L / maxHp) : 0;
            ModLogger.EventWithTrace(LogVerb.Signature,
                $"{m.Name} at {hpPct} percent health now deals {pct} percent damage (tier {tier}).",
                $"ultima Physical Attack hold {rec.natural} -> {target} (tier {tier})");
        }
        _ledger.RecordWrite(rosterNameId, StatLane.Pa, target);   // per evaluation
        if (step.Cur != target) _mem.W8(addr, (byte)target);
        _ultima[addr] = (rec.natural, target, rec.baked);
    }
}
