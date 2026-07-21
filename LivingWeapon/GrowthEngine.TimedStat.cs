using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The timed/mounted flat-stat half of GrowthEngine (Galewind's first-N-turns Speed +3,
/// Cavalier's Charge's while-mounted Speed +3), split to its own partial per the LW-90 plan
/// (the seam mirroring the Afterimage/Ultima/Mushin partials). Capture-natural-then-hold with
/// the shared NaturalLedger consulted at capture, and -- unique to this hold, which REVERTS
/// mid-battle when its window closes -- a post-revert corrective sentinel: if the capture was
/// corrected (the battle opened on the mod's own restart residue) and the engine's normalize
/// later restores that residue AFTER the revert, the sentinel re-writes the true natural for
/// the rest of the battle (the Iai post-release corrective hold's sibling; same unverified
/// normalize premise, same discriminator role).
///
/// Known residual (backlog LW-100): correction happens only at an ACTIVE capture, so a
/// restarted battle whose rider opens DISMOUNTED cannot see through the baked residue until
/// the player mounts (bounded, non-compounding, the pre-fix one-battle failure shape); and a
/// clean remount capture deliberately drops the corrective sentinel (a clean read implies
/// the baseline was already natural, an inference riding the same unverified premise).
/// </summary>
internal sealed partial class GrowthEngine
{
    // Post-revert corrective sentinels: stat addr -> (corrected natural, baked residue).
    // Populated only by a corrected-capture hold whose window closed; cleared per battle.
    private readonly Dictionary<long, (int nat, int baked)> _timedReverted = new();

    /// <summary>Hold a TIMED flat stat bonus (Galewind's Speed +3 for the wielder's first ForTurns
    /// turns), then revert. Captures natural on first sight while active and re-applies natural+bonus
    /// against the per-turn normalize; once the window passes, restores the captured natural and stops
    /// tracking. Only ever writes natural or natural+bonus (both guarded) -- worst case a buff lingers
    /// a turn (and resets next battle), never a corrupt value. Speed is the only wired stat today.
    /// LW-90: the capture consults the NaturalLedger (rosterNameId keys it, 0 = the degraded bypass
    /// lane; level is the ledger's level key); a corrected record's baked residue is recognized at
    /// re-apply, revert, AND after the revert via the corrective sentinel above.</summary>
    internal void HoldTimedStat(long s, WeaponSignature sig, int tier, int turns, int rosterNameId = 0,
                                int level = 0)
    {
        if (tier < sig.AtTier || sig.StatBonus == 0 || sig.Stat != "Speed") return;
        long addr = s + Offsets.CSpeed;
        if (!_mem.Writable(addr, 1)) return;
        int cur = _mem.U8(addr);
        bool active = sig.Mounted
            ? (_mem.U8(s + Offsets.CMount) & Offsets.CMountRidingBit) != 0   // riding a chocobo
            : turns < sig.ForTurns;
        if (_timedNatural.TryGetValue(addr, out var rec))
        {
            int boosted = Clamp(rec.nat + sig.StatBonus);
            if (active)
            {
                _ledger.RecordWrite(rosterNameId, StatLane.Speed, boosted);   // per evaluation (LW-90)
                if (cur == rec.nat || (rec.baked > 0 && cur == rec.baked))
                    _mem.W8(addr, (byte)boosted);   // re-apply after a normalize (possibly to the baked residue)
            }
            else
            {
                if (cur == boosted || (rec.baked > 0 && cur == rec.baked))
                    _mem.W8(addr, (byte)rec.nat);   // window over -> revert our boost (or the residue)
                if (rec.baked > 0) _timedReverted[addr] = rec;   // keep watching for the residue
                _timedNatural.Remove(addr);
            }
        }
        else if (active && cur >= StatMin && cur <= StatSaneHi)   // first sight while active: capture + apply
        {
            int nat = _ledger.FilterCapture(rosterNameId, StatLane.Speed, cur, level, out int baked);
            if (baked > 0)
                ModLogger.Debug(LogVerb.Growth, $"timed-stat: restart residue corrected at capture (read {baked}, natural {nat})");
            _timedReverted.Remove(addr);   // a fresh capture supersedes any prior sentinel
            _timedNatural.Add(addr, (nat, baked));
            int boosted = Clamp(nat + sig.StatBonus);
            _ledger.RecordWrite(rosterNameId, StatLane.Speed, boosted);
            _mem.W8(addr, (byte)boosted);
        }
        else if (_timedReverted.TryGetValue(addr, out var rev) && cur == rev.baked)
        {
            // Post-revert corrective: the engine normalized the residue back after our revert.
            _mem.W8(addr, (byte)rev.nat);
            ModLogger.Debug(LogVerb.Growth, $"timed-stat: restart residue re-corrected post-revert ({rev.baked} -> {rev.nat})");
        }
    }
}
