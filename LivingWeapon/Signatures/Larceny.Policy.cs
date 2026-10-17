using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>The per-struck-foe outcome of a Larceny hit (see <see cref="LarcenyPolicy.Decide"/>):
/// leave a duplicate alone, strip-without-keeping, or steal-and-hold.</summary>
internal enum LarcenyAction { Skip, Dispel, Steal }

/// <summary>
/// Pure decisions behind Arcanum's Larceny signature (no memory access). On a hit the foe's highest-priority
/// holdable buff is cleared and held on the wielder for LarcenyTurns of the wielder's own turns. The runtime
/// is Larceny.cs, gated on the buff-bit map in <see cref="Stealable"/> being completed live.
/// </summary>
internal static class LarcenyPolicy
{
    /// <summary>True when the signature is configured (LarcenyTurns set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.LarcenyTurns > 0;

    /// <summary>Never steal from an ally -- only enemies the wielder strikes.</summary>
    public static bool ShouldLatch(bool isEnemy) => isEnemy;

    /// <summary>What to do with one struck foe once its top buff is picked. Skip: the wielder already wears
    /// a stolen copy, so leave the foe alone (one foe per buff per term). Dispel: the wielder has the bit from
    /// its own source, so strip the foe without latching, and expiry never clears the wielder's own buff.
    /// Steal: the wielder lacks it, so strip the foe and grant and latch it.</summary>
    public static LarcenyAction Decide(bool alreadyHeld, bool wielderHasBuff)
        => alreadyHeld ? LarcenyAction.Skip
         : wielderHasBuff ? LarcenyAction.Dispel
         : LarcenyAction.Steal;

    /// <summary>A stealable buff: a band-relative status byte offset + its bit mask, with a label.</summary>
    public readonly record struct Buff(string Name, int Off, byte Mask);

    /// <summary>Priority order: the first buff the target has is the one stolen, so array order is the
    /// precedence. Reraise, Haste, Protect, Shell, Reflect, Regen.
    /// Reraise (+0x47/0x20) denies a free revive and grants it. Haste (+0x48/0x08), Protect (+0x48/0x20),
    /// Shell (+0x48/0x10), Reflect (+0x49/0x02), Regen (+0x48/0x40).
    /// Reraise and Regen are functional. The other four transfer their bits and are under a functional test
    /// (2026-06-15). Float was dropped because setting a display bit does not guarantee the effect, so every
    /// new row needs a functional test. Invisible is absent (player-only buff).</summary>
    public static readonly Buff[] Stealable =
    {
        new("Reraise", Offsets.AReraise, Offsets.AReraiseBit),   // +0x47/0x20, functional (FeignDeath shows the engine honors a held bit)
        new("Haste",   Offsets.AHaste,   Offsets.AHasteBit),     // +0x48/0x08, functional steal under test 2026-06-15
        new("Protect", Offsets.AProtect, Offsets.AProtectBit),   // +0x48/0x20, under test 2026-06-15
        new("Shell",   Offsets.AShell,   Offsets.AShellBit),     // +0x48/0x10, under test 2026-06-15
        new("Reflect", Offsets.AReflect, Offsets.AReflectBit),   // +0x49/0x02, under test 2026-06-15
        new("Regen", Offsets.ARegen, Offsets.ARegenBit),         // +0x48/0x40, functional (heals each turn, observed live 2026-06-15)
        // Float (+0x47/0x40) dropped 2026-06-15: the bit shows the icon but the unit does not float
        //   (observed live), and it is the lowest-value buff anyway.
    };

    /// <summary>Pick the highest-priority buff the target currently has set, or null when it has
    /// none worth stealing. <paramref name="readBand"/> maps a band-relative byte offset to its value.</summary>
    public static Buff? Pick(Func<int, byte> readBand)
    {
        foreach (var b in Stealable)
            if ((readBand(b.Off) & b.Mask) != 0) return b;
        return null;
    }

    /// <summary>True once a stolen buff has been worn its full term, counted in the wielder's own completed
    /// turns (TurnTracker.Turns, the acted-edge counter). The global-turn clock it replaced did not expire the
    /// buff in a normal fight (2026-06-16). Also true when the count is below the steal baseline, meaning a
    /// new battle reset it under a carried-over entry.</summary>
    public static bool IsExpired(int currentTurn, int stolenTurn, int turns)
        => currentTurn - stolenTurn >= turns || currentTurn < stolenTurn;

    // Guarded bit ops on a unit's band status byte, like Maim.HoldZero/Restore. All pre-filter with
    // Readable/Writable; a wrong offset at worst leaves a bit set for one battle (the next struct clears it).

    /// <summary>True when the unit at <paramref name="addr"/> currently has the bit set.</summary>
    public static bool HasBit(IGameMemory mem, long addr, int off, byte mask)
        => mem.Readable(addr + off, 1) && (mem.U8(addr + off) & mask) != 0;

    /// <summary>OR the bit on (grant/hold the stolen buff on the wielder). No-op if already set.</summary>
    public static void SetBit(IGameMemory mem, long addr, int off, byte mask)
    {
        long a = addr + off;
        if (!mem.Readable(a, 1) || !mem.Writable(a, 1)) return;
        byte cur = mem.U8(a);
        if ((cur & mask) == 0) mem.W8(a, (byte)(cur | mask));
    }

    /// <summary>AND-clear the bit (strip the buff from the foe, or drop the faded buff off the
    /// wielder). No-op if already clear.</summary>
    public static void ClearBit(IGameMemory mem, long addr, int off, byte mask)
    {
        long a = addr + off;
        if (!mem.Readable(a, 1) || !mem.Writable(a, 1)) return;
        byte cur = mem.U8(a);
        if ((cur & mask) != 0) mem.W8(a, (byte)(cur & ~mask));
    }
}

/// <summary>The active steals, keyed by (offset, mask), each with the wielder-turn index of the theft
/// (TurnTracker.Turns at steal time) as its expiry baseline. Several buffs can be held at once and each
/// fades Tuning.LarcenyHoldTurns wielder turns after it was stolen. Reset in Larceny.ResetBattle.</summary>
internal sealed class LarcenyState
{
    private readonly Dictionary<(int off, byte mask), int> _held = new();

    /// <summary>True while this buff is currently held on the wielder.</summary>
    public bool IsHeld((int off, byte mask) buff) => _held.ContainsKey(buff);

    /// <summary>Latch a freshly stolen buff. No-ops if already held (never reset its expiry baseline).</summary>
    public void Steal((int off, byte mask) buff, int stolenTurn)
    {
        if (!_held.ContainsKey(buff)) _held[buff] = stolenTurn;
    }

    /// <summary>The wielder-turn index at which this buff was stolen (the expiry baseline).</summary>
    public int StolenAt((int off, byte mask) buff)
        => _held.TryGetValue(buff, out var t) ? t : 0;

    /// <summary>Snapshot of every currently-held buff (safe to mutate the state while iterating).</summary>
    public IReadOnlyList<(int off, byte mask)> Held => new List<(int, byte)>(_held.Keys);

    /// <summary>Drop a buff that has faded (after clearing its bit on the wielder).</summary>
    public void Release((int off, byte mask) buff) => _held.Remove(buff);

    /// <summary>Forget all steals (battle exit -- the fresh per-battle struct clears the bits anyway).</summary>
    public void Clear() => _held.Clear();
}
