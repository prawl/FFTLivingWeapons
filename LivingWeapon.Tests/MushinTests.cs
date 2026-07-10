using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Kiku-ichimonji's "Mushin" signature, ROUND 5 (2026-07-09, owner decision: replace rounds 2-4's
/// CT-clock/seq-gate apparatus with the literal design on the engine's own per-unit turn flags).
/// See Mushin.cs's class doc for the full PSX-mapping provenance (tools/probes/mushin_wait_probe.py,
/// scratchpad/psxflags_watch.log) and the retired rounds' one-line history.
///
/// This suite drives the trigger DIRECTLY on the three band-relative turn-flag bytes (local test
/// consts aliasing Offsets.ATurnFlag/AMoved/AActed since LW-55 stage 1 promoted them out of
/// Mushin.cs): TURN FLAG (band +0x19C, the falling edge that decides), MOVED (+0x19D), ACTED
/// (+0x19E). No KillTracker, no TurnTracker, no CT clocks, no static-array oracle: Mushin's
/// round-5 ctor no longer even takes a KillTracker.
///
/// LOAD-BEARING: L-1 (a genuine full wait arms) and L-6 (nothing else matters: the module reads
/// none of rounds 2-4's signals) are the two tests written FIRST and confirmed RED against the
/// round-4 implementation before this file existed in its round-5 shape (see the round-5 build's
/// own commit-staging notes); L-6's round-4 counterpart proved round 4 DID react to the exact
/// "other units' CT churn" topology this suite now proves round 5 ignores entirely.
/// </summary>
public class MushinTests
{
    private const int KikuId = 45;
    private const int OtherWeaponId = 56;

    // LW-55 stage 1 promoted these to Offsets.cs (ATurnFlag/AMoved/AActed; see that file for the
    // full PSX -> frame -> band provenance chain). Local aliases kept so this suite's own naming
    // is unchanged from round 5.
    private const int TurnFlagOffset = Offsets.ATurnFlag;
    private const int MovedOffset = Offsets.AMoved;
    private const int ActedOffset = Offsets.AActed;

    private static void SetFlags(FakeSparseMemory mem, long entry, int turnFlag, int moved, int acted)
    {
        mem.ReadableAddrs.Add(entry + TurnFlagOffset);
        mem.ReadableAddrs.Add(entry + MovedOffset);
        mem.ReadableAddrs.Add(entry + ActedOffset);
        mem.U8s[entry + TurnFlagOffset] = (byte)turnFlag;
        mem.U8s[entry + MovedOffset] = (byte)moved;
        mem.U8s[entry + ActedOffset] = (byte)acted;
    }

    private static int StacksOf(Dictionary<(int lvl, int br, int fa), int> armed, (int lvl, int br, int fa) fp)
        => armed.TryGetValue(fp, out int s) ? s : 0;

    private static (FakeSparseMemory mem, Dictionary<int, int> kills, Dictionary<(int lvl, int br, int fa), int> armed,
                    Mushin mushin, long wielderEntry, (int lvl, int br, int fa) fp)
        Build(int kills = -1)
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
        var wielderFp = (lvl: 30, br: 65, fa: 60);
        const int wielderSlot = 24;
        long wielderEntry = Band.Entry(wielderSlot);

        MemSeats.SeatRoster(mem, 0, lvl: wielderFp.lvl, br: wielderFp.br, fa: wielderFp.fa, rh: KikuId);
        MemSeats.SeatBand(mem, wielderSlot, weapon: KikuId, lvl: wielderFp.lvl, br: wielderFp.br, fa: wielderFp.fa,
                          gx: 5, gy: 5, hp: 200, maxHp: 300);

