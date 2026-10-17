using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The signature half of GrowthEngine. The ability or stat a weapon grants at its kill tier, held
/// on the combat struct beside the stat growth. Two kinds:
///   - support bit (always-on or HP-gated), OR-ed in at +0x98 (Signatures.ResolveSupport/ConditionMet);
///   - timed flat stat (first-N-turns), a Speed bonus written then reverted, gated by TurnTracker.
/// Writes are guarded (Mem.Writable) and only OR-set a bit or write a value derived from the
/// captured natural, so a wrong guess lingers at most one battle (the next combat struct is fresh).
/// </summary>
internal sealed partial class GrowthEngine
{
    // Grants announced this battle (weapon id), so the read-back log fires once per arm, not every tick.
    private readonly HashSet<int> _grantLogged = new();

    // Support bits held this battle, so a mid-battle unequip (Rend/Steal mutate the roster live)
    // releases the grant instead of letting it linger to battle end (roster slot, weapon) ->
    // (struct base, bit, ability id, the latch-time brave/faith for the same-unit check).
    private readonly Dictionary<(int slot, int weapon), (long s, int off, byte mask, int abilityId, int brave, int faith)>
        _heldSupports = new();

    /// <summary>Hold this weapon's signature support passive on the combat struct, once its
    /// <summary>Hold this weapon's signature support passive once its kill tier is earned. OR the
    /// bit in each tick to beat the engine's per-turn normalize. ReleaseUnequipped strips it if the
    /// weapon leaves the wielder's hands mid-battle. pickedSupport is the player's chosen roster
    /// support id (Offsets.RSupport), used for the redundancy note (0 = unknown).</summary>
    private void HoldSignature(long s, int rosterSlot, int weapon, string name, WeaponSignature? sig, int tier,
                                int hp, int maxHp, int brave, int faith, int pickedSupport = 0)
    {
        if (!Signatures.ResolveSupport(sig, tier, out int off, out byte mask)) return;
        if (!Signatures.ConditionMet(sig, hp, maxHp)) return;   // HP-gate; no-op for always-on signatures
        long addr = s + Offsets.CSupport + off;
        // Latch and announce only a confirmed set. OrSet's read-back fails on an unwritable page or
        // a teardown race, and then we retry next tick so the once-per-battle log is not burned.
        if (!MemBits.OrSet(addr, mask, out _)) return;
        _heldSupports[(rosterSlot, weapon)] = (s, off, mask, sig!.AbilityId, brave, faith);
        LogGrantOnce(weapon, name, sig!, off, mask, addr, pickedSupport);
    }

    /// <summary>Mid-battle unequip release. When a latched weapon is no longer in its wielder's
    /// roster hands, clear the granted bit, unless it is the player's own picked support. Only
    /// clears while the latched struct still fingerprint-matches (units migrate between fixed
    /// slots; same rule as Maim.Drive). An unreadable roster slot keeps the latch.</summary>
    private void ReleaseUnequipped()
    {
        if (_heldSupports.Count == 0) return;
        List<(int slot, int weapon)>? drop = null;
        foreach (var ((slot, weapon), v) in _heldSupports)
        {
            long rb = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
            if (!_mem.Readable(rb + Offsets.RNameId, 2)) continue;
            int level = _mem.U8(rb + Offsets.RLevel);
            if (level < 1 || level > 99) continue;
            // A grant is only ever held for a main-hand weapon; check main-hand only for release.
            bool wielded = _mem.U16(rb + Offsets.RRHand) == weapon;
            if (!Signatures.ShouldClearOnUnequip(wielded, _mem.U8(rb + Offsets.RSupport), v.abilityId))
            {
                if (!wielded) (drop ??= new()).Add((slot, weapon));   // unequipped, but the pick is the player's own
                continue;
            }
            (drop ??= new()).Add((slot, weapon));
            if (_mem.U8(v.s + Offsets.CBrave) != v.brave || _mem.U8(v.s + Offsets.CFaith) != v.faith) continue;
            if (MemBits.Clear(v.s + Offsets.CSupport + v.off, v.mask))
                ModLogger.EventWithTrace(LogVerb.Grant,
                    $"{LogNames.Weapon(weapon)} was unequipped; its granted ability is switched off.",
                    $"grant released (party slot {slot}, ability {v.abilityId})");
        }
        if (drop is not null)
            foreach (var k in drop) { _heldSupports.Remove(k); _grantLogged.Remove(k.weapon); }   // re-equip re-announces
    }

