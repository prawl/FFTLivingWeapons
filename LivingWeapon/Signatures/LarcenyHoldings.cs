using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Per-wielder steal ledger for the Larceny signature. LW-252 stage 5: re-keyed off
/// <see cref="WielderKeyedStore{TState}"/> (nameId primary, fp fallback -- see its class doc), so
/// two deployed Arcanum holders each accumulate and expire their own stolen buffs independently
/// even when they share a (level,brave,faith) fingerprint, attributed to whichever one actually
/// acted. Every method keeps its ORIGINAL fp-only overload (delegating with nameId 0 -- the fp
/// lane, with side-map recovery) so existing callers/tests are untouched; a caller that resolved
/// a real nameId should prefer the nameId-aware overload so its query/steal key matches the
/// credit key (Larceny.cs does, from its wielder resolve). Memory is injected so the class is
/// unit-testable with a pinned buffer.
/// </summary>
internal sealed class LarcenyHoldings
{
    private readonly IGameMemory _mem;
    private readonly WielderKeyedStore<LarcenyState> _byWielder = new();

    public LarcenyHoldings(IGameMemory mem) => _mem = mem;

    /// <summary>Forget all per-wielder ledgers (battle exit).</summary>
    public void Clear() => _byWielder.Clear();

    /// <summary>True while this buff is latched in the ledger for the given wielder fingerprint
    /// (fp lane; nameId 0). See the nameId-aware overload below.</summary>
    public bool IsHeld((int lvl, int br, int fa) fp, (int off, byte mask) key) => IsHeld(0, fp, key);

    /// <summary>LW-252 stage 5: nameId-aware IsHeld -- Probe (read-only, never creates) via the
    /// store's resolution law.</summary>
    public bool IsHeld(int nameId, (int lvl, int br, int fa) fp, (int off, byte mask) key)
        => _byWielder.Probe(nameId, fp) is { } s && s.IsHeld(key);

    /// <summary>Set the bit on the wielder's band entry and latch it in that wielder's ledger
    /// (fp lane; nameId 0). See the nameId-aware overload below.
    /// No-ops the ledger latch if already held (never resets the expiry baseline).</summary>
    public void Steal((int lvl, int br, int fa) fp, long wielderAddr, (int off, byte mask) key, int stolenTurn)
        => Steal(0, fp, wielderAddr, key, stolenTurn);

    /// <summary>LW-252 stage 5: nameId-aware Steal. An ambiguous nameId-0 tick for a fp two
    /// SEEDED twins already registered under (GetOrCreate returns null) SKIPS the steal
    /// entirely, bit included -- an untracked, never-expiring hold would be worse than the foe
    /// simply keeping its buff this tick (miss beats mis-credit).</summary>
    public void Steal(int nameId, (int lvl, int br, int fa) fp, long wielderAddr, (int off, byte mask) key, int stolenTurn)
    {
        var s = _byWielder.GetOrCreate(nameId, fp, () => new LarcenyState());
        if (s == null) return;
        LarcenyPolicy.SetBit(_mem, wielderAddr, key.off, key.mask);
        s.Steal(key, stolenTurn);
    }

    /// <summary>Re-assert every held bit on each wielder's CURRENT band entry. If a wielder
    /// cannot be located this tick its bits are skipped (retried next tick; battle exit backstops).</summary>
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

    /// <summary>Drop expired stolen buffs per wielder, counting that wielder's own turns (fp
    /// lane; every wielder's turnsOf is queried with nameId 0). See the nameId-aware overload
    /// below. A wielder that can't be located this tick is skipped (battle exit backstops).
    /// LW-252 stage 5: an emptied ledger is no longer pruned (WielderKeyedStore's own law: NOTHING
    /// is ever removed mid-battle) -- it just stays inert; IsHeld already returns false for it
    /// either way, and Clear() wipes it at battle end.</summary>
    public void Expire(Func<(int, int, int), long> locate, Func<(int, int, int), int> turnsOf, int holdTurns)
        => Expire(locate, (_, fp) => turnsOf(fp), holdTurns);

    /// <summary>LW-252 stage 5: nameId-aware Expire -- <paramref name="turnsOf"/> receives each
    /// held wielder's OWN nameId (captured at that ledger's creation) alongside its fp, so a
    /// caller with a real TurnTracker query key (Larceny.cs's Turns) matches its own credit key
    /// even for a wielder OTHER than the one currently acting.</summary>
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
        foreach (var (state, fp, _) in _byWielder.All)
        {
            long a = locate(fp);
            if (a == 0) continue;
            foreach (var (off, mask) in state.Held)
                LarcenyPolicy.ClearBit(_mem, a, off, mask);
        }
    }
}
