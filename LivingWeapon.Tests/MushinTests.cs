using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Kiku-ichimonji's "Mushin" signature: a full WAIT turn (no move, no action) banks one stack
/// (up to Tuning.MushinMaxStacks); the wielder's next attack spends every banked stack in one
/// boosted hit, then the buff clears.
///
/// Turn-window apparatus mirrors Iai.cs's proven ActorPtr arrival/departure (Band.ActorEntry),
/// HARDENED (2026-07-07) the same way Iai's v2 rebuild was: arrival, departure, and the own-
/// acted-edge all identity-match the acting entry's frame nameId (Offsets.ANameId) against each
/// wielder's roster-nameId capture (Wielder.RosterNameId, taken once when a wielder's window is
/// first created) INSTEAD OF comparing the located wielder's band-entry ADDRESS. This fixes two
/// live glitches the address compare was exposed to (the engine mirrors a wielder at a second
/// identical combat slot, confirmed live at slots 24/28): a wait sometimes false-consuming the
/// buff, and an attack sometimes failing to consume it. A wielder whose nameId capture fails
/// (Wielder.RosterNameId returns &lt;= 0) falls back to the original address compare, degraded
/// like Iai's fallback, never worse. The armed dictionary lives SHARED with GrowthEngine
/// (constructor-injected here, exactly as production wires it in Engine.cs), keyed by wielder
/// fingerprint (lvl,br,fa), and now holds a STACK COUNT (0..3) instead of a bool.
///
/// LOAD-BEARING tests: MoveOnlyTurn_doesNotArm / AttackTurn_doesNotArm (the stacking negatives),
/// Stacking_ThreeWaits_CapsAtThree_OneAttackSpendsAll (the AC itself), and the two
/// MirrorChurn_* tests (the nameId-hardening rebuild -- address-only code fails both).
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
        if (nameId != 0) MemSeats.SeatFrameNameId(mem, wielderSlot, nameId);

        var armed = new Dictionary<(int lvl, int br, int fa), int>();
        var mushin = new Mushin(meta, killDict, armed, mem);
        return (mushin, mem, wielder, fp, armed);
    }

    /// <summary>Point Offsets.ActorPtr at <paramref name="bandEntry"/>'s combat frame (the
    /// inverse of Band.ActorEntry: frame = bandEntry - BandEntry).</summary>
    private static void PointActorAt(FakeSparseMemory mem, long bandEntry) =>
        mem.SeedU64(Offsets.ActorPtr, (ulong)(bandEntry - Offsets.BandEntry));

    private static void PointActorNowhere(FakeSparseMemory mem) => mem.SeedU64(Offsets.ActorPtr, 0);

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
        // The pointer already equals the wielder's entry on the VERY FIRST evaluated tick (no
        // arrival transition ever observed). A naive implementation that doesn't prime prevActing
        // would treat this as an immediate "departure never happened yet" no-op, but ALSO risks a
        // false decision if prevActed isn't primed together (Iai's I2b bug). Prove neither fires.
        var (mushin, mem, wielder, fp, armed) = Build();
        PointActorAt(mem, wielder);
        SetActed(mem, 1);

        mushin.Tick(onField: true);   // priming tick: must not decide anything
        Assert.Equal(0, StacksOf(armed, fp));

        mushin.Tick(onField: true);   // still no edge (pointer stayed equal, Acted stayed 1)
        Assert.Equal(0, StacksOf(armed, fp));
    }

    // ---- Arm/stack: the non-vacuous positive + negatives ----

    [Fact]
    public void FullWaitTurn_banksOneStack()
    {
        var (mushin, mem, wielder, fp, armed) = Build(gx: 2, gy: 2);

        mushin.Tick(onField: true);           // prime (pointer unseeded -> 0)
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
        PointActorAt(mem, wielder);            // arrival
        mushin.Tick(onField: true);
        SetActed(mem, 1);                       // the wielder's own acted edge: an attack
        mushin.Tick(onField: true);
        PointActorNowhere(mem);                 // departure
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

        mushin.Tick(onField: true);           // prime (pointer unseeded -> 0)
        PointActorAt(mem, wielder);            // the wielder's next turn begins
        mushin.Tick(onField: true);
        SetActed(mem, 1);                       // the attack lands: own acted edge inside the window
        mushin.Tick(onField: true);

        Assert.True(StacksOf(armed, fp) == 0, "the wielder's own attack must consume every banked stack");
    }

    [Fact]
    public void Armed_SurvivesActedEdgeOnAnEnemyTurn()
    {
        // Negative consume: an Acted edge that fires while the pointer names a DIFFERENT unit
        // (an enemy's turn, or the wielder merely reacting) must NOT consume the buff.
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

        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: KikuId);
        MemSeats.SeatBand(mem, 24, weapon: KikuId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 2, gy: 2, hp: 200, maxHp: 300);
        long enemyEntry = Band.Entry(0);
        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId, lvl: 20, br: 50, fa: 50,
                          gx: 5, gy: 5, hp: 100, maxHp: 100);

        var armed = new Dictionary<(int lvl, int br, int fa), int> { [fp] = 2 };
        var mushin = new Mushin(meta, kills, armed, mem);

        mushin.Tick(onField: true);           // prime (pointer unseeded -> 0)
        PointActorAt(mem, enemyEntry);         // an enemy's turn
        SetActed(mem, 1);                       // acted edge fires while the pointer names the ENEMY
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
        long wielder = Band.Entry(24);
        MemSeats.SeatBand(mem, 24, weapon: OtherWeaponId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 2, gy: 2, hp: 200, maxHp: 300);

        var armed = new Dictionary<(int lvl, int br, int fa), int>();
        var mushin = new Mushin(meta, kills, armed, mem);

        mushin.Tick(onField: true);
        PointActorAt(mem, wielder);
        mushin.Tick(onField: true);
        PointActorNowhere(mem);
        mushin.Tick(onField: true);

        Assert.True(StacksOf(armed, fp) == 0, "offhand-only wielder must never arm Mushin");
    }

    // ---- MIRROR CHURN (nameId identity hardening, the two LOAD-BEARING tests for change 2) ----
    //
    // Root cause (per the rework spec, matching Iai's proven mirror-churn diagnosis): the engine
    // mirrors a wielder at a second identical combat slot. Wielder.Locate's deterministic tie-
    // break (both candidates share the same frame nameId) returns whichever slot is scanned
    // FIRST -- here the MIRROR at a lower slot index -- while the real ActorPtr always names the
    // REAL frame. An address compare (acting == entry) then never matches even during the
    // wielder's own turn: arrival never opens the window, so neither the arm nor the consume edge
    // can ever fire. Identity-matching the acting frame's nameId against the wielder's captured
    // roster nameId sidesteps the address mismatch entirely.

    private static (FakeSparseMemory mem, long realEntry, long mirrorEntry, (int lvl, int br, int fa) fp,
                    Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills)
        BuildMirrored(int nameId = 298)
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

        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: KikuId, nameId: nameId);

        // The REAL wielder: band slot 24 (the actor pointer will name this frame).
        long realEntry = Band.Entry(24);
        MemSeats.SeatBand(mem, 24, weapon: KikuId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 2, gy: 2, hp: 200, maxHp: 300);
        MemSeats.SeatFrameNameId(mem, 24, nameId);

        // A MIRROR at a LOWER slot index (scanned first by Wielder.Locate), same weapon/fp/nameId,
        // a real position -- Locate's homogeneous tie-break deterministically returns THIS one.
        long mirrorEntry = Band.Entry(20);
        MemSeats.SeatBand(mem, 20, weapon: KikuId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 9, gy: 9, hp: 200, maxHp: 300);
        MemSeats.SeatFrameNameId(mem, 20, nameId);

        return (mem, realEntry, mirrorEntry, fp, meta, kills);
    }

    [Fact]
    public void MirrorChurn_arrivalAndDeparture_stillArmViaIdentity()
    {
        var (mem, realEntry, mirrorEntry, fp, meta, kills) = BuildMirrored();
        var armed = new Dictionary<(int lvl, int br, int fa), int>();
        var mushin = new Mushin(meta, kills, armed, mem);

        mushin.Tick(onField: true);   // prime (Wielder.Locate resolves the located entry to the MIRROR)

        // The actor pointer arrives at the REAL frame -- NOT the mirror's address.
        //
        // NON-VACUITY: an address-only implementation compares acting(realEntry) == entry
        // (mirrorEntry), which never matches -- the window never opens, so this never arms.
        PointActorAt(mem, realEntry);
        mushin.Tick(onField: true);
        mushin.Tick(onField: true);   // idle tick: no move, no act

        PointActorNowhere(mem);       // departure
        mushin.Tick(onField: true);

        Assert.Equal(1, StacksOf(armed, fp));
    }

    [Fact]
    public void MirrorChurn_consume_stillFiresViaIdentity()
    {
        var (mem, realEntry, mirrorEntry, fp, meta, kills) = BuildMirrored();
        var armed = new Dictionary<(int lvl, int br, int fa), int> { [fp] = 1 };   // pre-banked
        var mushin = new Mushin(meta, kills, armed, mem);

        mushin.Tick(onField: true);   // prime
        PointActorAt(mem, realEntry);  // the wielder's turn begins at the REAL frame
        mushin.Tick(onField: true);
        SetActed(mem, 1);               // the attack lands
        mushin.Tick(onField: true);

        // NON-VACUITY: an address-only implementation's ownActedEdge check (acting == entry) never
        // matches (mirrorEntry != realEntry), so the charge would survive the attack -- this is
        // exactly the "an attack sometimes did not consume" live bug the rework fixes.
        Assert.True(StacksOf(armed, fp) == 0, "the wielder's own attack must consume via identity even though Locate resolved the mirror's address");
    }

    [Fact]
    public void FailedNameIdCapture_fallsBackToAddressCompare()
    {
        // Roster nameId left UNSEEDED (0): Wielder.RosterNameId's single matching slot returns the
        // raw read (0), which fails the "> 0" guard -- this wielder degrades to the original
        // address-compare behavior, same as every pre-existing (nameId: 0) test above.
        var (mushin, mem, wielder, fp, armed) = Build(nameId: 0);

        mushin.Tick(onField: true);           // prime
        PointActorAt(mem, wielder);            // arrival via address fallback
        mushin.Tick(onField: true);
        mushin.Tick(onField: true);            // idle tick
        PointActorNowhere(mem);                 // departure via address fallback
        mushin.Tick(onField: true);

        Assert.Equal(1, StacksOf(armed, fp));
    }
}
