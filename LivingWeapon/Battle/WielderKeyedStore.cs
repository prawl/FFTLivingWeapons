using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-252 stage 5: minimal mutable reference-type wrapper for a single value, so a value type
/// (e.g. an int turn counter) can serve as <see cref="WielderKeyedStore{TState}"/>'s TState
/// (which requires `class`). Two callers holding the SAME Box instance (returned by the SAME
/// store for the SAME wielder) mutate the SAME boxed value in place.
/// </summary>
internal sealed class Box<T>
{
    public T Value = default!;
}

/// <summary>
/// LW-252 stage 5: per-wielder state keyed on roster nameId, with (level,brave,faith) fingerprint
/// as a stable fallback lane -- WITHOUT ever letting a key rotate for one unit within a battle.
/// Ports the fp-only keying every per-wielder signature hold (Iai's _holds, Larceny's
/// _byWielder, Mushin's _armed/_prevTurnFlag, TurnTracker's _turns) used before this stage,
/// which shared state between fp-TWIN units (documented limitation, e.g. Iai.cs's old class doc).
///
/// THE HAZARD THIS GUARDS AGAINST (the repo's historical FAIL-4 double-arm class): Mem reads
/// fail-safe to 0 on transients (an unmapped page, a mid-tick engine rebuild), so a naive
/// per-tick "key = nameId if nameId else fp" derivation would ROTATE a unit's key the instant its
/// nameId read glitches to 0 for one tick -- re-arming/re-creating state that should have
/// persisted. This store never rotates a key once assigned: a state's identity (ArmNameId) is
/// fixed at creation, and every later probe either finds it by name, finds it by its own
/// original fp slot, or recovers it via the fp side map -- never by re-deriving a fresh key.
///
/// THREE CONTAINERS (byName / byFp / the fp side map) hold entries, never state objects
/// directly, so an entry can be reachable from BOTH byName and byFp (the alias case) while still
/// being ONE object. NOTHING is ever removed mid-battle (no per-tick churn risk, no removal race)
/// -- <see cref="Clear"/> is the only way anything leaves, wired to each owner's ResetBattle.
///
/// <see cref="Probe"/> (read-only, never creates) and <see cref="GetOrCreate"/> (creates on a
/// genuine first sight) share ONE resolution law:
///   nameId &gt; 0: byName[nameId] hit -&gt; that entry. Miss -&gt; byFp[fp] hit (such entries always
///     have ArmNameId == 0 by construction) -&gt; ALIAS: byName[nameId] now ALSO points at that SAME
///     entry (the byFp slot is left in place too, but that retention alone is REDUNDANT -- the fp
///     side map, populated once at this entry's creation and never touched by alias-adopt, is the
///     actual survivor that lets a LATER nameId-0 transient recover it; see the "nameId == 0"
///     branch below).
///     Miss both -&gt; GetOrCreate creates a fresh entry (ArmNameId = nameId) under byName + the fp
///     side map; Probe returns null.
///   nameId == 0: byFp[fp] hit -&gt; that entry. Miss -&gt; consult the fp side map: exactly ONE
///     entry registered under this fp -&gt; return it (TRANSIENT RECOVERY for a name-keyed unit
///     whose nameId read glitched to 0 this one tick). TWO OR MORE entries registered -&gt; return
///     null: an ambiguous nameId-0 tick for a fp shared by multiple SEEDED units must SKIP this
///     tick entirely (no create, no adopt) -- it can never safely guess which twin it is, and
///     guessing wrong would steal or corrupt a DIFFERENT unit's state. ZERO registered -&gt;
///     GetOrCreate creates a fresh entry (ArmNameId = 0) under byFp + the side map; Probe null.
///
/// BEHAVIORAL CONSEQUENCE (deliberate, matches "master" for the case it can't improve): a MIXED
/// fp-twin pair where at least one member never resolves a nameId shares the SAME byFp-created
/// state -- identical to every pre-stage-5 signature's fp-only dictionary. A SEEDED pair (both
/// members' nameId reads resolve, even to different values) gets fully INDEPENDENT states -- the
/// actual fix. TState must be a reference type (mutated in place across ticks by every consumer,
/// mirroring the fp-keyed dictionaries this replaces).
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
    // Every entry this store has ever created, indexed by its OWN fp (added exactly once, at
    // creation, regardless of how many containers later come to reference it via alias-adopt).
    // Flattening this is also how <see cref="All"/> enumerates every distinct live state exactly
    // once (see its own doc).
    private readonly Dictionary<(int lvl, int br, int fa), List<Entry>> _byFpSide = new();

    /// <summary>Read-only lookup: never creates. Returns null on a genuine miss OR on the
    /// ambiguous nameId-0-shared-by-2+-seeded-units case (see the class doc's resolution law) --
    /// both mean "SKIP this tick", so callers that only ever read (e.g. GrowthEngine.Mushin.cs's
    /// stack-count peek) don't need to distinguish them.</summary>
    public TState? Probe(int nameId, (int lvl, int br, int fa) fp) => Resolve(nameId, fp, factory: null);

    /// <summary>Find-or-create: same resolution law as <see cref="Probe"/>, except the two ZERO-
    /// registered cases (a genuinely new nameId, or a genuinely new fp with no prior 0-keyed
    /// entry) create a fresh state via <paramref name="factory"/> instead of returning null.
    /// STILL returns null for the ambiguous nameId-0-shared-by-2+ case -- creating a THIRD state
    /// for an unresolvable identity would be exactly the kind of corruption this store exists to
    /// prevent, so GetOrCreate's create power never overrides that refusal.</summary>
    public TState? GetOrCreate(int nameId, (int lvl, int br, int fa) fp, Func<TState> factory) =>
        Resolve(nameId, fp, factory);

    private TState? Resolve(int nameId, (int lvl, int br, int fa) fp, Func<TState>? factory)
    {
        if (nameId > 0)
        {
            if (_byName.TryGetValue(nameId, out var byName)) return byName.State;
            if (_byFp.TryGetValue(fp, out var byFp))
            {
                _byName[nameId] = byFp;   // alias-adopt: byFp entry STAYS, byName now ALSO resolves it
                return byFp.State;
            }
            if (factory == null) return null;   // Probe: miss
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
                return null;                                  // 2+: ambiguous, SKIP (never guess)
            }
            if (factory == null) return null;   // Probe: miss
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

    /// <summary>Every distinct live state this store has ever created, exactly once each,
    /// paired with its OWN fp and ArmNameId (the identity captured at creation) -- flattens the
    /// fp side map, which is the ONE container every entry is guaranteed to appear in exactly
    /// once regardless of later alias-adopt. Consumers that must sweep every held wielder
    /// (Iai's release pass, LarcenyHoldings' Drive/Expire/ReleaseAll) iterate this rather than a
    /// raw dictionary.</summary>
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
