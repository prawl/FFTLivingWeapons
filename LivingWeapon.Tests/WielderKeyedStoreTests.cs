using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-252 stage 5, Part A: WielderKeyedStore's resolution law, driven directly (no game fakes
/// needed -- the store has no memory dependency at all). Each test's name states the exact
/// scenario from the store's own class doc; see it for the full law.
/// </summary>
public class WielderKeyedStoreTests
{
    private static readonly (int lvl, int br, int fa) Fp = (30, 65, 60);
    private const int NameA = 501, NameB = 502;

    private sealed class State { public int Tag; }

    [Fact]
    public void Transient_zero_then_n_then_zero_never_rotates_the_state_object()
    {
        // [Sabotage note: replacing alias-adopt with remove+reinsert must red this test]
        var store = new WielderKeyedStore<State>();

        var atZero = store.GetOrCreate(0, Fp, () => new State { Tag = 1 });   // tick 1: nameId unseeded
        var atName = store.GetOrCreate(NameA, Fp, () => new State { Tag = 2 }); // tick 2: nameId now readable
        var backAtZero = store.Probe(0, Fp);                                  // tick 3: nameId transient back to 0

        Assert.Same(atZero, atName);
        Assert.Same(atZero, backAtZero);
        Assert.Single(store.All);   // no second state was ever created
        Assert.Same(atZero, store.Probe(NameA, Fp));   // the alias survives: byName[n] resolves it too
    }

    [Fact]
    public void N_then_zero_then_n_recovers_via_the_fp_side_map()
    {
        var store = new WielderKeyedStore<State>();

        var atName1 = store.GetOrCreate(NameA, Fp, () => new State());   // created under byName, not byFp
        var atZero = store.Probe(0, Fp);                                 // byFp miss -> side-map recovery
        var atName2 = store.GetOrCreate(NameA, Fp, () => new State());   // byName hit directly

        Assert.Same(atName1, atZero);
        Assert.Same(atName1, atName2);
        Assert.Single(store.All);
    }

    [Fact]
    public void Mixed_pair_with_a_genuinely_unseeded_member_shares_one_state()
    {
        // Master's exact behavior for this pair: neither unit ever presents a nameId, so the
        // store cannot tell them apart -- both probes at nameId 0 land on the SAME byFp state.
        var store = new WielderKeyedStore<State>();

        var unitA = store.GetOrCreate(0, Fp, () => new State());
        var unitB = store.GetOrCreate(0, Fp, () => new State());

        Assert.Same(unitA, unitB);
        Assert.Single(store.All);
    }

    [Fact]
    public void Seeded_pair_at_the_same_fp_gets_independent_states()
    {
        // THE FIX: two units sharing a fingerprint but each with their OWN readable nameId no
        // longer collide -- unlike the mixed-pair case above.
        var store = new WielderKeyedStore<State>();

        var unitA = store.GetOrCreate(NameA, Fp, () => new State { Tag = 1 });
        var unitB = store.GetOrCreate(NameB, Fp, () => new State { Tag = 2 });

        Assert.NotSame(unitA, unitB);
        Assert.Equal(1, unitA.Tag);
        Assert.Equal(2, unitB.Tag);
        Assert.Equal(2, store.All.Count());
    }

    [Fact]
    public void Seeded_twin_transient_zero_tick_skips_creates_nothing_steals_nothing()
    {
        var store = new WielderKeyedStore<State>();
        var unitA = store.GetOrCreate(NameA, Fp, () => new State());
        var unitB = store.GetOrCreate(NameB, Fp, () => new State());

        var probed = store.Probe(0, Fp);
        var gotten = store.GetOrCreate(0, Fp, () => new State());   // GetOrCreate's create power does NOT override the refusal

        Assert.Null(probed);
        Assert.Null(gotten);
        Assert.Equal(2, store.All.Count());          // nothing created
        Assert.Same(unitA, store.Probe(NameA, Fp));   // neither existing state was disturbed
        Assert.Same(unitB, store.Probe(NameB, Fp));
    }

    // ---- supporting coverage: Probe never creates; Clear empties all three containers ----

    [Fact]
    public void Probe_never_creates_on_a_genuine_miss()
    {
        var store = new WielderKeyedStore<State>();
        Assert.Null(store.Probe(NameA, Fp));
        Assert.Null(store.Probe(0, Fp));
        Assert.Empty(store.All);
    }

    [Fact]
    public void Clear_empties_every_container()
    {
        var store = new WielderKeyedStore<State>();
        store.GetOrCreate(NameA, Fp, () => new State());
        store.GetOrCreate(0, (1, 2, 3), () => new State());

        store.Clear();

        Assert.Empty(store.All);
        Assert.Null(store.Probe(NameA, Fp));
        Assert.Null(store.Probe(0, (1, 2, 3)));
        // A fresh GetOrCreate after Clear must not accidentally alias anything from before.
        var fresh = store.GetOrCreate(NameA, Fp, () => new State { Tag = 9 });
        Assert.Equal(9, fresh!.Tag);
    }

    [Fact]
    public void Different_fingerprints_never_share_a_state_even_at_nameId_zero()
    {
        var store = new WielderKeyedStore<State>();
        var atFp1 = store.GetOrCreate(0, Fp, () => new State());
        var atFp2 = store.GetOrCreate(0, (99, 1, 1), () => new State());
        Assert.NotSame(atFp1, atFp2);
    }
}
