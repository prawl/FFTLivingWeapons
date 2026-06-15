using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Arcanum's "Larceny" signature -- no memory access. On a hit, Arcanum
/// doesn't just dispel the foe's buff, it STEALS it: the highest-priority holdable buff is cleared
/// on the struck enemy and held on the wielder for LarcenyTurns of the wielder's own turns, then it
/// fades. The stateful latch/hold/restore runtime (Larceny.cs) is Maim-shaped but holds on the
/// WIELDER, and is GATED on the buff-bit map below being completed live.
///
/// THE LIVE-PENDING PART -- the buff table. Only the buffs PROVEN holdable today are wired:
/// Reraise (+0x47/0x20) and Invisible (+0x47/0x10), the FeignDeath pair (a held bit there is
/// honored by the engine). The MARQUEE buffs -- Haste / Protect / Shell / Reflect / Regen / Float /
/// Faith -- have UNMAPPED bits, and even once mapped a held bit might be cosmetic-only (the Plague
/// green-tint seam). Map them with `tools/probes/poison_probe.py diff &lt;mhp&gt; &lt;lvl&gt;` (apply the
/// buff, watch which bit in +0x44..+0x4C flips on) and confirm effect with its `holdbit` mode, then
/// add a row to <see cref="Stealable"/>. The whole transfer mechanism is exercised in tests against
/// the proven Reraise bit, so extending coverage is purely adding table rows.
/// </summary>
internal static class LarcenyPolicy
{
    /// <summary>True when the signature is configured (LarcenyTurns set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.LarcenyTurns > 0;

    /// <summary>Never steal from an ally -- only enemies the wielder strikes.</summary>
    public static bool ShouldLatch(bool isEnemy) => isEnemy;

    /// <summary>A stealable buff: a band-relative status byte offset + its bit mask, with a label.</summary>
    public readonly record struct Buff(string Name, int Off, byte Mask);

    /// <summary>Priority order -- the first buff the target actually has is the one stolen. Only
    /// PROVEN-holdable buffs are listed (see the class remarks); add mapped marquee buffs here.</summary>
    public static readonly Buff[] Stealable =
    {
        new("Reraise",   Offsets.AReraise,   Offsets.AReraiseBit),     // +0x47/0x20 -- proven (FeignDeath holds it live)
        new("Invisible", Offsets.AInvisible, Offsets.AInvisibleBit),   // +0x47/0x10 -- proven (FeignDeath holds it live)
        // TODO (poison_probe diff + holdbit): Haste, Protect, Shell, Reflect, Regen, Float, Faith.
    };

    /// <summary>Pick the highest-priority buff the target currently has set, or null when it has
    /// none worth stealing. <paramref name="readBand"/> maps a band-relative byte offset to its value.</summary>
    public static Buff? Pick(Func<int, byte> readBand)
    {
        foreach (var b in Stealable)
            if ((readBand(b.Off) & b.Mask) != 0) return b;
        return null;
    }

    /// <summary>True once the wielder has worn a stolen buff for its full term (counted in the
    /// wielder's own completed turns since the steal).</summary>
    public static bool IsExpired(int wielderTurnsNow, int baselineTurns, int larcenyTurns)
        => wielderTurnsNow - baselineTurns >= larcenyTurns;

    // ── Guarded bit ops on a unit's band status byte (injected mem, so tests drive them with a
    //    fake or a pinned buffer; mirrors Maim.HoldZero/Restore). Every one pre-filters with
    //    Readable/Writable; the worst a wrong offset can do is leave a status bit set for one
    //    battle (the fresh per-battle struct clears it). ──

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

/// <summary>The active steals: each holdable buff the wielder has lifted, keyed by its (offset,mask),
/// with the wielder's turn count at the moment of the theft (the expiry baseline). The wielder can
/// hold several different stolen buffs at once; each fades independently after LarcenyTurns of the
/// wielder's turns. Reset on battle exit (Larceny.ResetBattle).</summary>
internal sealed class LarcenyState
{
    private readonly Dictionary<(int off, byte mask), int> _held = new();

    /// <summary>True while this buff is currently held on the wielder.</summary>
    public bool IsHeld((int off, byte mask) buff) => _held.ContainsKey(buff);

    /// <summary>Latch a freshly stolen buff. No-ops if already held (never reset its expiry baseline).</summary>
    public void Steal((int off, byte mask) buff, int wielderTurns)
    {
        if (!_held.ContainsKey(buff)) _held[buff] = wielderTurns;
    }

    /// <summary>The wielder's turn count when this buff was stolen (the expiry baseline).</summary>
    public int BaselineTurns((int off, byte mask) buff)
        => _held.TryGetValue(buff, out int t) ? t : 0;

    /// <summary>Snapshot of every currently-held buff (safe to mutate the state while iterating).</summary>
    public IReadOnlyList<(int off, byte mask)> Held => new List<(int, byte)>(_held.Keys);

    /// <summary>Drop a buff that has faded (after clearing its bit on the wielder).</summary>
    public void Release((int off, byte mask) buff) => _held.Remove(buff);

    /// <summary>Forget all steals (battle exit -- the fresh per-battle struct clears the bits anyway).</summary>
    public void Clear() => _held.Clear();
}