    /// <summary>Read the granted bit back and log it once per weapon per battle (SET vs miss, with
    /// the ability named). Warns when the support is build-time-only (e.g. HP Boost) and cannot take
    /// effect live, and notes when the player's own pick matches the grant (same bit, no stack).</summary>
    private void LogGrantOnce(int weapon, string name, WeaponSignature sig, int off, byte mask, long addr,
                               int pickedSupport = 0)
    {
        if (!_grantLogged.Add(weapon)) return;
        bool present = (_mem.U8(addr) & mask) != 0;   // read-back. Did our write actually land?
        ModLogger.EventWithTrace(LogVerb.Grant,
            $"{name} bestows {sig.DisplayLabel} on its wielder.",
            $"grant detail (support ability {sig.AbilityId}, readback={(present ? "SET" : "MISS")}, +0x98[{off}]=0x{mask:X2})");
        if (!present)
            ModLogger.Warn(LogVerb.Grant, $"The {sig.DisplayLabel} grant could not be confirmed by read-back; it may not take effect.");
        else if (Signatures.IsBuildTimeOnly(sig.AbilityId))
            ModLogger.Warn(LogVerb.Grant, $"{sig.DisplayLabel} is a build-time-only support; the live grant will not take effect.");
        if (pickedSupport != 0 && pickedSupport == sig.AbilityId)
            ModLogger.Event(LogVerb.Grant, $"The wielder already chose {sig.DisplayLabel} as their support; the weapon's grant adds nothing (pick a different support to benefit).");
    }

    /// <summary>Read a unit's (currentHP, maxHP) from the band by its (level, brave, faith)
    /// fingerprint, since the combat struct carries no HP and the static array freezes on battle
    /// restart. Returns (0,0) on no match; prefers real-position entries over (0,0) twins. Only
    /// called for HP-gated signatures.
    /// Two tiers. Tier 1 (rosterNameId &gt; 0) needs an exact band nameId match; a miss falls
    /// back to tier 2, the plain fingerprint scan with a veto on any entry whose nonzero nameId
    /// differs. The nameId read is unguarded (as in Iai/Wielder): Mem's U16 returns 0 when
    /// unreadable, and a Readable pre-filter would leave tier 1 dead against FakeSparseMemory.</summary>
    internal static (int hp, int maxHp) ReadHp(IGameMemory mem, int level, int brave, int faith, int rosterNameId = 0)
    {
        if (rosterNameId > 0)
        {
            var (found1, hp1, maxHp1) = ReadHpScan(mem, level, brave, faith, rosterNameId, exact: true);
            if (found1) return (hp1, maxHp1);
        }
        var (_, hp2, maxHp2) = ReadHpScan(mem, level, brave, faith, rosterNameId, exact: false);
        return (hp2, maxHp2);
    }

    /// <summary>One band pass <paramref name="exact"/> is tier 1 (band nameId == rosterNameId,
    /// only called with rosterNameId &gt; 0); otherwise tier 2 (fingerprint match, plus the foreign-nameId veto
    /// when rosterNameId &gt; 0).
    /// Decision-4 exception (b): on the non-exact pass with rosterNameId &lt;= 0 and
    /// two or more real-position candidates, returns (0,0) rather than guessing, as
    /// GrowthEngine.Locate.cs's PickHomogeneousTwin does. Consumers (GrowthEngine.cs, Ultima.cs,
    /// Afterimage.cs) treat (0,0) as no info.</summary>
    private static (bool found, int hp, int maxHp) ReadHpScan(IGameMemory mem, int level, int brave, int faith,
                                                              int rosterNameId, bool exact)
    {
        (int hp, int maxHp) result = (0, 0);
        bool foundReal = false, found = false;
        int realCount = 0;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Offsets.BandReadBase + (long)s * Offsets.CombatStride;
            if (!mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
            if (!Band.LevelMatchesRoster(level, mem.U8(addr + Offsets.ALevel))) continue;   // level = pre-battle roster value
            if (mem.U8(addr + Offsets.ABrave) != brave) continue;
            if (mem.U8(addr + Offsets.AFaith) != faith) continue;
            if (exact)
            {
                int entryNameId = mem.U16(addr + Offsets.ANameId);   // unguarded fail-safe read (Iai pattern)
                if (entryNameId != rosterNameId) continue;
            }
            else if (rosterNameId > 0)
            {
                int entryNameId = mem.U16(addr + Offsets.ANameId);
                if (entryNameId != 0 && entryNameId != rosterNameId) continue;   // veto
            }
            bool realPos = mem.U8(addr + Offsets.AGx) != 0 || mem.U8(addr + Offsets.AGy) != 0;
            if (foundReal && !realPos) continue;   // prefer real over twin
            if (realPos) realCount++;
            result = (mem.U16(addr + Offsets.AHp), mem.U16(addr + Offsets.AMaxHp));
            found = true;
            if (realPos) foundReal = true;
        }
        if (!exact && rosterNameId <= 0 && realCount >= 2) return (false, 0, 0);   // refuse, never guess
        return (found, result.hp, result.maxHp);
    }

    private static int Clamp(int v) => v < StatMin ? StatMin : v > StatMax ? StatMax : v;
}
