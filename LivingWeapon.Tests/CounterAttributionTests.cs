using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Counter-kill attribution -- when a player counters during an enemy's turn and kills,
/// the PREVIOUS player's stale latch must NOT be credited. The kill goes uncredited
/// (miss beats mis-credit). The fix reads TqTeam at the alive->dead edge (deadStreak 0->1)
/// and diverts to _lethalUntracked when team is a confident non-player value (1=enemy,
/// 2=ally/guest). The tracked-delayed path (Jump/Charge snapshot) still wins over the divert:
/// ConsumeDelayedCulprit() is checked first at credit time and the no-credit branch is gated
/// `delayed == null`, so a Jump landing during an enemy turn still credits the jumper.
/// </summary>
public class CounterAttributionTests
{
    private static readonly HashSet<int> Weapons = new() { 35, 90 };

    private const int ArmedWeapon  = 35;   // player A's weapon -- the stale latch at counter time
    private const int JumperWeapon = 90;   // jumper P's weapon -- delayed-action path (T3)

    // Player band slot (player-side: index >= SlotsBack=20)
    private const int ASlot = Offsets.SlotsBack;   // slot 20

    // Enemy band slot (enemy-side: <= EnemySlotMax=19)
    private const int EnemySlot = 0;

    // --- helpers (mirror SummonerAttributionTests style) ---

