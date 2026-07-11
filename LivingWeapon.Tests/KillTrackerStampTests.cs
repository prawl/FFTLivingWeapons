using System.Collections.Generic;
using System.Reflection;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Death-edge culprit-stamp integration tests -- KillTracker.Poll driven end-to-end through the
/// REAL KillerStamp/ActorRegister wiring (KillTracker.Stamp.cs, KillTracker.Corpses.cs's
/// deadStreak==1 edge). Companion to KillerStampTests.cs (the pure Decide/TryHypothesis halves).
/// Fixture idioms mirror KillTrackerTests.cs's register-first section (~line 1492+): SetActive
/// seats the turn-queue tuple, SetUnit/SetEnemy seat band entries (+ the static-array identity
/// capture for enemies), SetRoster seats a roster slot's fingerprint+hands+nameId, PointAt/
/// SetFrameNameId drive the engine actor pointer and its frame&lt;-&gt;roster nameId bridge.
/// </summary>
public class KillTrackerStampTests
{
    private static void SetActive(FakeSparseMemory m, int hp, int maxHp, int level, int team = 0, int acted = 1)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = (ushort)team;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
        m.U8s[Offsets.Acted] = (byte)acted;
    }

    private static void SetUnit(FakeSparseMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                                int level = 10, int brave = 50, int faith = 50, int weapon = 0)
        => MemSeats.SeatBand(m, slot, weapon: weapon, lvl: level, br: brave, fa: faith,
                             gx: gx, gy: gy, hp: hp, maxHp: maxHp);

    private static void SetArrayEnemy(FakeSparseMemory m, int slot, int level, int brave, int faith, int maxHp,
                                      int inb = 1)
    {
        long s = Offsets.ArrayReadBase + (long)slot * Offsets.ArrayStride;
        m.U16s[s + Offsets.AInBattle] = (ushort)inb;
        m.U8s[s + Offsets.ALevel] = (byte)level;
        m.U8s[s + Offsets.ABrave] = (byte)brave;
        m.U8s[s + Offsets.AFaith] = (byte)faith;
        m.U16s[s + Offsets.AMaxHp] = (ushort)maxHp;
    }

    private static void SetEnemy(FakeSparseMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                                 int level = 10, int brave = 50, int faith = 50)
    {
        SetUnit(m, slot, hp, maxHp, gx, gy, level, brave, faith);
        if (slot <= Offsets.EnemySlotMax)
            SetArrayEnemy(m, slot, level, brave, faith, maxHp);
    }

    private static void SetRoster(FakeSparseMemory m, int slot, int level, int brave, int faith, int weapon,
                                  int lhand = 0xFFFF, int offhand = 0xFFFF, int nameId = 0)
        => MemSeats.SeatRoster(m, slot, level, brave, faith, weapon, lhand, offhand, nameId);

    private static void PointAt(FakeSparseMemory m, int bandIdx) =>
        m.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (long)bandIdx * Offsets.CombatStride));

    private static void SetFrameNameId(FakeSparseMemory m, int bandIdx, int nameId) =>
        MemSeats.SeatFrameNameId(m, bandIdx, nameId);

    private static void SetJumpBit(FakeSparseMemory m, int bandSlot, bool set = true)
    {
        long addr = Band.Entry(bandSlot);
        byte cur = m.U8s.TryGetValue(addr + Offsets.ADeadStatus, out var v) ? v : (byte)0;
        m.U8s[addr + Offsets.ADeadStatus] = set
            ? (byte)(cur | Offsets.AJumpBit)
            : (byte)(cur & ~Offsets.AJumpBit);
    }

    private static void Settle(KillTracker t, int n = 3) { for (int i = 0; i < n; i++) t.Poll(true); }

    /// <summary>Set a band slot alive (hp&gt;0), settle 3 ticks (seenAlive), then set it dead and
    /// settle 3 ticks (deadStreak). Leaves it ready for credit (mirrors KillTrackerTests.cs).</summary>
    private static void AliveThenDead(FakeSparseMemory m, int slot, KillTracker t,
                                      int hp = 300, int maxHp = 400, int level = 10, int brave = 50, int faith = 50)
    {
        SetEnemy(m, slot, hp, maxHp, level: level, brave: brave, faith: faith);
        Settle(t, 3);
        SetUnit(m, slot, hp: 0, maxHp: maxHp, level: level, brave: brave, faith: faith);
        Settle(t, 3);
    }

    // Player band slot indices (arbitrary; just need to be player-side / non-enemy).
    private const int P1 = Offsets.SlotsBack;       // band slot 20
    private const int P2 = Offsets.SlotsBack + 1;   // band slot 21
    private const int P3 = Offsets.SlotsBack + 2;   // band slot 22

    private static readonly HashSet<int> Weapons = new() { 22, 37, 53, 60, 84, 90 };

    // --- Tape replays (the live 2026-07-05 flight captures this fix is built from) ---

    [Fact]
    public void Tape2_replay_register_overrides_stale_latch()
    {
        // THE LOAD-BEARING TEST. P1 latches at period start (register-first, mirroring
        // KillTrackerTests.Pointer_stable_owner_credits_over_ambiguous_fingerprint's setup). The
        // acted byte then STAYS at 1 -- KillTracker's own latch machinery never re-resolves, so
        // without this fix the corpse would credit P1's stale weapon. The engine actor pointer
        // (ground truth, independent of the Acted-byte gate) meanwhile dwells on the enemy
        // victim's OWN frame (Enemy bridge -- must leave the snapshot untouched) and then arrives
        // on P2 -- the register hypothesis this fix consults catches up to the real killer where
        // the latch cannot.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();

        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 53, nameId: 501);   // P1
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: 22, nameId: 502);   // P2
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons, recorder: (type, payload) => recorded.Add((type, payload)));

        t.Poll(true);                              // priming (pointer unseeded)
        SetFrameNameId(m, P1, 501);
        PointAt(m, P1);
        t.Poll(true);                              // arrival on P1 -- strictly before the edge

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        t.Poll(true);                              // period begins -- P1's ONE resolve: latch [53]

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);                              // victim seenAlive

        // The pointer dwells on the victim's OWN frame (Enemy bridge) -- must leave the snapshot
        // untouched, not clear P1's.
        SetFrameNameId(m, 0, 700);
        PointAt(m, 0);
        t.Poll(true);

        // The pointer moves on to P2 -- a fresh, disagreeing Player hypothesis (postdates P1's resolve).
        SetFrameNameId(m, P2, 502);
        PointAt(m, P2);
        t.Poll(true);

        // acted NEVER falls -- the stuck period from the live tape. The kill lands now.
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true);   // deadStreak=1: StampCulprit overrides the stale latch with P2's [22]
        t.Poll(true);   // deadStreak=2
        t.Poll(true);   // deadStreak=3 -> credit

        Assert.Equal(1, kills.GetValueOrDefault(22));
        Assert.False(kills.ContainsKey(53));
        Assert.Contains(recorded, r => r.type == "kill" && r.payload.StartsWith("stamp-override"));
    }

    [Fact]
    public void Tape1_replay_stuck_period_credits_current_killer()
    {
        // Companion tape: acted is HELD at 1 for the whole test -- no falling edge ever fires, so
        // _lastPlayerWeapons/_lastResolveTick are set exactly ONCE (P1's single resolve) and never
        // move again. Two separate corpses mature later in the SAME stuck period, each while the
        // pointer sits on a DIFFERENT player (P2, then P3) -- both must credit the CURRENT pointer
        // owner, not the one-time latch, independent of any acted-falling edge.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 60, nameId: 601);   // P1
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: 37, nameId: 602);   // P2
        SetRoster(m, slot: 5, level: 40, brave: 45, faith: 48, weapon: 84, nameId: 603);   // P3
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        SetUnit(m, P3, hp: 380, maxHp: 380, level: 40, brave: 45, faith: 48);
        var t = new KillTracker(kills, m, Weapons);

        t.Poll(true);
        SetFrameNameId(m, P1, 601);
        PointAt(m, P1);
        t.Poll(true);                    // arrival on P1 -- strictly before the edge

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        t.Poll(true);                    // period begins -- P1's ONE-TIME resolve: latch [60]

        // Victim 1 comes alive, then the pointer moves to P2 (postdates the resolve), then it dies.
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);
        SetFrameNameId(m, P2, 602);
        PointAt(m, P2);
        t.Poll(true);                    // arrival on P2
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true); t.Poll(true); t.Poll(true);   // deadStreak 1->3: register names P2 -> credit [37]

        // Victim 2 comes alive, then the pointer moves to P3, then it dies -- acted NEVER fell.
        SetEnemy(m, slot: 1, hp: 250, maxHp: 300, level: 15, brave: 40, faith: 40);
        Settle(t, 3);
        SetFrameNameId(m, P3, 603);
        PointAt(m, P3);
        t.Poll(true);                    // arrival on P3
        SetUnit(m, slot: 1, hp: 0, maxHp: 300, level: 15, brave: 40, faith: 40);
        t.Poll(true); t.Poll(true); t.Poll(true);   // deadStreak 1->3: register names P3 -> credit [84]

        Assert.Equal(1, kills.GetValueOrDefault(37));
        Assert.Equal(1, kills.GetValueOrDefault(84));
        Assert.False(kills.ContainsKey(60));
    }

    // --- Safety-net cases (dwell / ambiguity / staleness / scope fences) ---

    [Fact]
    public void Victim_dwell_does_not_displace_killer()
    {
        // The pointer dwells on the VICTIM's own frame right up to (and through) the death edge --
        // a real dwell pattern (struck units park the pointer). An implementation reading
        // CurrentBridge at the edge (instead of the LastPlayer snapshot) would see Enemy here and
        // wrongly fall back to the stale latch; the correct fix still credits P2.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 60, nameId: 908);   // P1
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: 37, nameId: 909);   // P2
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons);

        t.Poll(true);
        SetFrameNameId(m, P1, 908);
        PointAt(m, P1);
        t.Poll(true);

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        t.Poll(true);                       // P1 latches [60]

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        SetFrameNameId(m, P2, 909);
        PointAt(m, P2);
        t.Poll(true);                       // fresh arrival on P2 -- the last-player snapshot

        SetFrameNameId(m, 0, 950);           // matches no roster slot -> Enemy bridge
        PointAt(m, 0);
        t.Poll(true);                       // the pointer now dwells on the victim's own frame

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true); t.Poll(true); t.Poll(true);   // deadStreak 1->3, pointer still on the victim

        Assert.Equal(1, kills.GetValueOrDefault(37));
        Assert.False(kills.ContainsKey(60));
    }

    [Fact]
    public void Unknown_bridge_arrival_invalidates_hypothesis()
    {
        // P2's fresh Player arrival is followed by an AMBIGUOUS arrival (a nameId+fingerprint
        // matching TWO roster slots) -- Unknown bridge CLEARS the snapshot (ActorRegister.Update).
        // The kill must credit the LATCH, not P2's now-invalidated hypothesis.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 60, nameId: 910);   // P1
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: 37, nameId: 911);   // P2
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        // Two roster slots share the SAME fingerprint+nameId -- an arrival matching both is Unknown.
        SetRoster(m, slot: 6, level: 45, brave: 55, faith: 65, weapon: 84, nameId: 999);
        SetRoster(m, slot: 7, level: 45, brave: 55, faith: 65, weapon: 22, nameId: 999);
        SetUnit(m, P3, hp: 380, maxHp: 380, level: 45, brave: 55, faith: 65);
        var t = new KillTracker(kills, m, Weapons);

        t.Poll(true);
        SetFrameNameId(m, P1, 910);
        PointAt(m, P1);
        t.Poll(true);

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        t.Poll(true);                       // P1 latches [60]

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        SetFrameNameId(m, P2, 911);
        PointAt(m, P2);
        t.Poll(true);                       // fresh Player arrival -- snapshot populated

        SetFrameNameId(m, P3, 999);
        PointAt(m, P3);
        t.Poll(true);                       // ambiguous arrival (duplicate nameId) -- Unknown clears it

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true); t.Poll(true); t.Poll(true);   // deadStreak 1->3: no hypothesis -> latch [60] governs

        Assert.Equal(1, kills.GetValueOrDefault(60));
        Assert.False(kills.ContainsKey(37));
    }

    [Fact]
    public void No_pointer_seeded_is_byte_identical()
    {
        // No ActorPtr writes at all -- the register never trusts an arrival, so StampCulprit's
        // hypothesis never fires and behavior is byte-identical to the pre-fix latch-only path.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 53);
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);

        Settle(t, 3);
        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(53));
    }

    [Fact]
    public void Stale_arrival_outside_window_uses_latch()
    {
        // Ordering passes (the arrival postdates the resolve) but the coarse RegisterKillWindow
        // backstop has long since elapsed -- the hypothesis must be refused and the latch governs.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 60, nameId: 906);   // P1
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: 37, nameId: 907);   // P2
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons);

        t.Poll(true);
        SetFrameNameId(m, P1, 906);
        PointAt(m, P1);
        t.Poll(true);                       // arrival on P1 -- strictly before the edge

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        t.Poll(true);                       // P1 latches [60]

        SetFrameNameId(m, P2, 907);
        PointAt(m, P2);
        t.Poll(true);                       // fresh arrival on P2 -- postdates the resolve

        // Let the arrival go stale: far more ticks than RegisterKillWindow with no new transition.
        for (int i = 0; i < Tuning.RegisterKillWindow + 5; i++) t.Poll(true);

        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(60));
        Assert.False(kills.ContainsKey(37));
    }

    [Fact]
    public void Bury_when_register_player_unarmed()
    {
        // A fresh, disagreeing hypothesis whose roster slot holds NO tracked weapon must bury the
        // kill (miss beats mis-credit) rather than fall back to crediting the stale (nonempty) latch.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 60, nameId: 801);        // P1: tracked
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: 0xFFFF, nameId: 802);    // P2: bare hands
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons, recorder: (type, payload) => recorded.Add((type, payload)));

        t.Poll(true);
        SetFrameNameId(m, P1, 801);
        PointAt(m, P1);
        t.Poll(true);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        t.Poll(true);                       // P1 latches [60]

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        SetFrameNameId(m, P2, 802);
        PointAt(m, P2);
        t.Poll(true);                       // arrival on P2 -- a fresh but EMPTY hypothesis

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true); t.Poll(true); t.Poll(true);   // deadStreak 1->3: bury, not credit-to-latch

        Assert.Empty(kills);
        Assert.Contains(recorded, r => r.type == "kill" && r.payload.StartsWith("stamp-bury"));
    }

    [Fact]
    public void Delayed_culprit_still_preempts_stamp()
    {
        // Pins ConsumeDelayedCulprit's precedence over the new register stamp: P1's Jump-charge
        // snapshot arms while a LIVE, fresh, disagreeing register hypothesis (P2) exists at kill
        // time -- StampCulprit still runs and would stamp P2's weapon into _lethalActor, but the
        // delayed culprit is consulted FIRST at credit time (`culprit = delayed ?? _lethalActor[s]
        // ?? ...`), so P1 -- the true committer -- is credited regardless.
        const int w1 = 60, w2 = 37;
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 0, level: 99, brave: 89, faith: 76, weapon: w1);                // P1: jumper
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: w2, nameId: 902);   // P2
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetJumpBit(m, P1, set: true);   // Jump bit set at commit
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);                    // P1 latches [60] (TQ path; register not yet seeded) + snapshots

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);                    // enemy seenAlive (established before the arm)

        SetJumpBit(m, P1, set: false);   // Jump lands -- arms [60]
        t.Poll(true);

        // A fresh, disagreeing register hypothesis arrives (P2) -- would override the stamp if
        // not for the delayed-culprit precedence.
        SetFrameNameId(m, P2, 902);
        PointAt(m, P2);
        t.Poll(true);

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true); t.Poll(true); t.Poll(true);   // deadStreak 1->3 -- delayed [60] wins over the stamp

        Assert.Equal(1, kills.GetValueOrDefault(w1));
        Assert.False(kills.ContainsKey(w2));
    }

    [Fact]
    public void NonPlayerTurn_divert_still_buries()
    {
        // Scope fence: the pre-existing nonPlayerTurn divert (CounterAttributionTests) fires
        // BEFORE StampCulprit is ever consulted -- a fresh, otherwise-valid register hypothesis
        // naming a DIFFERENT player must not leak past the team gate and produce a credit.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        m.ReadableAddrs.Add(Offsets.TurnQueue + Offsets.TqTeam);
        SetRoster(m, slot: 0, level: 90, brave: 80, faith: 70, weapon: 60);                // P1
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 90, brave: 80, faith: 70);
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: 37, nameId: 903);   // P2
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons);

        SetActive(m, hp: 352, maxHp: 352, level: 90, team: 0, acted: 1);
        Settle(t, 3);                       // P1 latches [60] (team=0, TQ path)

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        SetActive(m, hp: 352, maxHp: 352, level: 90, team: 0, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);   // P1's period ends -- stale latch [60] persists

        // A fresh register hypothesis for a DIFFERENT player (P2) -- would win the load-bearing
        // test's shape, but the death lands during an ENEMY turn (team=1) instead.
        SetFrameNameId(m, P2, 903);
        PointAt(m, P2);
        t.Poll(true);

        SetActive(m, hp: 150, maxHp: 250, level: 20, team: 1, acted: 1);   // enemy turn
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true);   // deadStreak=1: nonPlayerTurn -> _lethalUntracked; StampCulprit never runs
        t.Poll(true);
        t.Poll(true);   // deadStreak=3 -> no credit

        Assert.Empty(kills);
    }

    [Fact]
    public void Pending_extension_no_latch_fresh_hypothesis_credits_register()
    {
        // A: the REAL pending-hole geometry -- an acted-period is OPEN but the latch is
        // unresolvable by EITHER path: the period begins on a PERMANENTLY garbage TQ tuple
        // (the Ownership_churn trick -- TQ can never match a band unit), and the pointer
        // arrival lands AFTER period start (StableSince false -> the register latch path is
        // closed too). _lastResolveTick stays 0; the fresh Player arrival is the only identity
        // evidence, and the pending-hole extension stamps the killer at the death edge instead
        // of leaving the corpse to a possibly-stale future latch.
        const int p2Weapon = 22;
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: p2Weapon, nameId: 904);
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons);

        t.Poll(true);                                        // priming
        SetActive(m, hp: 0, maxHp: 0, level: 0, acted: 1);   // period opens on a garbage tuple
        t.Poll(true);                 // BeginActedPeriod -- the latch can never resolve via TQ
        SetFrameNameId(m, P2, 904);
        PointAt(m, P2);
        t.Poll(true);                 // arrival AFTER period start -> register latch path closed too

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);                 // enemy seenAlive
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true); t.Poll(true); t.Poll(true);   // deadStreak 1->3: the pending-hole extension stamps P2

        Assert.Equal(1, kills.GetValueOrDefault(p2Weapon));

        // B: no hypothesis at all (pointer never seeded) -- must stay byte-identical to today: the
        // corpse waits pending until a real latch appears (mirrors
        // KillTrackerTests.Credits_a_corpse_seen_before_the_actor_latched's shape).
        var kills2 = new Dictionary<int, int>();
        var m2 = new FakeSparseMemory();
        SetRoster(m2, slot: 3, level: 99, brave: 89, faith: 76, weapon: 53);
        SetUnit(m2, P1, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m2, hp: 352, maxHp: 352, level: 99, acted: 0);   // actor has NOT acted yet
        var t2 = new KillTracker(kills2, m2, Weapons);

        SetEnemy(m2, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t2, 3);
        SetUnit(m2, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t2.Poll(true); t2.Poll(true);              // dead ticks 1-2: no hypothesis -> stays pending
        Assert.Empty(kills2);

        SetActive(m2, hp: 352, maxHp: 352, level: 99, acted: 1);   // the real actor finally latches
        t2.Poll(true);                              // dead tick 3 -> deadStreak>=3, real latch -> credit
        Assert.Equal(1, kills2.GetValueOrDefault(53));
    }

    [Fact]
    public void Pending_extension_inert_outside_any_period()
    {
        // Pins THE PERIOD GATE itself (KillTracker.Stamp.cs): same shape as the restaged part A
        // of Pending_extension_no_latch_fresh_hypothesis_credits_register, but acted stays 0
        // throughout -- NO acted-period ever opens. A fresh, Player-bridged, otherwise-valid
        // hypothesis is deliberately seeded (the register WOULD answer if consulted), yet the
        // pending-hole extension must stay inert: outside any period the register gate is closed
        // by construction (the V8 pin, KillTrackerTests.FirstKillFallback_falls_back_to_pure_TQ_
        // outside_any_period), and the stamp paths must live inside that invariant.
        // NON-VACUITY: deleting the `_periodOpen` gate in StampCulpritFromHypothesisOnly flips
        // this test (the extension would stamp P2 at the death edge and credit at deadStreak 3).
        const int p2Weapon = 22;
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: p2Weapon, nameId: 912);
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons);

        t.Poll(true);                 // priming (TQ never seeded: garbage tuple, acted stays 0)
        SetFrameNameId(m, P2, 912);
        PointAt(m, P2);
        t.Poll(true);                 // fresh Player arrival -- a live hypothesis, but no period

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);                 // enemy seenAlive
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true); t.Poll(true); t.Poll(true);   // deadStreak 1->3
        t.Poll(true); t.Poll(true);                 // +2 extra polls -- still nothing may credit

        Assert.Empty(kills);
    }

    [Fact]
    public void ResetBattle_clears_last_player_snapshot()
    {
        const int staleWeapon = 90, freshWeapon = 53;
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: staleWeapon, nameId: 905);
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);
        var t = new KillTracker(kills, m, Weapons);

        t.Poll(true);
        SetFrameNameId(m, P1, 905);
        PointAt(m, P1);
        t.Poll(true);                 // a real Player arrival -- snapshot populated

        t.ResetBattle();               // must clear the snapshot -- the stale pointer is STILL parked at P1

        // A fresh battle: a DIFFERENT player latches via the ordinary TQ path. The pointer memory
        // still reads P1's old address (ResetBattle does not touch raw ActorPtr contents), but the
        // register's re-priming rule (KillTrackerTests.Stale_pointer_after_reset_never_trusted)
        // must keep it inert -- proving the snapshot itself, not just the pointer trust, was cleared.
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: freshWeapon);
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        Settle(t, 3);                  // P2 latches [53] via TQ (register re-primes, untrusted)

        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(freshWeapon));
        Assert.False(kills.ContainsKey(staleWeapon));
    }

    // --- LW-1: the empty-latch sibling (StampCulpritFromEmptyLatch, KillTracker.Stamp.cs) ---
    //
    // Companion to the tape-replay tests above, but for the OTHER half of the death-edge stamp:
    // the acted-period latch resolved EMPTY (a roster player with no tracked weapon: the Boco/
    // Phoenix Down live case, docs/TODO.md LW-1). Before this fix the bury branch
    // (KillTracker.Corpses.cs's `else if (_latchResolvedEmpty && _latched)`) stamped
    // UntrackedReason.ActedLatch unconditionally; the fix consults the SAME KillerStamp register
    // hypothesis the armed-latch sibling already uses, and overrides only on a fresh, disagreeing,
    // ARMED arrival.

    [Fact]
    public void EmptyLatch_register_overrides_with_fresh_armed_arrival()
    {
        // LOAD-BEARING (LW-1). Boco (P1) is the acting player this acted-period; he holds no
        // tracked weapon, so the latch resolves EMPTY and freezes (_latchResolvedEmpty &&
        // _latched). acted then STAYS at 1 (the stuck-period pattern from the live tape), so the
        // empty latch never re-resolves. The engine actor pointer independently arrives on Ramza
        // (P2, armed) STRICTLY AFTER the empty latch's own resolve tick. Pre-fix this kill was
        // buried unconditionally; the fix credits Ramza's weapon instead.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();

        SetRoster(m, slot: 0, level: 30, brave: 40, faith: 40, weapon: 0xFFFF, nameId: 701);   // P1 Boco: unarmed
        SetUnit(m, P1, hp: 300, maxHp: 300, level: 30, brave: 40, faith: 40);
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: 53, nameId: 702);        // P2 Ramza: armed
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons, recorder: (type, payload) => recorded.Add((type, payload)));

        t.Poll(true);
        SetFrameNameId(m, P1, 701);
        PointAt(m, P1);
        t.Poll(true);                              // arrival on P1: strictly before the edge

        SetActive(m, hp: 300, maxHp: 300, level: 30, acted: 1);
        t.Poll(true);                              // period begins: Boco resolves EMPTY; latch freezes empty

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);                               // victim seenAlive

        // acted NEVER falls (the stuck-period pattern). The pointer moves on to P2, a fresh,
        // disagreeing, ARMED hypothesis that postdates Boco's resolve.
        SetFrameNameId(m, P2, 702);
        PointAt(m, P2);
        t.Poll(true);

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true);   // deadStreak=1: StampCulpritFromEmptyLatch overrides the empty latch with P2's [53]
        t.Poll(true);
        t.Poll(true);   // deadStreak=3 -> credit

        Assert.Equal(1, kills.GetValueOrDefault(53));
        Assert.Contains(recorded, r => r.type == "kill" && r.payload.StartsWith("stamp-override-empty-latch"));
    }

    [Fact]
    public void EmptyLatch_arrival_predating_resolve_stays_buried()
    {
        // Companion to the load-bearing test: P2's armed arrival happens well BEFORE Boco's empty
        // latch resolves (the ordering gate). The pointer then goes idle (entry 0) before Boco's
        // period begins, so Boco's own resolve runs the ordinary TQ fingerprint path (register
        // path closed: the idle owner is untrusted) and still lands on empty. P2's stale arrival
        // must be rejected: the bury holds byte-identical to today.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();

        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: 53, nameId: 703);   // P2 Ramza: armed, arrives FIRST
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        SetRoster(m, slot: 0, level: 30, brave: 40, faith: 40, weapon: 0xFFFF, nameId: 704); // P1 Boco: unarmed
        SetUnit(m, P1, hp: 300, maxHp: 300, level: 30, brave: 40, faith: 40);
        var t = new KillTracker(kills, m, Weapons, recorder: (type, payload) => recorded.Add((type, payload)));

        t.Poll(true);                               // priming (pointer unseeded)
        SetFrameNameId(m, P2, 703);
        PointAt(m, P2);
        t.Poll(true);                               // P2 arrives: well before Boco's period

        m.SeedU64(Offsets.ActorPtr, 0);
        t.Poll(true);                                // pointer goes idle: P2's snapshot stays, untouched

        SetActive(m, hp: 300, maxHp: 300, level: 30, acted: 1);
        t.Poll(true);                                // period begins: register path closed; TQ resolves Boco EMPTY

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true); t.Poll(true); t.Poll(true);   // deadStreak 1->3: P2's arrival predates the resolve -> bury

        Assert.Empty(kills);
        Assert.Contains(recorded, r => r.type == "kill" && r.payload.Contains("reason=untracked-weapon") && r.payload.Contains("via=ActedLatch"));
    }

    [Fact]
    public void EmptyLatch_fresh_arrival_also_unarmed_stays_buried()
    {
        // A fresh, postdating hypothesis that is ITSELF empty (the unarmed actor re-arriving via
        // the pointer, or an equally unarmed second guest) must stay buried: SameSet of two empty
        // sets is a Latch outcome, not a Register override. A dancer/summoner/unarmed guest stays
        // her own uncredited hypothesis, exactly as designed.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();

        SetRoster(m, slot: 0, level: 30, brave: 40, faith: 40, weapon: 0xFFFF, nameId: 705);   // P1 Boco: unarmed
        SetUnit(m, P1, hp: 300, maxHp: 300, level: 30, brave: 40, faith: 40);
        var t = new KillTracker(kills, m, Weapons, recorder: (type, payload) => recorded.Add((type, payload)));

        t.Poll(true);
        SetActive(m, hp: 300, maxHp: 300, level: 30, acted: 1);
        t.Poll(true);                                // period begins: Boco resolves EMPTY via TQ (no pointer yet)

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        // acted stays at 1. The pointer now arrives on Boco himself, fresh and postdating, but
        // still an EMPTY hypothesis (he is his own hypothesis).
        SetFrameNameId(m, P1, 705);
        PointAt(m, P1);
        t.Poll(true);

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true); t.Poll(true); t.Poll(true);   // deadStreak 1->3: empty hypothesis -> Latch outcome -> bury

        Assert.Empty(kills);
        Assert.Contains(recorded, r => r.type == "kill" && r.payload.Contains("reason=untracked-weapon") && r.payload.Contains("via=ActedLatch"));
    }

    [Fact]
    public void EmptyLatch_untrusted_register_stays_buried()
    {
        // The register never trusts an arrival at all (no ActorPtr ever seeded): TryHypothesis
        // never fires and the bury holds byte-identical to the pre-fix behavior.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();

        SetRoster(m, slot: 0, level: 30, brave: 40, faith: 40, weapon: 0xFFFF, nameId: 706);   // P1 Boco: unarmed
        SetUnit(m, P1, hp: 300, maxHp: 300, level: 30, brave: 40, faith: 40);
        SetActive(m, hp: 300, maxHp: 300, level: 30, acted: 1);
        var t = new KillTracker(kills, m, Weapons, recorder: (type, payload) => recorded.Add((type, payload)));

        Settle(t, 3);                                 // Boco's period begins and resolves EMPTY via TQ
        AliveThenDead(m, slot: 0, t);

        Assert.Empty(kills);
        Assert.Contains(recorded, r => r.type == "kill" && r.payload.Contains("reason=untracked-weapon") && r.payload.Contains("via=ActedLatch"));
    }

    [Fact]
    public void EmptyLatch_period_closed_stays_buried_via_private_gate()
    {
        // Structural note: _latched and _periodOpen are set/cleared in lockstep everywhere in
        // KillTracker.cs (both flip together at the debounced acted-falling edge and in
        // ResetBattle), so the callsite condition (_latchResolvedEmpty && _latched) can never
        // naturally fire with _periodOpen false through Poll() alone. StampCulpritFromEmptyLatch's
        // own period gate mirrors the sibling sites defensively regardless; this test pins that
        // guard directly through the private surface, the only way to exercise it.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: 53, nameId: 707);   // an otherwise-valid armed hypothesis
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons);

        t.Poll(true);
        SetFrameNameId(m, P2, 707);
        PointAt(m, P2);
        t.Poll(true);   // a fresh Player arrival exists: would answer Register if the gate were open

        typeof(KillTracker).GetField("_periodOpen", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(t, false);
        var method = typeof(KillTracker).GetMethod("StampCulpritFromEmptyLatch", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(t, new object[] { 0 });

        var lethalUntracked = (UntrackedReason[])typeof(KillTracker)
            .GetField("_lethalUntracked", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(t)!;
        Assert.Equal(UntrackedReason.ActedLatch, lethalUntracked[0]);
    }

    // --- LW-63 stage 2: the flags-first hypothesis lane at the death-edge stamp (KillerStamp.cs's
    // TryHypothesis, D4). Companion to the tape-replay/safety-net tests above (which all pin the
    // REGISTER-snapshot lane byte-identically -- none of them seed any turn flags, so they double
    // as the "no flags owner" regression pin for plan test 13). ---

    [Fact]
    public void FlagsStamp_overrides_stale_latch_with_no_register_arrival_at_all()
    {
        // Plan test 11: A's weapon latches (stale); B (a DIFFERENT player) holds t=1 AND a=1 at
        // the death edge (the STAMP KEY, D4) -- StampCulprit overrides the stale latch to credit
        // B, with NO register arrival seeded at all (proving the flags-first stamp lane needs
        // none: this is the StampCulprit path, register-free).
        const int wa = 60, wb = 37;
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: wa, nameId: 801);   // A
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: wb, nameId: 802);   // B
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons);

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        Settle(t, 3);                              // A latches [wa] via TQ (no register seeded)

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);                               // victim seenAlive

        // acted stays stuck at 1 (A's period never closes). B's turn flags name him at the edge.
        SetFrameNameId(m, P2, 802);
        long p2Entry = Band.Entry(P2);
        m.U8s[p2Entry + Offsets.ATurnFlag] = 1;
        m.U8s[p2Entry + Offsets.AActed] = 1;

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true);   // deadStreak=1: StampCulprit's flags-first hypothesis overrides A -> B
        t.Poll(true);
        t.Poll(true);   // deadStreak=3 -> credit

        Assert.Equal(1, kills.GetValueOrDefault(wb));
        Assert.False(kills.ContainsKey(wa));
    }

    [Fact]
    public void FlagsStamp_bury_when_flag_owner_is_unarmed_and_stamps_TurnFlags_reason()
    {
        // Plan test 12: the flags-named TRUE actor (B) holds no tracked weapon (a=1, bare
        // hands) -- StampCulprit must bury the kill rather than fall back to the stale latch
        // (A), and the verdict stamps UntrackedReason.TurnFlags so the console/flight evidence
        // names the turn flags as the resolve source (not the actor register).
        const int wa = 60;
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: wa, nameId: 803);       // A: stale latch
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: 0xFFFF, nameId: 804);   // B: bare hands, true actor
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons, recorder: (type, payload) => recorded.Add((type, payload)));

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        Settle(t, 3);                              // A latches [wa]

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        SetFrameNameId(m, P2, 804);
        long p2Entry = Band.Entry(P2);
        m.U8s[p2Entry + Offsets.ATurnFlag] = 1;
        m.U8s[p2Entry + Offsets.AActed] = 1;

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true); t.Poll(true); t.Poll(true);   // deadStreak 1->3: bury, not the stale latch

        Assert.Empty(kills);
        Assert.Contains(recorded, r => r.type == "kill" && r.payload.StartsWith("stamp-bury") && r.payload.Contains("src=flags"));

        var lethalUntracked = (UntrackedReason[])typeof(KillTracker)
            .GetField("_lethalUntracked", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(t)!;
        Assert.Equal(UntrackedReason.TurnFlags, lethalUntracked[0]);
    }

    [Fact]
    public void FlagsStamp_pending_extension_credits_the_flag_owner_with_no_latch_at_all()
    {
        // Plan test 14 (StampCulpritFromHypothesisOnly parity). The acted-period opens on a
        // PERMANENTLY garbage TQ tuple and no register arrival is ever seeded, so
        // _lastPlayerWeapons stays empty and _latched stays false for the whole test: the main
        // latch never gets a chance at the flags owner because P2's flags only become valid on
        // the SAME tick the period debounce-falls (acted -> 0), when the main latch's own
        // Acted==1 block does not even run. Only the death-edge pending-hole extension
        // (StampCulpritFromHypothesisOnly) consults the flags-first hypothesis and stamps P2.
        const int p2Weapon = 22;
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: p2Weapon, nameId: 950);
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons);

        SetActive(m, hp: 0, maxHp: 0, level: 0, acted: 1);   // period opens on a garbage tuple
        t.Poll(true);                 // BeginActedPeriod; TQ garbage, no flags for P2 yet -> unlatched

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);                 // enemy seenAlive; still unlatched every tick (P2 flags absent)

        // P2's flags become valid on the SAME tick the period debounce-falls and the corpse
        // dies -- the main latch's Acted==1 block never runs this tick, so only the death-edge
        // stamp ever sees the flags hypothesis.
        SetFrameNameId(m, P2, 950);
        long p2Entry = Band.Entry(P2);
        m.U8s[p2Entry + Offsets.ATurnFlag] = 1;
        m.U8s[p2Entry + Offsets.AActed] = 1;
        SetActive(m, hp: 0, maxHp: 0, level: 0, acted: 0);
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true);   // deadStreak=1: StampCulpritFromHypothesisOnly stamps P2 from the flags hypothesis
        t.Poll(true);   // deadStreak=2
        t.Poll(true);   // deadStreak=3 -> credit

        Assert.Equal(1, kills.GetValueOrDefault(p2Weapon));
    }

    [Fact]
    public void FlagsStamp_refuses_the_freshly_opened_next_owner_during_the_debounce_tail()
    {
        // Plan test 18 (review finding 3), THE non-vacuous negative for the stamp lane. A
        // latches and kills; the period then enters its 3-tick close debounce (acted has fallen
        // to 0, but _periodOpen has not yet flipped false). WITHIN that tail, B's freshly-opened
        // next turn reads t1 m0 a0 (engine-reset at turn open) -- the stamp lane's a==1
        // requirement must refuse B (no register hypothesis is seeded either), leaving A's latch
        // to govern via the plain Latch outcome. Deleting the a==1 requirement (testing t==1
        // alone) would wrongly credit B instead of A.
        const int wa = 60, wb = 37;
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: wa, nameId: 811);   // A: true killer
        SetUnit(m, P1, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: wb, nameId: 812);   // B: innocent next owner
        SetUnit(m, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        var t = new KillTracker(kills, m, Weapons);

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        Settle(t, 3);                              // A latches [wa] via TQ

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);                               // victim seenAlive

        // A's period debounce-falls (acted -> 0); B's turn opens FRESH this same tick (t1 m0 a0).
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        SetFrameNameId(m, P2, 812);
        long p2Entry = Band.Entry(P2);
        m.U8s[p2Entry + Offsets.ATurnFlag] = 1;   // B's menu is open...
        m.U8s[p2Entry + Offsets.AMoved] = 0;      // ...freshly (engine-reset at turn open)...
        m.U8s[p2Entry + Offsets.AActed] = 0;      // ...and he has NOT acted -- the stamp key must refuse

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true);   // deadStreak=1, period still technically open (debounce tail): the flags
                        // hypothesis refuses (a=0 on B) -> Latch governs
        t.Poll(true);
        t.Poll(true);   // deadStreak=3 -> credit

        Assert.Equal(1, kills.GetValueOrDefault(wa));
        Assert.False(kills.ContainsKey(wb));
    }
}