        var armed = new Dictionary<(int lvl, int br, int fa), int>();
        var mushin = new Mushin(meta, killDict, armed, mem);
        return (mem, killDict, armed, mushin, wielderEntry, wielderFp);
    }

    // ================= L-1 [LOAD-BEARING] =================

    [Fact]
    public void L1_FullWait_Arms()
    {
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        SetFlags(mem, wielderEntry, turnFlag: 0, moved: 0, acted: 0);
        mushin.Tick(true);   // prime

        SetFlags(mem, wielderEntry, turnFlag: 1, moved: 0, acted: 0);   // turn opens
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, turnFlag: 0, moved: 0, acted: 0);   // turn closes: a genuine full wait
        mushin.Tick(true);

        Assert.Equal(1, StacksOf(armed, fp));
    }

    // ================= L-2 =================

    [Fact]
    public void L2_MoveOnlyTurn_ArmedStaysZero()
    {
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);   // prime

        SetFlags(mem, wielderEntry, 1, 0, 0);   // turn opens
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 1, 1, 0);   // the wielder moves, mid-window
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 0, 1, 0);   // turn closes: move-only
        mushin.Tick(true);

        Assert.Equal(0, StacksOf(armed, fp));
    }

    // ================= L-3 =================

    [Fact]
    public void L3_MoveOnlyTurn_PreArmedCharge_Survives()
    {
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);   // prime
        armed[fp] = 1;       // as if a prior wait already armed the charge

        SetFlags(mem, wielderEntry, 1, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 1, 1, 0);   // moves mid-window
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 0, 1, 0);   // move-only close
        mushin.Tick(true);

        Assert.Equal(1, StacksOf(armed, fp));   // untouched: move-only neither arms nor consumes
    }

    // ================= L-4 =================

    [Fact]
    public void L4_AttackTurn_Consumes()
    {
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);   // prime
        armed[fp] = 1;       // pre-armed

        SetFlags(mem, wielderEntry, 1, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 1, 0, 1);   // the wielder acts, mid-window
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 0, 0, 1);   // turn closes: acted
        mushin.Tick(true);

        Assert.Equal(0, StacksOf(armed, fp));
    }

    // ================= L-5 =================

    [Fact]
    public void L5_MoveThenAttackTurn_ConsumesNotArms()
    {
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);   // prime
        armed[fp] = 1;       // pre-armed: the discriminating precondition. A wrong implementation
                              // that checks "moved" before "acted" would leave this SURVIVING
                              // (the L-3 shape) instead of correctly consuming it.

        SetFlags(mem, wielderEntry, 1, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 1, 1, 0);   // moves first
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 1, 1, 1);   // then attacks, same window
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 0, 1, 1);   // turn closes: moved AND acted
        mushin.Tick(true);

        Assert.Equal(0, StacksOf(armed, fp));   // acted takes priority: consume, never arm
    }

    // ================= L-6 [NON-VACUITY vs rounds 2-4] =================

    [Fact]
    public void L6_NothingElseMatters_LegacySignalChurn_ZeroEffect()
    {
        // Round 2-4's whole apparatus (other units' scheduler-CT cycling, the global Acted byte,
        // the ActorPtr) is seeded with exactly the topology that armed the charge under round 4
        // (see the round-5 build's RED-phase proof against round 4), while the wielder's OWN turn
        // flag never transitions at all. Round 5 reads none of it: zero effect.
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        mem.ReadableAddrs.Add(wielderEntry + TurnFlagOffset);
        mem.U8s[wielderEntry + TurnFlagOffset] = 0;   // never transitions this whole test
        mushin.Tick(true);   // prime

        long enemyEntry = Band.Entry(0);
        mem.U8s[enemyEntry + Offsets.ALevel] = 20;
        mem.U8s[enemyEntry + Offsets.ABrave] = 40;
        mem.U8s[enemyEntry + Offsets.AFaith] = 45;
        mem.U16s[enemyEntry + Offsets.AMaxHp] = 250;
        mem.U16s[enemyEntry + Offsets.AHp] = 250;
        mem.U8s[enemyEntry + Offsets.AGx] = 1;
        mem.U8s[enemyEntry + Offsets.AGy] = 1;

        for (int i = 0; i < 8; i++)
        {
            mem.U8s[Offsets.Acted] = (byte)(i % 2 == 0 ? 1 : 0);                    // global Acted flip
            mem.U8s[enemyEntry + Offsets.ACtSlam] = (byte)(i % 2 == 0 ? 95 : 8);    // another unit's CT cycling
            mem.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (i % 5) * Offsets.CombatStride));
            mushin.Tick(true);
        }

        Assert.Empty(armed);
    }

    // ================= L-7 =================

    [Fact]
    public void L7_ReactionContamination_ActedDuringWait_DoesNotArm()
    {
        // Fail-safe direction: a reaction mid-window (an enemy's charged spell resolving while
        // the wielder's own menu is open) sets ACTED without real player intent. Indistinguishable
        // from a real action here, so the wait never arms.
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);   // prime

        SetFlags(mem, wielderEntry, 1, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 1, 0, 1);   // acted sets mid-window, unintended
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 0, 0, 1);   // closes
        mushin.Tick(true);

        Assert.Equal(0, StacksOf(armed, fp));
    }

    // ================= L-8 =================

    [Fact]
    public void L8_Priming_FirstSightFlagAlreadyOne_ThenFallingEdge_DecidesNormally()
    {
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        SetFlags(mem, wielderEntry, 1, 0, 0);   // first sight: mid-turn (flag already open)
        mushin.Tick(true);                       // primes only, no decision (safe: flags reset at open)
        Assert.Empty(armed);

        SetFlags(mem, wielderEntry, 0, 0, 0);   // falls: decides normally
        mushin.Tick(true);

        Assert.Equal(1, StacksOf(armed, fp));
    }

    [Fact]
    public void L8_Priming_FirstSightFlagZero_NoDecisionUntilFullCycle()
    {
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        SetFlags(mem, wielderEntry, 0, 0, 0);   // first sight: closed
        mushin.Tick(true);                       // primes only
        SetFlags(mem, wielderEntry, 0, 0, 0);   // still closed: no rise ever happened, so no edge
        mushin.Tick(true);
        Assert.Equal(0, StacksOf(armed, fp));

        SetFlags(mem, wielderEntry, 1, 0, 0);   // now a genuine open
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 0, 0, 0);   // and a genuine close: a full 0->1->0 cycle
        mushin.Tick(true);

        Assert.Equal(1, StacksOf(armed, fp));
    }

    // ================= L-9 =================

    [Fact]
    public void L9_FlagFlicker_DecidesOncePerFallingEdge_ReArmIdempotent()
    {
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);   // prime

        // First full wait: 0 -> 1 -> 0 arms.
        SetFlags(mem, wielderEntry, 1, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);
        Assert.Equal(1, StacksOf(armed, fp));

        // Held CLOSED for further ticks: prev is already 0, so this is NOT a repeated falling
        // edge. A later value change (acted flipping true) while the flag stays closed must never
        // be mistaken for a second decision; if it were, this would wrongly consume the charge.
        SetFlags(mem, wielderEntry, 0, 0, 1);
        mushin.Tick(true);
        mushin.Tick(true);
        Assert.Equal(1, StacksOf(armed, fp));   // still armed: the stray acted flip was never evaluated

        // A GENUINE second falling edge (the flag actually reopens, then closes again) is a new
        // decision and re-arms idempotently: armed stays 1, never climbs past 1, harmless.
        SetFlags(mem, wielderEntry, 1, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);
        Assert.Equal(1, StacksOf(armed, fp));
    }

    // ================= L-10 =================

    [Fact]
    public void L10_TwoWielders_Isolated()
    {
        var (mem, kills, armed, mushin, wielderEntryA, fpA) = Build();

        var fpB = (lvl: 45, br: 80, fa: 30);
        const int slotB = 21;
        long wielderEntryB = Band.Entry(slotB);
        MemSeats.SeatRoster(mem, 1, lvl: fpB.lvl, br: fpB.br, fa: fpB.fa, rh: KikuId);
        MemSeats.SeatBand(mem, slotB, weapon: KikuId, lvl: fpB.lvl, br: fpB.br, fa: fpB.fa,
                          gx: 12, gy: 12, hp: 180, maxHp: 260);

        SetFlags(mem, wielderEntryA, 0, 0, 0);
        SetFlags(mem, wielderEntryB, 0, 0, 0);
        mushin.Tick(true);   // primes both wielders' independent falling-edge state
        armed[fpB] = 1;       // B pre-armed

        // A does a genuine full wait: arms A only.
        SetFlags(mem, wielderEntryA, 1, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntryA, 0, 0, 0);
        mushin.Tick(true);

        // B attacks: consumes B only, independent of A.
        SetFlags(mem, wielderEntryB, 1, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntryB, 0, 0, 1);
        mushin.Tick(true);

        Assert.Equal(1, StacksOf(armed, fpA));
        Assert.Equal(0, StacksOf(armed, fpB));
    }

    // ================= L-11: Gates =================

    [Fact]
    public void L11_Gate_offField_NoDecisions_PrevFlagFrozen()
    {
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);   // prime on-field

        // Off-field: the flag transitions through a would-be full wait, but the module must never
        // observe it (band reads are unsafe off-field; the loop returns before touching anything).
        SetFlags(mem, wielderEntry, 1, 0, 0);
        mushin.Tick(false);
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(false);
        Assert.Empty(armed);

        // Back on-field, prev is still frozen at 0 (the last on-field observation) and current
        // also reads 0: no phantom edge on return.
        mushin.Tick(true);
        Assert.Empty(armed);

        // The module is still fully alive: a genuine subsequent full wait still arms normally.
        SetFlags(mem, wielderEntry, 1, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);
        Assert.Equal(1, StacksOf(armed, fp));
    }

    [Fact]
    public void L11_Gate_belowTier_neverArms()
    {
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build(kills: Tuning.ProdThresholds[1]);   // tier 2 < AtTier 3
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 1, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);

        Assert.True(StacksOf(armed, fp) == 0, "below AtTier must never arm even with a genuine full wait");
    }

    [Fact]
    public void L11_Gate_missing_meta_no_crash()
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>();
        var kills = new Dictionary<int, int> { [KikuId] = Tuning.ProdThresholds[2] };
        var armed = new Dictionary<(int, int, int), int>();
        var mushin = new Mushin(meta, kills, armed, mem);

        mushin.Tick(true);

        Assert.Empty(armed);
    }

    [Fact]
    public void L11_Gate_null_signature_no_crash()
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [KikuId] = new WeaponMeta { Name = "Kiku-ichimonji", Wp = 13, Cat = "Katana", Formula = 2, Signature = null }
        };
        var kills = new Dictionary<int, int> { [KikuId] = Tuning.ProdThresholds[2] };
        var armed = new Dictionary<(int, int, int), int>();
        var mushin = new Mushin(meta, kills, armed, mem);

        mushin.Tick(true);

        Assert.Empty(armed);
    }

    [Fact]
    public void L11_Gate_offhandOnly_neverArms()
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
        var kills = new Dictionary<int, int> { [KikuId] = Tuning.ProdThresholds[2] };
        var fp = (lvl: 30, br: 65, fa: 60);
        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: OtherWeaponId, oh: KikuId);
        MemSeats.SeatBand(mem, 20, weapon: OtherWeaponId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 2, gy: 2, hp: 200, maxHp: 300);
        var armed = new Dictionary<(int lvl, int br, int fa), int>();
        var mushin = new Mushin(meta, kills, armed, mem);

        mushin.Tick(true);

        Assert.True(StacksOf(armed, fp) == 0, "offhand-only wielder must never arm Mushin");
        Assert.Empty(armed);   // ResolveDeployedMainHandAll never returns an offhand-only slot
    }

    [Fact]
    public void L11_ResetBattle_clears_armed_and_primeState()
    {
        var (mem, kills, armed, mushin, wielderEntry, fp) = Build();
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);   // prime at closed
        SetFlags(mem, wielderEntry, 1, 0, 0);
        mushin.Tick(true);   // turn opens: prev becomes 1 (mid-turn, no decision yet)
        armed[fp] = 1;         // pretend a charge is already armed going into the reset

        mushin.ResetBattle();
        Assert.Empty(armed);

        // The prime state must ALSO be cleared: if a stale prev==1 survived the reset, the very
        // next tick reading flag==0 would be wrongly treated as a genuine falling edge (a decision
        // leaking across the battle boundary). A correctly cleared prime state treats this as
        // FIRST SIGHT instead: prime only, no decision.
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);
        Assert.Empty(armed);   // no decision: this was a fresh prime, not a leaked falling edge

        // The module is fully alive post-reset: a genuine subsequent full wait still arms.
        SetFlags(mem, wielderEntry, 1, 0, 0);
        mushin.Tick(true);
        SetFlags(mem, wielderEntry, 0, 0, 0);
        mushin.Tick(true);
        Assert.Equal(1, StacksOf(armed, fp));
    }
}