    /// <param name="team">TqTeam value: 0=player, 1=enemy, 2=ally/guest; any other value
    /// is treated as "unknown" and takes the normal credit path (fail-safe).</param>
    private static void SetActive(FakeSparseMemory m, int hp, int maxHp, int level, int acted = 1, int team = 0)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam]  = (ushort)team;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp]    = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
        m.U8s[Offsets.Acted] = (byte)acted;
    }

    private static void SetRoster(FakeSparseMemory m, int slot, int level, int brave, int faith, int weapon)
        => MemSeats.SeatRoster(m, slot, level, brave, faith, weapon);

    private static void SetUnit(FakeSparseMemory m, int bandSlot, int hp, int maxHp = 400,
                                int level = 10, int brave = 50, int faith = 50)
        => MemSeats.SeatBand(m, bandSlot, weapon: 0, lvl: level, br: brave, fa: faith,
                             gx: 5, gy: 5, hp: hp, maxHp: maxHp);

    private static void SetEnemy(FakeSparseMemory m, int bandSlot, int hp, int maxHp = 400,
                                 int level = 10, int brave = 50, int faith = 50)
    {
        MemSeats.SeatBand(m, bandSlot, weapon: 0, lvl: level, br: brave, fa: faith,
                          gx: 5, gy: 5, hp: hp, maxHp: maxHp);
        if (bandSlot <= Offsets.EnemySlotMax)
        {
            long s = Offsets.ArrayReadBase + (long)bandSlot * Offsets.ArrayStride;
            m.U16s[s + Offsets.AInBattle] = 1;
            m.U8s[s + Offsets.ALevel]     = (byte)level;
            m.U8s[s + Offsets.ABrave]     = (byte)brave;
            m.U8s[s + Offsets.AFaith]     = (byte)faith;
            m.U16s[s + Offsets.AMaxHp]    = (ushort)maxHp;
        }
    }

    private static void SetJumpBit(FakeSparseMemory m, int bandSlot, bool set = true)
    {
        long addr = Band.Entry(bandSlot);
        byte cur = m.U8s.TryGetValue(addr + Offsets.ADeadStatus, out var v) ? v : (byte)0;
        m.U8s[addr + Offsets.ADeadStatus] = set
            ? (byte)(cur | Offsets.AJumpBit)
            : (byte)(cur & ~Offsets.AJumpBit);
    }

    private static void Settle(KillTracker t, int n = 3)
    {
        for (int i = 0; i < n; i++) t.Poll(true);
    }

    // --- T1: HEADLINE -- counter kill during enemy turn not credited to stale latch ---

    [Fact]
    public void Counter_kill_during_enemy_turn_is_not_credited_to_stale_latch()
    {
        // T1 HEADLINE: A (weapon 35) acts on team=0, latches [ArmedWeapon]. A's period ends.
        // An enemy then acts (team=1, acted=1, distinct fingerprint/no roster entry) -- the
        // scenario where Melioudoul counters on an enemy's turn. TryResolveActingPlayer fails
        // for the enemy -> _latched stays false; _lastPlayerWeapons=[ArmedWeapon] (stale).
        // The enemy victim dies while team=1: deadStreak==1 -> nonPlayerTurn -> _lethalUntracked
        // stamped -> no-credit fires at deadStreak==3.
        // Non-vacuous: without the team gate, _lethalActor=[ArmedWeapon] -> kills[ArmedWeapon]=1.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        m.ReadableAddrs.Add(Offsets.TurnQueue + Offsets.TqTeam);   // enable team read at death edge

        // A: tracked player (weapon ArmedWeapon).
        SetRoster(m, slot: 0, level: 90, brave: 80, faith: 70, weapon: ArmedWeapon);
        SetUnit(m, ASlot, hp: 352, maxHp: 352, level: 90, brave: 80, faith: 70);

        var t = new KillTracker(kills, m, Weapons);

        // Phase 1: A acts (team=0) -> latch [ArmedWeapon].
        SetActive(m, hp: 352, maxHp: 352, level: 90, acted: 1, team: 0);
        Settle(t, 3);

        // Phase 2: A's period ends -> _latched=false; _lastPlayerWeapons=[ArmedWeapon] stays (stale).
        SetActive(m, hp: 352, maxHp: 352, level: 90, acted: 0, team: 0);
        Settle(t, KillTracker.UnfreezeTicks);

        // Phase 3: enemy victim seen alive (seenAlive requires 3 consecutive alive ticks).
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // Phase 4: enemy turn (team=1). Distinct hp/maxHp/level from A, no roster entry ->
        // TryResolveActingPlayer fails -> stale latch persists. Victim dies on the same tick.
        // At deadStreak==1: team=1 -> nonPlayerTurn=true -> no-credit (fix).
        SetActive(m, hp: 150, maxHp: 250, level: 20, acted: 1, team: 1);
        SetUnit(m, EnemySlot, hp: 0);
        t.Poll(true);   // deadStreak=1: nonPlayerTurn -> _lethalUntracked[EnemySlot]=true
        t.Poll(true);   // deadStreak=2
        t.Poll(true);   // deadStreak=3 -> no-credit fires

        Assert.Empty(kills);   // Reis (stale latch) must NOT be credited Melioudoul's counter-kill
    }

    // --- T2: REGRESSION -- identical but team=0: stale latch IS credited normally ---

    [Fact]
    public void Normal_kill_on_player_turn_still_credits_stale_latch()
    {
        // T2 REGRESSION: same setup as T1 but the victim dies while team=0 (player turn or idle).
        // nonPlayerTurn=false -> _lethalActor stamps [ArmedWeapon] -> credit fires at deadStreak==3.
        // Proves the gate only diverts on a non-player team and leaves the normal path intact.
        // Non-vacuous: if the gate misfired on team=0, kills would be empty.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        m.ReadableAddrs.Add(Offsets.TurnQueue + Offsets.TqTeam);

        SetRoster(m, slot: 0, level: 90, brave: 80, faith: 70, weapon: ArmedWeapon);
        SetUnit(m, ASlot, hp: 352, maxHp: 352, level: 90, brave: 80, faith: 70);

        var t = new KillTracker(kills, m, Weapons);

        // A acts (team=0) -> latch [ArmedWeapon].
        SetActive(m, hp: 352, maxHp: 352, level: 90, acted: 1, team: 0);
        Settle(t, 3);

        // A's period ends.
        SetActive(m, hp: 352, maxHp: 352, level: 90, acted: 0, team: 0);
        Settle(t, KillTracker.UnfreezeTicks);

        // Enemy seen alive.
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // Enemy dies while team=0 (TqTeam unchanged from phase 2): stale latch applies normally.
        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 3);   // deadStreak 1->3: team=0 -> _lethalActor=[ArmedWeapon] -> credit

        Assert.Equal(1, kills.GetValueOrDefault(ArmedWeapon));
    }

    // --- T3: DELAYED PRIORITY -- Jump credits over the team gate ---

    [Fact]
    public void Delayed_actor_credits_over_counter_team_gate()
    {
        // T3 DELAYED PRIORITY: P (JumperWeapon=90) commits a Jump on team=0 -> latch+snapshot.
        // P's period ends. Jump bit clears (lands) -> tracked arm: _delayedActor=[JumperWeapon].
        // Enemy then acts (team=1). Enemy victim dies while team=1: deadStreak==1 -> nonPlayerTurn
        // -> _lethalUntracked stamped. At credit: ConsumeDelayedCulprit() fires FIRST (delayed!=null)
        // -> _lethalUntracked no-credit branch is skipped -> JumperWeapon credited.
        // Non-vacuous: checking _lethalUntracked before delayed (removing the delayed==null guard)
        // would suppress the delayed actor -> kills empty.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        m.ReadableAddrs.Add(Offsets.TurnQueue + Offsets.TqTeam);

        // P: tracked jumper (JumperWeapon=90).
        SetRoster(m, slot: 0, level: 99, brave: 89, faith: 76, weapon: JumperWeapon);
        SetUnit(m, ASlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);

        var t = new KillTracker(kills, m, Weapons);

        // (1) P acts (team=0) + Jump bit sets -> latch [JumperWeapon]; TrackDelayed snapshots.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1, team: 0);
        SetJumpBit(m, ASlot, set: true);
        Settle(t, 3);   // latch fires tick 1; snapshot on tick 1 (fp match + weapons non-empty)

        // (2) Enemy seen alive.
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // (3) P's period ends.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0, team: 0);
        Settle(t, KillTracker.UnfreezeTicks);

        // (4) Jump bit clears (lands): TrackDelayed arms _delayedActor=[JumperWeapon].
        SetJumpBit(m, ASlot, set: false);
        t.Poll(true);   // arm fires; _delayedArmedTicks=Window

        // (5) Enemy turn (team=1, acted=1, distinct fingerprint/no roster entry). Resolve fails
        // -> _latched stays false; _lastPlayerWeapons=[JumperWeapon] (stale from P's turn).
        SetActive(m, hp: 150, maxHp: 250, level: 20, acted: 1, team: 1);
        t.Poll(true);

        // (6) Enemy victim dies while team=1: deadStreak==1 -> nonPlayerTurn -> _lethalUntracked.
        // At deadStreak==3: delayed=[JumperWeapon] != null -> _lethalUntracked branch skipped -> credit.
        SetUnit(m, EnemySlot, hp: 0);
        t.Poll(true);   // deadStreak=1; _lethalUntracked[EnemySlot]=true
        t.Poll(true);   // deadStreak=2
        t.Poll(true);   // deadStreak=3 -> ConsumeDelayedCulprit -> [JumperWeapon] wins

        Assert.Equal(1, kills.GetValueOrDefault(JumperWeapon));
        Assert.Equal(1, kills.Count);   // no spurious credits
    }

    // --- T4: FAIL-SAFE -- garbage team value 3 takes the normal credit path ---

    [Fact]
    public void Garbage_team_value_takes_normal_credit_path()
    {
        // T4 FAIL-SAFE: same as T1 but TqTeam reads 3 (garbage, not 0/1/2) at the death edge.
        // nonPlayerTurn=(3==1||3==2)=false -> _lethalActor=[ArmedWeapon] -> credit fires normally.
        // Proves that only a confident non-player value (1 or 2) triggers the divert; any
        // other value -- including a bad/unreadable read defaulting to 0 -- takes the normal
        // credit path so a hardware glitch never wrongly suppresses a legit player kill.
        // Non-vacuous: hard-coding nonPlayerTurn=true would suppress the credit here.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        m.ReadableAddrs.Add(Offsets.TurnQueue + Offsets.TqTeam);

        SetRoster(m, slot: 0, level: 90, brave: 80, faith: 70, weapon: ArmedWeapon);
        SetUnit(m, ASlot, hp: 352, maxHp: 352, level: 90, brave: 80, faith: 70);

        var t = new KillTracker(kills, m, Weapons);

        // A acts (team=0) -> latch [ArmedWeapon].
        SetActive(m, hp: 352, maxHp: 352, level: 90, acted: 1, team: 0);
        Settle(t, 3);

        // A's period ends.
        SetActive(m, hp: 352, maxHp: 352, level: 90, acted: 0, team: 0);
        Settle(t, KillTracker.UnfreezeTicks);

        // Enemy seen alive.
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // At the death edge, TqTeam reads garbage=3 (readable but not a known team value).
        // team=3 -> nonPlayerTurn=false -> normal _lethalActor path -> credit.
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 3;
        SetUnit(m, EnemySlot, hp: 0);
        t.Poll(true);   // deadStreak=1: team=3, nonPlayerTurn=false -> _lethalActor=[ArmedWeapon]
        t.Poll(true);   // deadStreak=2
        t.Poll(true);   // deadStreak=3 -> credit

        Assert.Equal(1, kills.GetValueOrDefault(ArmedWeapon));
    }
}
