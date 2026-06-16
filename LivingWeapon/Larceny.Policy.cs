using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>The per-struck-foe outcome of a Larceny hit (see <see cref="LarcenyPolicy.Decide"/>):
/// leave a duplicate alone, strip-without-keeping, or steal-and-hold.</summary>
internal enum LarcenyAction { Skip, Dispel, Steal }

/// <summary>
/// The pure decisions behind Arcanum's "Larceny" signature -- no memory access. On a hit, Arcanum
/// doesn't just dispel the foe's buff, it STEALS it: the highest-priority holdable buff is cleared
/// on the struck enemy and held on the wielder for LarcenyTurns of the wielder's own turns, then it
/// fades. The stateful latch/hold/restore runtime (Larceny.cs) is Maim-shaped but holds on the
/// WIELDER, and is GATED on the buff-bit map below being completed live.
///
/// THE LIVE-PENDING PART -- the buff table. Only the buffs PROVEN holdable today are wired:
/// Reraise (+0x47/0x20) and Invisible (+0x47/0x10), the FeignDeath pair (a held bit there is
/// honored by the engine). The MARQUEE buffs -- Haste / Protect / Shell / Reflect / Regen / Float --
/// have UNMAPPED bits, and even once mapped a held bit might be cosmetic-only (the Plague green-tint
/// seam). The locked STEAL PRECEDENCE (which one a multi-buff foe loses) lives on <see cref="Stealable"/>:
/// Reraise &gt; Haste &gt; Protect &gt; Shell &gt; Reflect &gt; Regen &gt; Float, with Invisible last. Map each with
/// `tools/probes/poison_probe.py diff &lt;mhp&gt; &lt;lvl&gt;` (apply the buff, watch which bit in +0x44..+0x4C
/// flips on) and confirm effect with its `holdbit` mode, then INSERT its row at the ranked position.
/// The whole transfer mechanism is exercised in tests against the proven Reraise bit, so extending
/// coverage is purely adding table rows.
/// </summary>
internal static class LarcenyPolicy
{
    /// <summary>True when the signature is configured (LarcenyTurns set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.LarcenyTurns > 0;

    /// <summary>Never steal from an ally -- only enemies the wielder strikes.</summary>
    public static bool ShouldLatch(bool isEnemy) => isEnemy;

    /// <summary>What to do with ONE struck foe whose highest-priority buff has been picked. Applied
    /// once per damaged enemy, so it governs the multi-target sweep (one action hitting several
    /// buffed foes):
    ///   Skip   -- the wielder is already wearing a STOLEN copy of this exact buff: leave this foe's
    ///             copy untouched (don't even strip it). Steal each buff from only ONE foe per term.
    ///   Dispel -- the wielder already has the bit from ANOTHER source (its own enchantment) but it
    ///             was never stolen: strip the foe but DON'T latch, so expiry never clears the
    ///             wielder's own buff. Applies to every such duplicate foe.
    ///   Steal  -- a buff the wielder lacks entirely: strip the foe and grant + latch it.</summary>
    public static LarcenyAction Decide(bool alreadyHeld, bool wielderHasBuff)
        => alreadyHeld ? LarcenyAction.Skip
         : wielderHasBuff ? LarcenyAction.Dispel
         : LarcenyAction.Steal;

    /// <summary>A stealable buff: a band-relative status byte offset + its bit mask, with a label.</summary>
    public readonly record struct Buff(string Name, int Off, byte Mask);

    /// <summary>Priority order -- the first buff the target actually has is the one stolen, so the
    /// ARRAY ORDER *is* the steal precedence. Locked design ranking (highest steal-value first):
    ///   1. Reraise   -- a free auto-revive; deny it AND wear it = the biggest swing.   [PROVEN +0x47/0x20]
    ///   2. Haste     -- +50% turn frequency, the strongest combat buff in the game.    [TODO -- map]
    ///   3. Protect   -- halves physical damage (great on a melee wielder).             [TODO -- map]
    ///   4. Shell     -- halves magic damage.                                           [TODO -- map]
    ///   5. Reflect   -- bounces single-target magic.                                   [TODO -- map]
    ///   6. Regen     -- HP regained each turn.                                         [TODO -- map]
    ///   7. Float     -- earth/trap immunity (situational).                             [TODO -- map]
    ///   last. Invisible -- pops the instant the wielder attacks, so it's a weak HELD buff; ranked
    ///         below every marquee buff (stripping it off a foe is still worth it -- makes them
    ///         targetable again).                                                       [PROVEN +0x47/0x10]
    /// Only the PROVEN-holdable bits are wired today; map each TODO with `poison_probe.py diff
    /// &lt;mhp&gt; &lt;lvl&gt;` (watch the bit flip in +0x44..+0x4C) + `holdbit` (confirm the held bit is
    /// honored, not cosmetic), then INSERT its row at the ranked position below. The transfer
    /// mechanism and the multi-target Decide already work for any row.</summary>
    public static readonly Buff[] Stealable =
    {
        new("Reraise",   Offsets.AReraise,   Offsets.AReraiseBit),     // 1 -- +0x47/0x20, proven (FeignDeath holds it live)
        // 2-7 insert here once mapped, IN THIS ORDER (each gated on poison_probe diff+holdbit):
        //   Haste, Protect, Shell, Reflect, Regen, Float.
        new("Invisible", Offsets.AInvisible, Offsets.AInvisibleBit),   // last -- +0x47/0x10, proven; breaks on the wielder's own attack
    };

    /// <summary>Pick the highest-priority buff the target currently has set, or null when it has
    /// none worth stealing. <paramref name="readBand"/> maps a band-relative byte offset to its value.</summary>
    public static Buff? Pick(Func<int, byte> readBand)
    {
        foreach (var b in Stealable)
            if ((readBand(b.Off) & b.Mask) != 0) return b;
        return null;
    }

    /// <summary>True once a stolen buff has been worn its full term -- counted in GLOBAL turns (any
    /// unit's turn, off TurnTracker.GlobalTurns) elapsed since the steal. NOT the wielder's own turn
    /// count, which never advances while the player parks the unit (the buff held through 6 sat-out
    /// turns, live 2026-06-14), and NOT wall-clock (it bled down in menus and ignored battle pace).</summary>
    public static bool IsExpired(int currentTurn, int stolenTurn, int turns)
        => currentTurn - stolenTurn >= turns;

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
/// with the GLOBAL-turn index of the theft (the expiry baseline -- TurnTracker.GlobalTurns at steal
/// time). The wielder can hold several different stolen buffs at once; each fades independently
/// Tuning.LarcenyHoldTurns global turns after it was stolen. Reset on battle exit (Larceny.ResetBattle).</summary>
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

    /// <summary>The global-turn index at which this buff was stolen (the expiry baseline).</summary>
    public int StolenAt((int off, byte mask) buff)
        => _held.TryGetValue(buff, out var t) ? t : 0;

    /// <summary>Snapshot of every currently-held buff (safe to mutate the state while iterating).</summary>
    public IReadOnlyList<(int off, byte mask)> Held => new List<(int, byte)>(_held.Keys);

    /// <summary>Drop a buff that has faded (after clearing its bit on the wielder).</summary>
    public void Release((int off, byte mask) buff) => _held.Remove(buff);

    /// <summary>Forget all steals (battle exit -- the fresh per-battle struct clears the bits anyway).</summary>
    public void Clear() => _held.Clear();
}
