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
        if (!_mem.Writable(addr, 1)) return;

        if (!_afterimage.TryGetValue(addr, out var rec))
        {
            int cur0 = _mem.U8(addr);
            if (cur0 < StatMin || cur0 > StatSaneHi) return;     // wait for a sane natural reading
            // LW-90: see through the mod's own restart residue; lastTarget seeds to the byte
            // as-is either way (residue or natural, both are re-owned on the first check).
            int nat0 = _ledger.FilterCapture(rosterNameId, StatLane.Speed, cur0, level, out int baked0);
            if (baked0 > 0)
                ModLogger.Debug(LogVerb.Growth, $"afterimage: restart residue corrected at capture (read {baked0}, natural {nat0})");
            rec = (nat0, AfterimageState.Empty, cur0, baked0);
        }

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

        int cur = _mem.U8(addr);
        if (cur == rec.lastTarget || cur == rec.natural
            || (rec.baked > 0 && cur == rec.baked))              // we own it (or the engine normalized -- possibly to the baked residue)
        {
            if (next.Stacks != rec.st.Stacks)                    // log each ramp STEP (and the hit-reset back to 0)
                ModLogger.Debug(LogVerb.Signature, $"stepped Afterimage: {m.Name} wielder Speed {rec.natural} -> {target} (stacks {next.Stacks} of {Tuning.AfterimageSpeedCap})");
            _ledger.RecordWrite(rosterNameId, StatLane.Speed, target);   // per evaluation (LW-90)
            if (cur != target) _mem.W8(addr, (byte)target);
            _afterimage[addr] = (rec.natural, next, target, rec.baked);
        }
        else _afterimage[addr] = (rec.natural, next, rec.lastTarget, rec.baked);   // foreign value: advance state, leave byte
    }
}
