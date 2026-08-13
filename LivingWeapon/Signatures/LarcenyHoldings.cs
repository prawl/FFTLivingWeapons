using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Per-wielder steal ledger for the Larceny signature. Owns a dictionary keyed by
/// (level,brave,faith) fingerprint so two deployed Arcanum holders each accumulate and
/// expire their own stolen buffs independently, attributed to whichever one actually acted.
/// Memory is injected so the class is unit-testable with a pinned buffer.
/// </summary>
internal sealed class LarcenyHoldings
{
    private readonly IGameMemory _mem;
    private readonly Dictionary<(int, int, int), LarcenyState> _byWielder = new();

    public LarcenyHoldings(IGameMemory mem) => _mem = mem;

    /// <summary>Forget all per-wielder ledgers (battle exit).</summary>
    public void Clear() => _byWielder.Clear();

    /// <summary>True while this buff is latched in the ledger for the given wielder fingerprint.</summary>
    public bool IsHeld((int lvl, int br, int fa) fp, (int off, byte mask) key)
        => _byWielder.TryGetValue(fp, out var s) && s.IsHeld(key);

    /// <summary>Set the bit on the wielder's band entry and latch it in that wielder's ledger.
    /// No-ops the ledger latch if already held (never resets the expiry baseline).</summary>
    public void Steal((int lvl, int br, int fa) fp, long wielderAddr, (int off, byte mask) key, int stolenTurn)
    {
        LarcenyPolicy.SetBit(_mem, wielderAddr, key.off, key.mask);
        if (!_byWielder.TryGetValue(fp, out var s))
            _byWielder[fp] = s = new LarcenyState();
        s.Steal(key, stolenTurn);
    }

    /// <summary>Re-assert every held bit on each wielder's CURRENT band entry. If a wielder
    /// cannot be located this tick its bits are skipped (retried next tick; battle exit backstops).</summary>
    public void Drive(Func<(int, int, int), long> locate)
    {
        foreach (var kv in _byWielder)
        {
            long a = locate(kv.Key);
            if (a == 0) continue;
            foreach (var (off, mask) in kv.Value.Held)
                LarcenyPolicy.SetBit(_mem, a, off, mask);
        }
    }

    /// <summary>Drop expired stolen buffs per wielder, counting that wielder's own turns.
    /// A wielder that can't be located this tick is skipped (battle exit backstops).
    /// Empties ledgers are pruned after the iteration.</summary>
    public void Expire(Func<(int, int, int), long> locate, Func<(int, int, int), int> turnsOf, int holdTurns)
    {
        List<(int, int, int)>? empties = null;
        foreach (var kv in _byWielder)
        {
            var fp = kv.Key;
            var st = kv.Value;
            long a = locate(fp);
            if (a == 0) continue;   // can't locate this tick -> retry next (battle exit backstops)
            int turn = turnsOf(fp);
            List<(int off, byte mask)>? drop = null;
            foreach (var key in st.Held)
                if (LarcenyPolicy.IsExpired(turn, st.StolenAt(key), holdTurns))
                    (drop ??= new()).Add(key);
            if (drop != null)
                foreach (var key in drop)
                {
                    LarcenyPolicy.ClearBit(_mem, a, key.off, key.mask);
                    st.Release(key);
                    ModLogger.Event(LogVerb.Signature, $"The stolen {BuffName(key)} wore off the wielder after {holdTurns} of its turns.");
                }
            if (st.Held.Count == 0) (empties ??= new()).Add(fp);
        }
        if (empties != null) foreach (var fp in empties) _byWielder.Remove(fp);
    }

    /// <summary>The stealable buff's display name for the (offset,mask) key, from LarcenyPolicy's
    /// own table (the steal path picked it from there, so the key always round-trips); "buff" is
    /// the defensive fallback for a key no longer in the table.</summary>
    private static string BuffName((int off, byte mask) key)
    {
        foreach (var b in LarcenyPolicy.Stealable)
            if (b.Off == key.off && b.Mask == key.mask) return b.Name;
        return "buff";
    }

    /// <summary>Clear every held bit off every locatable wielder (battle exit).</summary>
    public void ReleaseAll(Func<(int, int, int), long> locate)
    {
        foreach (var kv in _byWielder)
        {
            long a = locate(kv.Key);
            if (a == 0) continue;
            foreach (var (off, mask) in kv.Value.Held)
                LarcenyPolicy.ClearBit(_mem, a, off, mask);
        }
    }
}
