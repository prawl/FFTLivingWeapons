using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-252 stage 5. Reference-type box so a value (e.g. an int turn counter) can be a
/// <see cref="WielderKeyedStore{TState}"/> state. Callers sharing a Box share its value.
/// </summary>
internal sealed class Box<T>
{
    public T Value = default!;
}

/// <summary>
/// LW-252 stage 5. Per-wielder state keyed on roster nameId, with the (level,brave,faith)
/// fingerprint as a fallback. Replaces the fp-only keying that Iai, Larceny, Mushin and TurnTracker
/// used, which shared state between fp-twin units.
///
/// A key never rotates once assigned. Mem reads fail to 0 on transients, so deriving the key each
/// tick would flip a unit to its fp key when nameId glitches to 0 and re-arm its state (the old
/// FAIL-4 double-arm). Entries live in byName, byFp and a per-fp side map, and one entry can be
/// reachable from both byName and byFp. Nothing is removed mid-battle; <see cref="Clear"/> runs
/// from each owner's ResetBattle.
///
/// <see cref="Probe"/> (read-only) and <see cref="GetOrCreate"/> share one resolution rule.
///   nameId > 0. A byName hit wins. Else a byFp hit is adopted (byName now aliases it). Else
///     GetOrCreate makes an entry under byName and the side map, and Probe returns null.
///   nameId == 0. A byFp hit wins. Else if exactly one entry is on the side map for this fp it is
///     returned (a name-keyed unit whose nameId glitched). Two or more return null, since guessing
///     could corrupt a different twin's state. None. GetOrCreate makes an entry under byFp and the
///     side map, and Probe returns null.
///
/// A fp-twin pair where one member never resolves a nameId still shares one byFp state, as before
/// stage 5. A pair that both resolve nameIds gets independent states. TState is a reference type
/// because consumers mutate it in place.
/// </summary>
internal sealed class WielderKeyedStore<TState> where TState : class
{
    private sealed class Entry
    {
        public TState State = null!;
        public (int lvl, int br, int fa) Fp;
        public int ArmNameId;
    }

    private readonly Dictionary<int, Entry> _byName = new();
    private readonly Dictionary<(int lvl, int br, int fa), Entry> _byFp = new();
    // Every created entry by its own fp, added once at creation. Flattening it enumerates each
    // state once for <see cref="All"/>.
    private readonly Dictionary<(int lvl, int br, int fa), List<Entry>> _byFpSide = new();

    /// <summary>Lookup that never creates. Null on a miss or on the ambiguous nameId-0, 2+-twin
    /// case; both mean skip this tick.</summary>
    public TState? Probe(int nameId, (int lvl, int br, int fa) fp) => Resolve(nameId, fp, factory: null);

    /// <summary>Same rule as <see cref="Probe"/>, but a new nameId or a new fp creates a state via
    /// <paramref name="factory"/>. Still null for the ambiguous nameId-0, 2+-twin case.</summary>
    public TState? GetOrCreate(int nameId, (int lvl, int br, int fa) fp, Func<TState> factory) =>
        Resolve(nameId, fp, factory);

    private TState? Resolve(int nameId, (int lvl, int br, int fa) fp, Func<TState>? factory)
    {
        if (nameId > 0)
        {
            if (_byName.TryGetValue(nameId, out var byName)) return byName.State;
            if (_byFp.TryGetValue(fp, out var byFp))
            {
                _byName[nameId] = byFp;   // alias-adopt. ByFp keeps the entry too
                return byFp.State;
            }
            if (factory == null) return null;   // Probe. Miss
            var created = new Entry { State = factory(), Fp = fp, ArmNameId = nameId };
            _byName[nameId] = created;
            AddSide(fp, created);
            return created.State;
        }
        else
        {
            if (_byFp.TryGetValue(fp, out var byFp)) return byFp.State;
            if (_byFpSide.TryGetValue(fp, out var side))
            {
                if (side.Count == 1) return side[0].State;   // transient recovery
                return null;                                  // 2+: ambiguous, skip
            }
            if (factory == null) return null;   // Probe. Miss
            var created = new Entry { State = factory(), Fp = fp, ArmNameId = 0 };
            _byFp[fp] = created;
            AddSide(fp, created);
            return created.State;
        }
    }

    private void AddSide((int lvl, int br, int fa) fp, Entry e)
    {
        if (!_byFpSide.TryGetValue(fp, out var list)) _byFpSide[fp] = list = new List<Entry>();
        list.Add(e);
    }

    /// <summary>Every state this store has created, once each, with its fp and ArmNameId from
    /// creation. Flattens the side map, which holds each entry exactly once. Iai's release pass and
    /// LarcenyHoldings (Drive/Expire/ReleaseAll) sweep this.</summary>
    public IEnumerable<(TState State, (int lvl, int br, int fa) Fp, int ArmNameId)> All
    {
        get
        {
            foreach (var list in _byFpSide.Values)
                foreach (var e in list)
                    yield return (e.State, e.Fp, e.ArmNameId);
        }
    }

    /// <summary>Forget every entry (battle exit). Wired into each owner's existing ResetBattle.</summary>
    public void Clear()
    {
        _byName.Clear();
        _byFp.Clear();
        _byFpSide.Clear();
    }
}
