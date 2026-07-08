using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Kiku-ichimonji's "Mushin" signature: a full WAIT turn (no move, no action) banks one stack
/// (up to Tuning.MushinMaxStacks); the wielder's next attack spends every banked stack in one
/// boosted hit, then the buff clears.
///
/// CT-DRIVEN (2026-07-07): the trigger now rides the wielder's OWN scheduler CT
/// (Offsets.ACtSlam, read off the entry Wielder.ResolveDeployedMainHandAll resolves), not the
/// ActorPtr/TurnQueue split: both of those track the cursor/UI, not the true turn owner, and
/// live testing caught both flickering (spurious banks) and false-consuming (cursor-follow).
/// Tests drive the CT directly via SeedCt: a "turn" is CT >= Mushin.CtCeiling for a tick
/// (Idle -&gt; Active), then CT &lt; Mushin.CtFloor held for Mushin.SettleTicks+1 more ticks
/// (Active -&gt; Settling -&gt; the one decision).
///
/// LOAD-BEARING tests: Bank_requiresCtReset_notMerePresence (a flicker with no CT reset must NOT
/// bank; the exact live over-fire bug this rebuild replaces), Bank_fires_onCtCeilingThenReset
/// (the reset is what drives a bank), and Consume_survives_whenCtNeverResets (the cursor-follow
/// false-consume, now gated by the CT reset).
/// </summary>
public class MushinTests
{
    private const int KikuId = 45;
    private const int OtherWeaponId = 56;

