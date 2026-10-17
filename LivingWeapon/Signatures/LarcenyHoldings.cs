using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Per-wielder steal ledger for Larceny. LW-252 stage 5. Keyed by <see cref="WielderKeyedStore{TState}"/> (nameId
/// primary, fp fallback), so two Arcanum holders sharing a (level, brave, faith) fingerprint expire their own
/// stolen buffs separately. The fp-only overloads delegate with nameId 0; callers with a real nameId should use
/// the nameId-aware ones so query and credit keys match (Larceny.cs does). Memory is injected for tests.
/// </summary>
internal sealed class LarcenyHoldings
{
    private readonly IGameMemory _mem;
    private readonly WielderKeyedStore<LarcenyState> _byWielder = new();

    public LarcenyHoldings(IGameMemory mem) => _mem = mem;

    /// <summary>Forget all per-wielder ledgers (battle exit).</summary>
    public void Clear() => _byWielder.Clear();

    /// <summary>True while this buff is latched for the wielder fingerprint (fp lane, nameId 0).</summary>
    public bool IsHeld((int lvl, int br, int fa) fp, (int off, byte mask) key) => IsHeld(0, fp, key);

    /// <summary>LW-252 stage 5. NameId-aware IsHeld. Read-only, never creates a ledger.</summary>
    public bool IsHeld(int nameId, (int lvl, int br, int fa) fp, (int off, byte mask) key)
        => _byWielder.Probe(nameId, fp) is { } s && s.IsHeld(key);

    /// <summary>Set the bit on the wielder's band entry and latch it (fp lane, nameId 0).
    /// Does not reset the expiry baseline if already held.</summary>
    public void Steal((int lvl, int br, int fa) fp, long wielderAddr, (int off, byte mask) key, int stolenTurn)
        => Steal(0, fp, wielderAddr, key, stolenTurn);

    /// <summary>LW-252 stage 5. NameId-aware Steal. An ambiguous nameId-0 tick for a fp that two seeded twins share
    /// (GetOrCreate returns null) skips the steal, bit included, since an untracked hold that never expires is
    /// worse than a missed steal.</summary>
    public void Steal(int nameId, (int lvl, int br, int fa) fp, long wielderAddr, (int off, byte mask) key, int stolenTurn)
    {
        var s = _byWielder.GetOrCreate(nameId, fp, () => new LarcenyState());
        if (s == null) return;
        LarcenyPolicy.SetBit(_mem, wielderAddr, key.off, key.mask);
        s.Steal(key, stolenTurn);
    }

    /// <summary>Re-assert every held bit on each wielder's current band entry. Unlocatable wielders retry next tick.</summary>
    public void Drive(Func<(int, int, int), long> locate)
    {
        foreach (var (state, fp, _) in _byWielder.All)
        {
            long a = locate(fp);
            if (a == 0) continue;
            foreach (var (off, mask) in state.Held)
                LarcenyPolicy.SetBit(_mem, a, off, mask);
        }
    }

    /// <summary>Drop expired stolen buffs per wielder, counting that wielder's own turns (fp lane, nameId 0).
    /// LW-252 stage 5. Emptied ledgers are not pruned mid-battle (WielderKeyedStore rule); Clear() wipes them at battle end.</summary>
    public void Expire(Func<(int, int, int), long> locate, Func<(int, int, int), int> turnsOf, int holdTurns)
        => Expire(locate, (_, fp) => turnsOf(fp), holdTurns);

    /// <summary>LW-252 stage 5. NameId-aware Expire. <paramref name="turnsOf"/> gets each held wielder's own nameId
    /// (captured at ledger creation), so the turn query matches the credit key even when another wielder is acting.</summary>
    public void Expire(Func<(int, int, int), long> locate, Func<int, (int, int, int), int> turnsOf, int holdTurns)
    {
        foreach (var (state, fp, armNameId) in _byWielder.All)
        {
            long a = locate(fp);
            if (a == 0) continue;   // can't locate this tick -> retry next (battle exit backstops)
            int turn = turnsOf(armNameId, fp);
            List<(int off, byte mask)>? drop = null;
            foreach (var key in state.Held)
                if (LarcenyPolicy.IsExpired(turn, state.StolenAt(key), holdTurns))
                    (drop ??= new()).Add(key);
            if (drop != null)
                foreach (var key in drop)
                {
                    LarcenyPolicy.ClearBit(_mem, a, key.off, key.mask);
                    state.Release(key);
                    ModLogger.Event(LogVerb.Signature, $"The stolen {BuffName(key)} wore off the wielder after {holdTurns} of its turns.");
                }
        }
    }

    /// <summary>Display name for the (offset, mask) key from LarcenyPolicy.Stealable, or "buff" if the key is missing.</summary>
    private static string BuffName((int off, byte mask) key)
    {
        foreach (var b in LarcenyPolicy.Stealable)
            if (b.Off == key.off && b.Mask == key.mask) return b.Name;
        return "buff";
    }

    /// <summary>Clear every held bit off every locatable wielder (battle exit).</summary>
    public void ReleaseAll(Func<(int, int, int), long> locate)
    {
        foreach (var (state, fp, _) in _byWielder.All)
        {
            long a = locate(fp);
            if (a == 0) continue;
            foreach (var (off, mask) in state.Held)
                LarcenyPolicy.ClearBit(_mem, a, off, mask);
        }
    }
}
