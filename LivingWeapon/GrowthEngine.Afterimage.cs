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
    // speed addr -> (captured natural, ramp state, the last value WE wrote -- our ownership token).
    private readonly Dictionary<long, (int natural, AfterimageState st, int lastTarget)> _afterimage = new();

    /// <summary>True when this weapon's signature is Afterimage -- it owns the wielder's Speed at
    /// every tier, so Route declines the Speed lane for it.</summary>
    internal static bool OwnsSpeed(WeaponMeta m) => m.Signature is { Afterimage: true };

    /// <summary>Hold Swiftedge's Speed: tier growth at every tier, plus the +3 Afterimage ramp on
    /// top. Main-hand only (the gift commands from the main hand), guarded, fail-safe no-op.</summary>
    private void HoldAfterimage(long s, WeaponMeta m, int tier, int level, int brave, int faith)
    {
        if (!OwnsSpeed(m)) return;
        long addr = s + Offsets.CSpeed;
        if (!_mem.Writable(addr, 1)) return;

        if (!_afterimage.TryGetValue(addr, out var rec))
        {
            int cur0 = _mem.U8(addr);
            if (cur0 < StatMin || cur0 > StatSaneHi) return;     // wait for a sane natural reading
            rec = (cur0, AfterimageState.Empty, cur0);           // first sight: own the byte at natural
        }

        AfterimageState next;
        if (AfterimagePolicy.IsActive(m.Signature, tier))
        {
            int turns = _turns.Turns(level, brave, faith);
            int hp = ReadHp(level, brave, faith).hp;             // 0 if no band match -> policy treats as unreadable
            next = AfterimagePolicy.Step(rec.st, turns, hp, Tuning.AfterimageSpeedCap);
        }
        else next = AfterimageState.Empty;                       // below +3: ramp dormant, growth only

        int growth = Clamp((int)Math.Round(rec.natural * (1 + Tuning.SpeedFactor[tier])));
        int target = Clamp(growth + AfterimagePolicy.SpeedBonus(next, Tuning.AfterimageSpeedPerTurn));

        int cur = _mem.U8(addr);
        if (cur == rec.lastTarget || cur == rec.natural)         // we own it (or the engine just normalized)
        {
            if (next.Stacks != rec.st.Stacks)                    // log each ramp STEP (and the hit-reset back to 0)
                Log.Info($"afterimage: {m.Name} wielder Speed {rec.natural} -> {target} (stacks {next.Stacks}/{Tuning.AfterimageSpeedCap})");
            if (cur != target) _mem.W8(addr, (byte)target);
            _afterimage[addr] = (rec.natural, next, target);
        }
        else _afterimage[addr] = (rec.natural, next, rec.lastTarget);   // foreign value: advance state, leave byte
    }
}