    private static (Mushin mushin, FakeSparseMemory mem, long wielderEntry, (int lvl, int br, int fa) fp,
                    Dictionary<(int lvl, int br, int fa), int> armed)
        Build(int kills = -1, int gx = 2, int gy = 2)
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
        const int wielderSlot = 24;
        long wielder = Band.Entry(wielderSlot);

        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: KikuId);
        MemSeats.SeatBand(mem, wielderSlot, weapon: KikuId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: gx, gy: gy, hp: 200, maxHp: 300);

        var armed = new Dictionary<(int lvl, int br, int fa), int>();
        var mushin = new Mushin(meta, killDict, armed, mem);
        return (mushin, mem, wielder, fp, armed);
    }

    /// <summary>Drive the wielder's own scheduler CT (Offsets.ACtSlam), the byte the whole
    /// state machine now keys on.</summary>
    private static void SeedCt(FakeSparseMemory mem, long entry, int ct) =>
        mem.U8s[entry + Offsets.ACtSlam] = (byte)ct;

    private static void SetActed(FakeSparseMemory mem, int v) => mem.U8s[Offsets.Acted] = (byte)v;

    private static int StacksOf(Dictionary<(int lvl, int br, int fa), int> armed, (int, int, int) fp)
        => armed.TryGetValue(fp, out int s) ? s : 0;

    /// <summary>Run the settle grace out: CT is assumed already dropped below CtFloor: ticks
    /// Mushin.SettleTicks+1 times, which is exactly enough to land the Active-&gt;Settling
    /// transition tick plus every settle-countdown tick through the one decision.</summary>
    private static void RunSettle(Mushin mushin)
    {
        for (int i = 0; i < Mushin.SettleTicks + 1; i++) mushin.Tick(onField: true);
    }

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
        SeedCt(mem, wielder, 95);
        mushin.Tick(onField: true);          // a real turn's CT shape would open the window at tier 3+
        SeedCt(mem, wielder, 8);
        RunSettle(mushin);                    // ...and close it at tier 3+

        Assert.True(StacksOf(armed, fp) == 0, "below AtTier must never arm");
    }

    // ---- Priming (F5, Iai's contract) ----

    [Fact]
    public void FirstTick_staleActive_doesNotFalseArmOrConsume()
    {
        // Both the CT and Acted already read "mid-turn" on the VERY FIRST evaluated tick (no
        // Idle->Active arrival ever observed). Prove neither an arm nor a consume fires just
        // because the priming tick seeded Phase=Active.
        var (mushin, mem, wielder, fp, armed) = Build();
        SeedCt(mem, wielder, 95);
        SetActed(mem, 1);

        mushin.Tick(onField: true);   // priming tick: must not decide anything
        Assert.Equal(0, StacksOf(armed, fp));

        mushin.Tick(onField: true);   // still no edge (CT and Acted both unchanged since priming)
        Assert.Equal(0, StacksOf(armed, fp));
    }

    // ---- Arm/stack: the non-vacuous positive + negatives ----

    [Fact]
    public void FullWaitTurn_banksOneStack()
    {
        var (mushin, mem, wielder, fp, armed) = Build(gx: 2, gy: 2);

        mushin.Tick(onField: true);           // prime (CT unseeded -> 0, Idle)
        SeedCt(mem, wielder, 95);              // turn active
        mushin.Tick(onField: true);            // Idle -> Active
        mushin.Tick(onField: true);            // idle sample: no move, no act
        SeedCt(mem, wielder, 8);               // scheduler reset: turn taken
        RunSettle(mushin);

        Assert.Equal(1, StacksOf(armed, fp));
    }

    [Fact]
    public void MoveOnlyTurn_doesNotArm()
    {
        // THE NON-VACUOUS NEGATIVE: an implementation that arms on turn-end regardless of the
        // move/act signals would wrongly arm here.
        var (mushin, mem, wielder, fp, armed) = Build(gx: 2, gy: 2);

        mushin.Tick(onField: true);           // prime
        SeedCt(mem, wielder, 95);
        mushin.Tick(onField: true);            // Active, snapshots (gx=2,gy=2)
        mem.U8s[wielder + Offsets.AGx] = 5;    // the wielder moved
        mushin.Tick(onField: true);
        SeedCt(mem, wielder, 8);
        RunSettle(mushin);

        Assert.Equal(0, StacksOf(armed, fp));
    }

    [Fact]
    public void AttackTurn_doesNotArm()
    {
        var (mushin, mem, wielder, fp, armed) = Build();

        mushin.Tick(onField: true);           // prime
        SeedCt(mem, wielder, 95);
        mushin.Tick(onField: true);            // Active
        SetActed(mem, 1);                       // the wielder's own acted edge: an attack
        mushin.Tick(onField: true);
        SeedCt(mem, wielder, 8);
        RunSettle(mushin);

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
        SeedCt(mem, wielder, 95);
        mushin.Tick(onField: true);            // Active, snapshots (gx=2,gy=2)
        mem.U8s[wielder + Offsets.AGx] = 5;    // the wielder moved (no attack)
        mushin.Tick(onField: true);
        SeedCt(mem, wielder, 8);
        RunSettle(mushin);

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
            SeedCt(mem, wielder, 95);
            mushin.Tick(onField: true);
            SeedCt(mem, wielder, 8);
            RunSettle(mushin);
            Assert.Equal(i, StacksOf(armed, fp));
        }

        // A fourth wait must not overflow past the cap.
        SeedCt(mem, wielder, 95);
        mushin.Tick(onField: true);
        SeedCt(mem, wielder, 8);
        RunSettle(mushin);
        Assert.Equal(3, StacksOf(armed, fp));

        // ONE attack spends every banked stack in a single hit.
        SeedCt(mem, wielder, 95);
        mushin.Tick(onField: true);
        SetActed(mem, 1);
        mushin.Tick(onField: true);
        SeedCt(mem, wielder, 8);
        RunSettle(mushin);
        Assert.Equal(0, StacksOf(armed, fp));
    }

    // ---- Consume ----

    [Fact]
    public void Armed_ConsumedByNextOwnTurnAttack()
    {
        var (mushin, mem, wielder, fp, armed) = Build();
        armed[fp] = 1;   // pre-armed, as if a prior full-wait turn charged it

        mushin.Tick(onField: true);           // prime
        SeedCt(mem, wielder, 95);              // the wielder's next turn begins
        mushin.Tick(onField: true);
        SetActed(mem, 1);                       // the attack lands: own acted edge inside the window
        mushin.Tick(onField: true);
        SeedCt(mem, wielder, 8);
        RunSettle(mushin);

        Assert.True(StacksOf(armed, fp) == 0, "the wielder's own attack must consume every banked stack");
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
        SeedCt(mem, wielder, 95);
        mushin.Tick(onField: true);
        SeedCt(mem, wielder, 8);
        RunSettle(mushin);

        Assert.True(StacksOf(armed, fp) == 0, "offhand-only wielder must never arm Mushin");
    }

    // ---- NON-VACUITY: load-bearing tests for the CT-reset-gated turn detector ----

    [Fact]
    public void Bank_requiresCtReset_notMerePresence()
    {
        // THE LOAD-BEARING NON-VACUITY TEST: reproduce the live over-fire bug directly. The old
        // ActorPtr window banked on ANY window close, CT reset or not, so a flicker (the window
        // opening and closing without the wielder ever actually taking its scheduler turn) still
        // banked a stack. Holding CT at a mid value that never reaches CtCeiling, with no
        // move/act, across MANY ticks must never bank: there is no turn here at all.
        var (mushin, mem, wielder, fp, armed) = Build();
        mushin.Tick(onField: true);   // prime

        SeedCt(mem, wielder, 50);   // never reaches CtCeiling: no Idle->Active transition, ever
        for (int i = 0; i < 50; i++) mushin.Tick(onField: true);

        Assert.Equal(0, StacksOf(armed, fp));
    }

    [Fact]
    public void Bank_fires_onCtCeilingThenReset()
    {
        // The positive twin of Bank_requiresCtReset_notMerePresence: the CT actually reaching the
        // ceiling then resetting below the floor is what drives a bank.
        var (mushin, mem, wielder, fp, armed) = Build();
        mushin.Tick(onField: true);   // prime

        SeedCt(mem, wielder, 95);
        mushin.Tick(onField: true);   // Idle -> Active
        SeedCt(mem, wielder, 8);
        RunSettle(mushin);             // Active -> Settling -> decide

        Assert.Equal(1, StacksOf(armed, fp));
    }

    [Fact]
    public void Consume_survives_whenCtNeverResets()
    {
        // The cursor-follow false-consume this rebuild replaces: an Acted edge alone, with no CT
        // reset ever confirming a turn actually happened (CT stays mid, never reaches the
        // ceiling), must never spend the banked stacks.
        var (mushin, mem, wielder, fp, armed) = Build();
        armed[fp] = 2;   // pre-banked

        mushin.Tick(onField: true);   // prime

        SeedCt(mem, wielder, 50);   // never reaches CtCeiling: the wielder never even enters Active
        SetActed(mem, 1);
        for (int i = 0; i < 20; i++) mushin.Tick(onField: true);

        Assert.True(armed[fp] == 2, "an acted edge with no confirmed CT turn must not consume");
    }
}
