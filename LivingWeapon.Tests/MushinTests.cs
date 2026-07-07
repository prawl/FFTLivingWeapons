using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Kiku-ichimonji's "Mushin" signature: a full WAIT turn (no move, no action) banks one stack
/// (up to Tuning.MushinMaxStacks); the wielder's next attack spends every banked stack in one
/// boosted hit, then the buff clears.
///
/// HYBRID (2026-07-07): the ARM WINDOW (turn-start snapshot, move sampling, the falling-edge arm
/// decision) rides Offsets.ActorPtr's full fingerprint (Band.ActorEntry), so these tests drive it
/// with PointActorAt/PointActorNowhere (the Iai/Puppeteer pattern). ACT + CONSUME still rides the
/// TurnQueue fingerprint (Band.ActiveOwner), so those tests keep seeding it with SeatActive/
/// SeatActiveNobody (the CounterAttributionTests.SetActive pattern). Live testing on the prior
/// TurnQueue-only window (2026-07-07) showed the arm lagging turns behind: Band.ActiveOwner
/// follows the cursor and bails on ambiguity (two same-level units), which starves a turn-span
/// window but is fine for the instant of an action.
///
/// LOAD-BEARING tests: Arm_banksViaActorPtr_whenTurnQueueCannotResolveWielder (proves the arm now
/// rides ActorPtr, not TurnQueue: it banks 0 on the pre-fix code and 1 after), MoveOnlyTurn_doesNotArm
/// / AttackTurn_doesNotArm (the stacking negatives), Stacking_ThreeWaits_CapsAtThree_OneAttackSpendsAll
/// (the AC itself), and Consume_survives_whenCollidingNameIdEnemyActs (the TurnQueue consume must
/// still ignore a nameId-colliding enemy).
/// </summary>
public class MushinTests
{
    private const int KikuId = 45;
    private const int OtherWeaponId = 56;

