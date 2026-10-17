using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Mount-gated flat-stat partial of GrowthEngine (Cavalier's Charge, Speed +3 while mounted), split
/// out like the Afterimage/Ultima/Mushin partials. Captures natural and holds it,
/// consulting the shared NaturalLedger at capture. This hold reverts mid-battle when its window closes,
/// so it keeps a post-revert sentinel. If the capture was corrected (the battle opened on the mod's
/// restart residue) and normalize restores that residue after the revert, the sentinel rewrites the
/// true natural. It parallels Iai's post-release corrective hold and rests on the same unverified
/// assumption about normalize.
/// Known gap, in the backlog. Correction only happens at an active capture, so a restarted battle
/// whose rider opens dismounted keeps the residue until the player mounts (bounded, does not compound).
/// A clean remount capture drops the sentinel, assuming a clean read means the baseline was natural.
/// </summary>
internal sealed partial class GrowthEngine
{
    // Post-revert sentinels, stat addr -> (corrected natural, baked residue). Set when a corrected
    // capture's window closes; cleared per battle.
    private readonly Dictionary<long, (int nat, int baked)> _timedReverted = new();

    // Addresses that already logged an unexpected window-closed read this battle (not boosted,
    // baked or natural), so the Debug line fires once instead of flooding on the growth cadence (the
    // census-evict flood shape). Cleared when the revert lands, on a fresh capture, and per battle.
    private readonly HashSet<long> _timedUnexpectedLogged = new();

    /// <summary>Holds the mount-gated Speed bonus while riding, then reverts on dismount. Captures natural
    /// on first sight, re-applies natural+bonus against the per-turn normalize, and restores natural once
    /// the window closes. Writes are guarded and only ever natural or natural+bonus. Speed is the only wired stat.
    /// The capture consults the NaturalLedger (rosterNameId keys it, 0 = degraded bypass lane,
    /// level is the level key). A baked residue is recognized at re-apply, at revert, and after revert via the sentinel.
    /// A window-closed read matching none of boosted, baked or natural keeps the record and retries
    /// next tick (it used to drop it). ResetBattle is the backstop.
    /// Capture, boost, re-apply and revert log at Debug, only when they write.
    /// Excluded from the OwnershipHold core shared by HoldUltima/HoldMushin/HoldAfterimage,
    /// because it reverts, keeps _timedReverted past the record, and latches unexpected reads
    /// (_timedUnexpectedLogged), none of which fit the core's capture-or-check branches.</summary>
    internal void HoldTimedStat(long s, WeaponSignature sig, int tier, int rosterNameId = 0,
                                int level = 0)
    {
        if (tier < sig.AtTier || sig.StatBonus == 0 || sig.Stat != "Speed") return;
        long addr = s + Offsets.CSpeed;
        if (!_mem.Writable(addr, 1)) return;
        int cur = _mem.U8(addr);
        bool active = (_mem.U8(s + Offsets.CMount) & Offsets.CMountRidingBit) != 0;   // riding a chocobo
        if (_timedNatural.TryGetValue(addr, out var rec))
        {
            int boosted = Clamp(rec.nat + sig.StatBonus);
            if (active)
            {
                _ledger.RecordWrite(rosterNameId, StatLane.Speed, boosted);   // per evaluation
                if (cur == rec.nat || (rec.baked > 0 && cur == rec.baked))
                {
                    _mem.W8(addr, (byte)boosted);   // re-apply after a normalize (possibly to the baked residue)
                    ModLogger.Debug(LogVerb.Growth, $"timed-stat: re-apply wrote {boosted} (read {cur} before write)");
                }
            }
            else
            {
                bool reverted = cur == boosted || (rec.baked > 0 && cur == rec.baked);
                bool alreadyNatural = !reverted && cur == rec.nat;
                if (reverted)
                {
                    _mem.W8(addr, (byte)rec.nat);   // window over -> revert our boost (or the residue)
                    ModLogger.Debug(LogVerb.Growth, $"timed-stat: revert wrote {rec.nat} (read {cur} before write)");
                }
                if (reverted || alreadyNatural)
                {
                    // The revert landed or the byte already reads natural. Both are done, not the lost-Speed retry case.
                    if (rec.baked > 0) _timedReverted[addr] = rec;   // keep watching for the residue
                    _timedNatural.Remove(addr);
                    _timedUnexpectedLogged.Remove(addr);   // a fresh recurrence later logs again
                }
                else if (_timedUnexpectedLogged.Add(addr))   // unexpected reading, keep the record
                {
                    string expected = rec.baked > 0
                        ? $"boosted {boosted}, baked residue {rec.baked}, or natural {rec.nat}"
                        : $"boosted {boosted} or natural {rec.nat}";
                    ModLogger.Debug(LogVerb.Growth,
                        $"timed-stat: window closed but read {cur}, expected {expected}; keeping the record to retry the revert");
                }
            }
        }
        else if (active && cur >= StatMin && cur <= StatSaneHi)   // first sight while active. Capture + apply
        {
            int nat = _ledger.FilterCapture(rosterNameId, StatLane.Speed, cur, level, out int baked);
            ModLogger.Debug(LogVerb.Growth, $"timed-stat: capture natural {nat} (read {cur}, level {level}, rosterNameId {rosterNameId})");
            if (baked > 0)
                ModLogger.Debug(LogVerb.Growth, $"timed-stat: restart residue corrected at capture (read {baked}, natural {nat})");
            _timedReverted.Remove(addr);   // a fresh capture supersedes any prior sentinel
            _timedUnexpectedLogged.Remove(addr);   // a fresh capture supersedes any prior unexpected-reading latch
            _timedNatural.Add(addr, (nat, baked));
            int boosted = Clamp(nat + sig.StatBonus);
            _ledger.RecordWrite(rosterNameId, StatLane.Speed, boosted);
            _mem.W8(addr, (byte)boosted);
            ModLogger.Debug(LogVerb.Growth, $"timed-stat: boost wrote {boosted} (natural {nat} + bonus {sig.StatBonus})");
        }
        else if (_timedReverted.TryGetValue(addr, out var rev) && cur == rev.baked)
        {
            // The engine normalized the residue back after our revert.
            _mem.W8(addr, (byte)rev.nat);
            ModLogger.Debug(LogVerb.Growth, $"timed-stat: restart residue re-corrected post-revert ({rev.baked} -> {rev.nat})");
        }
    }
}
