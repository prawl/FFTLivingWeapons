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
/// THE BUFF TABLE. WIRED: Reraise (+0x47/0x20, FUNCTIONAL -- FeignDeath proves the engine honors a held
/// bit) + Regen (+0x48/0x40, bit transfers; functional heal pending live confirm). Haste/Protect/Shell/
/// Reflect have bit-CONFIRMED offsets (the FFT status map, six cross-checks) but sit commented in
/// <see cref="Stealable"/> pending a FUNCTIONAL live test each. Float was DROPPED 2026-06-15: its bit
/// only paints the icon, the unit doesn't actually float (cosmetic-only -- a display bit is not the
/// effect). Invisible dropped earlier (player-only buff). See Stealable for the precedence + the lesson.
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
    /// ARRAY ORDER *is* the steal precedence (highest steal-value first):
    ///   Reraise &gt; Haste &gt; Protect &gt; Shell &gt; Reflect &gt; Regen   (Float dropped, see below).
    ///   1. Reraise -- free auto-revive; deny it AND wear it = the biggest swing.   [+0x47/0x20]
    ///   2. Haste   -- +50% turn frequency, the strongest combat buff.              [+0x48/0x08]
    ///   3. Protect -- halves physical damage (great on a melee wielder).           [+0x48/0x20]
    ///   4. Shell   -- halves magic damage.                                         [+0x48/0x10]
    ///   5. Reflect -- bounces single-target magic.                                 [+0x49/0x02]
    ///   6. Regen   -- HP regained each turn.                                       [+0x48/0x40]
    /// WIRED (all six): Reraise + Regen are FUNCTIONAL (proven live). Haste/Protect/Shell/Reflect are
    /// wired and UNDER live functional test 2026-06-15 -- the bits transfer; whether the EFFECT applies
    /// is the open question (drop any that prove cosmetic, like Float). THE HARD LESSON (Float, 2026-06-15):
    /// setting a display bit does NOT guarantee the effect -- Float painted the icon but the unit didn't
    /// float (its hover/earth-immunity lives in engine state the bit doesn't touch). So every new row
    /// needs a FUNCTIONAL live test, not just a bit transfer. Invisible is also absent (player-only
    /// buff). The transfer mechanism + the multi-target Decide already work for any row.</summary>
    public static readonly Buff[] Stealable =
    {
        new("Reraise", Offsets.AReraise, Offsets.AReraiseBit),   // 1 -- +0x47/0x20, FUNCTIONAL (FeignDeath proves the engine honors a held bit)
        new("Haste",   Offsets.AHaste,   Offsets.AHasteBit),     // 2 -- +0x48/0x08, functional steal UNDER TEST 2026-06-15
        new("Protect", Offsets.AProtect, Offsets.AProtectBit),   // 3 -- +0x48/0x20, functional steal UNDER TEST 2026-06-15
        new("Shell",   Offsets.AShell,   Offsets.AShellBit),     // 4 -- +0x48/0x10, functional steal UNDER TEST 2026-06-15
        new("Reflect", Offsets.AReflect, Offsets.AReflectBit),   // 5 -- +0x49/0x02, functional steal UNDER TEST 2026-06-15
        new("Regen", Offsets.ARegen, Offsets.ARegenBit),         // 6 -- +0x48/0x40, FUNCTIONAL (heals each turn -- proven live 2026-06-15)
        // Float (+0x47/0x40) DROPPED 2026-06-15: setting the bit shows the icon but does NOT make the unit
        //   float -- the hover/earth-immunity effect lives in engine state the display bit doesn't touch
        //   (the cosmetic-only seam, proven live). Lowest-value buff anyway, so not worth chasing the path.
    };

    /// <summary>Pick the highest-priority buff the target currently has set, or null when it has
    /// none worth stealing. <paramref name="readBand"/> maps a band-relative byte offset to its value.</summary>
    public static Buff? Pick(Func<int, byte> readBand)
    {
        foreach (var b in Stealable)
            if ((readBand(b.Off) & b.Mask) != 0) return b;
        return null;
    }

    /// <summary>True once a stolen buff has been worn its full term -- counted in the WIELDER's OWN
    /// completed turns (TurnTracker.Turns for the wielder's fingerprint -- the acted-edge counter that
    /// reliably tallies a player unit's turns, unlike the noisy active-unit/CT reads). The GLOBAL-turn
    /// clock this replaced did not expire the buff in a normal fight (2026-06-16); a deployed wielder
    /// always takes turns (you can't bench mid-battle), so the per-unit count always advances -- no
    /// wall-clock backstop is needed. Also true when the count sits BELOW the steal baseline -- a new
    /// battle reset it under a ledger entry carried over from the prior fight: drop it rather than wait
    /// out a term that can never be reached (battle-restart carryover guard).</summary>
    public static bool IsExpired(int currentTurn, int stolenTurn, int turns)
        => currentTurn - stolenTurn >= turns || currentTurn < stolenTurn;

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
/// with the WIELDER-TURN index of the theft (the expiry baseline -- TurnTracker.Turns for the wielder
/// at steal time). The wielder can hold several different stolen buffs at once; each fades independently
/// Tuning.LarcenyHoldTurns of the wielder's own turns after it was stolen. Reset on battle exit
/// (Larceny.ResetBattle).</summary>
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