    private static (Mushin mushin, FakeSparseMemory mem, long wielderEntry, (int lvl, int br, int fa) fp,
                    Dictionary<(int lvl, int br, int fa), int> armed)
        Build(int kills = -1, int wielderSlot = 24, int gx = 2, int gy = 2, int nameId = 0)
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [KikuId] = new WeaponMeta
            {
                Name = "Kiku-ichimonji", Wp = 13, Cat = "Katana", Formula = 2,
                Flavor = "chrysanthemum-hilt blade",
                Signature = new WeaponSignature { AtTier = Tuning.MushinAtTier, Mushin = true, DisplayLabel = "Mushin" }
            }
        };
        var killDict = new Dictionary<int, int> { [KikuId] = kills >= 0 ? kills : Tuning.ProdThresholds[2] };

        var fp = (lvl: 30, br: 65, fa: 60);
        long wielder = Band.Entry(wielderSlot);

        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: KikuId, nameId: nameId);
        MemSeats.SeatBand(mem, wielderSlot, weapon: KikuId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: gx, gy: gy, hp: 200, maxHp: 300);

        var armed = new Dictionary<(int lvl, int br, int fa), int>();
        var mushin = new Mushin(meta, killDict, armed, mem);
        return (mushin, mem, wielder, fp, armed);
    }

    /// <summary>Seat the TurnQueue as naming a unit with this maxHp/hp/level as the active owner
    /// (the CounterAttributionTests.SetActive pattern). Band.ActiveOwner also needs a band entry
    /// whose OWN maxHp/hp/level match (MemSeats.SeatBand), so this only names units already
    /// seated that way; the Build() wielder is seated hp:200 maxHp:300.</summary>
    private static void SeatActive(FakeSparseMemory mem, int hp, int maxHp, int level, int team = 0)
    {
        mem.U16s[Offsets.TurnQueue + Offsets.TqTeam]  = (ushort)team;
        mem.U16s[Offsets.TurnQueue + Offsets.TqHp]    = (ushort)hp;
        mem.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        mem.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
    }

    /// <summary>Un-name every unit as active: Band.ActiveOwner's own garbage guard (maxHp 0)
    /// closes every wielder's window, the TurnQueue analog of pointing ActorPtr nowhere.</summary>
    private static void SeatActiveNobody(FakeSparseMemory mem) =>
        mem.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 0;

    /// <summary>Point Offsets.ActorPtr at <paramref name="entry"/>'s combat frame (the inverse of
    /// Band.ActorEntry: frame = entry - BandEntry). Drives the ARM WINDOW (Iai/Puppeteer pattern).</summary>
    private static void PointActorAt(FakeSparseMemory mem, long entry) =>
        mem.SeedU64(Offsets.ActorPtr, (ulong)(entry - Offsets.BandEntry));

    /// <summary>Clear the pointer so Band.ActorEntry resolves to nothing, closing every window.</summary>
    private static void PointActorNowhere(FakeSparseMemory mem) =>
        mem.SeedU64(Offsets.ActorPtr, 0);

    private static void SetActed(FakeSparseMemory mem, int v) => mem.U8s[Offsets.Acted] = (byte)v;

    private static int StacksOf(Dictionary<(int lvl, int br, int fa), int> armed, (int, int, int) fp)
        => armed.TryGetValue(fp, out int s) ? s : 0;

    // ---- Gates ----

    [Fact]
    public void Gate_offField_no_change()
    {
        var (mushin, _, _, fp, armed) = Build();
        mushin.Tick(onField: false);
        Assert.Equal(0, StacksOf(armed, fp));
        Assert.Empty(armed);
    }

    [Fact]
    public void Gate_missing_meta_no_crash()
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>();
        var kills = new Dictionary<int, int> { [KikuId] = Tuning.ProdThresholds[2] };
        var armed = new Dictionary<(int, int, int), int>();
        var mushin = new Mushin(meta, kills, armed, mem);

        mushin.Tick(onField: true);

        Assert.Empty(armed);
    }

    [Fact]
    public void Gate_null_signature_no_crash()
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [KikuId] = new WeaponMeta { Name = "Kiku-ichimonji", Wp = 13, Cat = "Katana", Formula = 2, Signature = null }
        };
        var kills = new Dictionary<int, int> { [KikuId] = Tuning.ProdThresholds[2] };
        var armed = new Dictionary<(int, int, int), int>();
        var mushin = new Mushin(meta, kills, armed, mem);

        mushin.Tick(onField: true);

        Assert.Empty(armed);
    }

    [Fact]
    public void Gate_belowTier_no_arm()
    {
        var (mushin, mem, wielder, fp, armed) = Build(kills: Tuning.ProdThresholds[1]);   // tier 2 < AtTier 3

        mushin.Tick(onField: true);          // prime
        PointActorAt(mem, wielder);
        mushin.Tick(onField: true);          // arrival would open the window at tier 3+
        PointActorNowhere(mem);
        mushin.Tick(onField: true);          // departure would arm at tier 3+

        Assert.True(StacksOf(armed, fp) == 0, "below AtTier must never arm");
    }

    // ---- Priming (F5, Iai's contract) ----

    [Fact]
    public void FirstTick_staleEqual_doesNotFalseArmOrConsume()
    {
        // Both signals already name the wielder on the VERY FIRST evaluated tick (no arrival
        // transition ever observed for either). Prove neither an arm nor a consume fires.
        var (mushin, mem, wielder, fp, armed) = Build();
        PointActorAt(mem, wielder);
        SeatActive(mem, hp: 200, maxHp: 300, level: fp.lvl);
        SetActed(mem, 1);

        mushin.Tick(onField: true);   // priming tick: must not decide anything
        Assert.Equal(0, StacksOf(armed, fp));

        mushin.Tick(onField: true);   // still no edge (both signals unchanged, Acted stayed 1)
        Assert.Equal(0, StacksOf(armed, fp));
    }

    // ---- Arm/stack: the non-vacuous positive + negatives ----

    [Fact]
    public void FullWaitTurn_banksOneStack()
    {
        var (mushin, mem, wielder, fp, armed) = Build(gx: 2, gy: 2);

        mushin.Tick(onField: true);           // prime (ActorPtr unseeded -> resolves nowhere)
        PointActorAt(mem, wielder);            // arrival: turn start
        mushin.Tick(onField: true);
        mushin.Tick(onField: true);            // idle tick: no move, no act
        PointActorNowhere(mem);                 // departure: turn end
        mushin.Tick(onField: true);

        Assert.Equal(1, StacksOf(armed, fp));
    }

    [Fact]
    public void MoveOnlyTurn_doesNotArm()
    {
        // THE NON-VACUOUS NEGATIVE: an implementation that arms on turn-end regardless of the
        // move/act signals would wrongly arm here.
        var (mushin, mem, wielder, fp, armed) = Build(gx: 2, gy: 2);

        mushin.Tick(onField: true);           // prime
        PointActorAt(mem, wielder);            // arrival, snapshots (gx=2,gy=2)
        mushin.Tick(onField: true);
        mem.U8s[wielder + Offsets.AGx] = 5;    // the wielder moved
        mushin.Tick(onField: true);
        PointActorNowhere(mem);                 // departure
        mushin.Tick(onField: true);

        Assert.Equal(0, StacksOf(armed, fp));
    }

    [Fact]
    public void AttackTurn_doesNotArm()
    {
        var (mushin, mem, wielder, fp, armed) = Build();

        mushin.Tick(onField: true);           // prime
        PointActorAt(mem, wielder);            // ActorPtr window opens
        SeatActive(mem, hp: 200, maxHp: 300, level: fp.lvl);   // TurnQueue also names the wielder
        mushin.Tick(onField: true);
        SetActed(mem, 1);                       // the wielder's own acted edge: an attack
        mushin.Tick(onField: true);
        PointActorNowhere(mem);                 // departure
        SeatActiveNobody(mem);                  // the TurnQueue moves on too
        mushin.Tick(onField: true);

        Assert.Equal(0, StacksOf(armed, fp));
    }

    [Fact]
    public void MoveOnlyTurn_afterStacksBanked_doesNotResetBankedStacks()
    {
        // A move-only turn withholds the NEW stack, but must not discard the stacks already
        // banked from earlier full-wait turns (only an attack ever clears them).
        var (mushin, mem, wielder, fp, armed) = Build(gx: 2, gy: 2);
        armed[fp] = 2;   // pre-banked, as if two prior full-wait turns charged it

        mushin.Tick(onField: true);           // prime
        PointActorAt(mem, wielder);            // arrival, snapshots (gx=2,gy=2)
        mushin.Tick(onField: true);
        mem.U8s[wielder + Offsets.AGx] = 5;    // the wielder moved (no attack)
        mushin.Tick(onField: true);
        PointActorNowhere(mem);                 // departure
        mushin.Tick(onField: true);

        Assert.True(StacksOf(armed, fp) == 2, "a move-only turn must not add a stack, but must not clear banked ones either");
    }

    // ---- Stacking (the AC itself) ----

    [Fact]
    public void Stacking_ThreeWaits_CapsAtThree_ThenOneAttackConsumesAll()
    {
        var (mushin, mem, wielder, fp, armed) = Build();

        mushin.Tick(onField: true);           // prime

        for (int i = 1; i <= 3; i++)
        {
            PointActorAt(mem, wielder);
            mushin.Tick(onField: true);
            PointActorNowhere(mem);
            mushin.Tick(onField: true);
            Assert.Equal(i, StacksOf(armed, fp));
        }

        // A fourth wait must not overflow past the cap.
        PointActorAt(mem, wielder);
        mushin.Tick(onField: true);
        PointActorNowhere(mem);
        mushin.Tick(onField: true);
        Assert.Equal(3, StacksOf(armed, fp));

        // ONE attack spends every banked stack in a single hit.
        PointActorAt(mem, wielder);
        SeatActive(mem, hp: 200, maxHp: 300, level: fp.lvl);
        mushin.Tick(onField: true);
        SetActed(mem, 1);
        mushin.Tick(onField: true);
        Assert.Equal(0, StacksOf(armed, fp));
    }

    // ---- Consume ----

    [Fact]
    public void Armed_ConsumedByNextOwnTurnAttack()
    {
        var (mushin, mem, wielder, fp, armed) = Build();
        armed[fp] = 1;   // pre-armed, as if a prior full-wait turn charged it

        mushin.Tick(onField: true);           // prime
        SeatActive(mem, hp: 200, maxHp: 300, level: fp.lvl);   // the wielder's next turn begins
        mushin.Tick(onField: true);
        SetActed(mem, 1);                       // the attack lands: own acted edge inside the window
        mushin.Tick(onField: true);

        Assert.True(StacksOf(armed, fp) == 0, "the wielder's own attack must consume every banked stack");
    }

    [Fact]
    public void Armed_SurvivesActedEdgeOnAnEnemyTurn()
    {
        // Negative consume: an Acted edge that fires while the TurnQueue names a DIFFERENT unit
        // (an enemy's turn) must NOT consume the buff.
        var (mushin, mem, wielder, fp, armed) = Build();
        armed[fp] = 2;

        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId, lvl: 20, br: 50, fa: 50,
                          gx: 5, gy: 5, hp: 100, maxHp: 100);

        mushin.Tick(onField: true);           // prime
        SeatActive(mem, hp: 100, maxHp: 100, level: 20, team: 1);   // an enemy's turn
        SetActed(mem, 1);                       // acted edge fires while the TurnQueue names the ENEMY
        mushin.Tick(onField: true);

        Assert.True(armed[fp] == 2, "an acted edge outside the wielder's own turn window must not consume");
    }

    // ---- ResetBattle ----

    [Fact]
    public void ResetBattle_clears_shared_armed_dict()
    {
        var (mushin, _, _, fp, armed) = Build();
        armed[fp] = 3;

        mushin.ResetBattle();

        Assert.Empty(armed);
    }

    // ---- Gate: offhand-only wielder never arms (main-hand only, mirrors Iai) ----

    [Fact]
    public void Gate_offhand_only_never_arms()
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [KikuId] = new WeaponMeta
            {
                Name = "Kiku-ichimonji", Wp = 13, Cat = "Katana", Formula = 2,
                Signature = new WeaponSignature { AtTier = Tuning.MushinAtTier, Mushin = true, DisplayLabel = "Mushin" }
            }
        };
        var kills = new Dictionary<int, int> { [KikuId] = Tuning.ProdThresholds[2] };
        var fp = (lvl: 30, br: 65, fa: 60);

        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: OtherWeaponId, oh: KikuId);
        MemSeats.SeatBand(mem, 24, weapon: OtherWeaponId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 2, gy: 2, hp: 200, maxHp: 300);
        long wielder = Band.Entry(24);

        var armed = new Dictionary<(int lvl, int br, int fa), int>();
        var mushin = new Mushin(meta, kills, armed, mem);

        mushin.Tick(onField: true);
        PointActorAt(mem, wielder);
        mushin.Tick(onField: true);
        PointActorNowhere(mem);
        mushin.Tick(onField: true);

        Assert.True(StacksOf(armed, fp) == 0, "offhand-only wielder must never arm Mushin");
    }

    // ---- NON-VACUITY: load-bearing tests for the ActorPtr-window / TurnQueue-consume split ----

    [Fact]
    public void Arm_banksViaActorPtr_whenTurnQueueCannotResolveWielder()
    {
        // THE LOAD-BEARING TEST for this change. Reproduce the live bug directly: a second
        // real-position band entry shares the wielder's maxHp+hp+level but a DIFFERENT
        // brave/faith, so Band.ActiveOwner's ambiguity guard bails (distinct fingerprints;
        // "miss beats mis-credit") even while the TurnQueue is actively trying to name the
        // wielder's own turn. On the pre-fix TurnQueue-gated window this leaves namesWielder
        // false for the whole turn, so the window never opens and a full wait banks 0. The
        // ActorPtr window sidesteps Band.ActiveOwner entirely, so the same full wait banks 1
        // once the arm rides ActorPtr instead.
        var (mushin, mem, wielder, fp, armed) = Build();
        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId, lvl: fp.lvl, br: 70, fa: 70,
                          gx: 5, gy: 5, hp: 200, maxHp: 300);

        mushin.Tick(onField: true);           // prime
        PointActorAt(mem, wielder);            // arrival, purely via ActorPtr
        SeatActive(mem, hp: 200, maxHp: 300, level: fp.lvl);   // the TurnQueue tries to name the wielder...
        mushin.Tick(onField: true);            // ...and bails (ambiguous), so tqNamesWielder is false
        mushin.Tick(onField: true);            // idle tick: no move, no act
        PointActorNowhere(mem);                 // departure, purely via ActorPtr
        mushin.Tick(onField: true);

        Assert.Equal(1, StacksOf(armed, fp));
    }

    [Fact]
    public void Consume_survives_whenCollidingNameIdEnemyActs()
    {
        // An enemy that SHARES the wielder's roster nameId (the old actingNameId == w.NameId
        // check's exact collision class: generic units reuse small nameId values) but carries a
        // DIFFERENT maxHp/level. Band.ActiveOwner never looks at nameId at all, so naming this
        // enemy as the TurnQueue's active owner must leave the wielder's banked stacks untouched.
        // (The old nameId-identity code consumed here.)
        var (mushin, mem, wielder, fp, armed) = Build(nameId: 1);
        armed[fp] = 2;   // pre-banked

        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId, lvl: 20, br: 50, fa: 50,
                          gx: 5, gy: 5, hp: 100, maxHp: 100);
        MemSeats.SeatFrameNameId(mem, 0, 1);   // same nameId as the wielder: decorative, irrelevant to the new trigger

        mushin.Tick(onField: true);           // prime
        SeatActive(mem, hp: 100, maxHp: 100, level: 20, team: 1);   // the TurnQueue names the ENEMY
        SetActed(mem, 1);                       // acted edge fires while the enemy is the active owner
        mushin.Tick(onField: true);

        Assert.True(armed[fp] == 2, "an acted edge naming a colliding-nameId ENEMY must not consume the wielder's stacks");
    }

    [Fact]
    public void Consume_survives_whenSameMaxHpLevelDifferentFpUnitActs()
    {
        // A second unit deployed alongside the wielder sharing the SAME maxHp/hp/level (so the
        // TurnQueue fingerprint alone cannot tell them apart) but a DIFFERENT brave/faith:
        // Band.ActiveOwner must bail as ambiguous rather than guess, so nobody's window is named
        // and the wielder's banked stacks survive.
        var (mushin, mem, wielder, fp, armed) = Build();
        armed[fp] = 1;   // pre-banked

        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId, lvl: fp.lvl, br: 70, fa: 70,
                          gx: 5, gy: 5, hp: 200, maxHp: 300);

        mushin.Tick(onField: true);           // prime
        SeatActive(mem, hp: 200, maxHp: 300, level: fp.lvl);   // ambiguous: two real entries match
        SetActed(mem, 1);
        mushin.Tick(onField: true);

        Assert.True(armed[fp] == 1, "an ambiguous TurnQueue match must bail rather than guess, leaving banked stacks untouched");
    }

    [Fact]
    public void MirrorChurn_consumeFiltersTwinViaTurnQueue_armStillBanksViaActorPtr()
    {
        // A frozen (0,0) twin at another slot, sharing the wielder's exact fingerprint and
        // maxHp/hp: Band.ActiveOwner's twin filter must prefer the REAL wielder entry, so the
        // TurnQueue consume edge still fires correctly. The ActorPtr window never needed to
        // disambiguate the twin at all (it addresses one specific entry directly), so a genuine
        // full-wait arm afterward still banks regardless of the twin's presence.
        var (mushin, mem, wielder, fp, armed) = Build();
        MemSeats.SeatBand(mem, 20, weapon: KikuId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 0, gy: 0, hp: 200, maxHp: 300);

        armed[fp] = 1;   // pre-banked, so this pass exercises the consume edge first

        mushin.Tick(onField: true);           // prime
        SeatActive(mem, hp: 200, maxHp: 300, level: fp.lvl);   // the twin filter resolves the REAL entry
        mushin.Tick(onField: true);
        SetActed(mem, 1);                       // the attack lands
        mushin.Tick(onField: true);

        Assert.True(StacksOf(armed, fp) == 0, "the twin filter must resolve the REAL entry so the attack still consumes");

        // Close the attacking turn, then run a genuine full-wait turn purely via ActorPtr.
        PointActorNowhere(mem);
        SeatActiveNobody(mem);                  // the TurnQueue moves on too
        mushin.Tick(onField: true);            // no window was open, so this is a no-op
        Assert.Equal(0, StacksOf(armed, fp));

        PointActorAt(mem, wielder);
        mushin.Tick(onField: true);            // arrival
        mushin.Tick(onField: true);            // idle: no move, no act
        PointActorNowhere(mem);
        mushin.Tick(onField: true);            // departure: full wait banks a stack

        Assert.Equal(1, StacksOf(armed, fp));
    }
}
