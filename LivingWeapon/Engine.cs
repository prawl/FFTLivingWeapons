using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivingWeapon;

/// <summary>
/// The Living Weapon runtime. One background loop: in battle it counts kills and
/// applies stat growth; the per-weapon tally (<see cref="KillTally"/>) persists
/// across sessions. Detection and growth share the in-memory tally (no IPC).
/// Display painting is layered on top.
/// </summary>
internal sealed class Engine
{
    // Poll fast: at fast-forward a death's hp==0 window is brief, and a 100ms loop sailed
    // past it, missing kills. The kill scan is cheap; growth (heavier) runs every Nth tick.
    private const int PollMs = 33;
    private const int GrowthEveryNTicks = 3;   // ~100ms; stat-hold doesn't need 33ms
    private int _tick;

    private readonly KillTally _tally;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly TurnTracker _turns;
    private readonly GrowthEngine _growth;
    private readonly CharmLock _charm;     // named: Heartbeat is engine-driven, outside the module contract
    private readonly Barrage _barrage;     // named: ticks in AND out of battle (learn-screen hold), pre-gate
    private readonly TreasureMaster _treasure;
    private readonly ISignature[] _signatures;        // every signature module, battle-exit reset order
    private readonly ISignature[] _fieldSignatures;   // the in-battle tick order (Barrage ticks pre-gate instead)
    private readonly Display _display;
    private readonly BattleState _battle = new();      // debounced in/out edges (slot9 sticks; mode flickers)
    private CancellationTokenSource? _cts;
    private DateTime _lastField = DateTime.MinValue;   // last tick we were on the live battlefield
    private const double FieldSettleSeconds = 1.5;      // wait this long off-field before painting
    private bool _lastBattleStatus;                     // edge-detect entering the in-battle status card

    public Engine(string modDir)
    {
        _tally = KillTally.Load(Path.Combine(modDir, "kills.json"));
        _kills = _tally.Kills;
        var meta = MetaLoader.Load(modDir);
        if (Tuning.DevSeedAllKills)   // DEV build: every weapon starts at max tier for fast verification
        {
            Tuning.SeedKills(meta.Keys, _kills, Tuning.DevKillSeed);
            Log.Info($"DEV: force-seeded all {meta.Count} weapons to at least {Tuning.DevKillSeed} kills -- every weapon starts with +3 effects active for fast testing.");
        }
        var live = new LiveMemory();   // the ONE production IGameMemory, shared by every subsystem
        _turns = new TurnTracker(live);
        _tracker = new KillTracker(_kills, live, new HashSet<int>(meta.Keys),
                                   new BattleLog(Tuning.VerboseEvents));
        _growth = new GrowthEngine(meta, _kills, _turns, live);
        _charm = new CharmLock(meta, _kills, live);                 // Galewind +3: one charm held unbreakable (own-CT turns)
        _barrage = new Barrage(meta, _kills, live);                 // Yoichi +3: grant Barrage command to the wielder
        var extra = new ExtraTurn(_kills, live);                    // Zwill +3: a kill grants the killer an immediate extra turn
        var eagle = new EagleEye(meta, _kills, live);               // Eclipsebolt +3: hasten any enemy Doom to a 1-turn countdown
        var ricochet = new Ricochet(meta, _kills, _tracker, live);  // Stormarc +3: bounce chip to nearest other enemy
        var maim = new Maim(meta, _kills, _tracker, live);          // Huntress +3: struck enemies lose reactions N turns
        var plague = new Plague(meta, _kills, _tracker, mem: live); // Venombolt +3: poison never fades, ticks harder
        var lifeSap = new LifeSap(meta, _kills, mem: live);         // Umbral +3: a kill heals the wielder 25% max HP
        var wyrmblood = new Wyrmblood(meta, _kills, _turns, live);  // Dragon Rod +3: turn-edge regen splash (1 tile)
        var rapture = new Rapture(meta, _kills, _turns, live);      // Rod of Faith +3: low-HP Master Teleportation window
        var font = new SpiritualFont(meta, _kills, _tracker, live); // Wellspring +3: a moved action restores HP and MP
        var treasureJson = Path.Combine(modDir, "treasure.json");
        _treasure = new TreasureMaster(
            load:         () => TreasureDb.Load(modDir),
            datasetStamp: () => { try { return File.GetLastWriteTimeUtc(treasureJson); }
                                  catch { return null; } },
            mem: live);
        _treasure.StartFastHold();
        // Both orders are load-bearing and preserved verbatim from the hand-wired era:
        // reset runs charm..font with Barrage between Plague and LifeSap; the in-battle tick
        // excludes Barrage (it ticks before the !nowIn early-return, learn screens included).
        // TreasureMaster is tail-appended to both arrays (order is load-bearing -- append only).
        _signatures = new ISignature[] { _charm, extra, eagle, ricochet, maim, plague, _barrage, lifeSap, wyrmblood, rapture, font, _treasure };
        _fieldSignatures = new ISignature[] { _charm, extra, eagle, ricochet, maim, plague, lifeSap, wyrmblood, rapture, font, _treasure };
        _display = new Display(meta, _kills, live);
        LogNames.Init(meta);
        Log.Info($"loaded {meta.Count} weapon types; {_tally.Total} total kills in the tally.");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            Log.Info("runtime loop started.");
            while (!token.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception ex) { Log.Error("tick: " + ex.Message); }
                try { await Task.Delay(PollMs, token); } catch { }
            }
        }, token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private void Tick()
    {
        uint slot0 = Mem.U32(Offsets.Slot0);
        uint slot9 = Mem.U32(Offsets.Slot9);
        int battleMode = Mem.U8(Offsets.BattleMode);
        bool paused = Mem.U8(Offsets.PauseFlag) == 1;
        int eventId = Mem.U16(Offsets.EventId);   // out-of-live: dialogue/cutscene; in-combat: nameId alias
        var now = DateTime.Now;
        // Enter is instant; exit is debounced (battleMode flickers, slot9 sticks). nowIn is sticky
        // through mid-battle dips, flipping only on the debounced edges.
        BattleEdge edge = _battle.Step(slot0, slot9, battleMode, paused, eventId, now);
        bool nowIn = _battle.In;
        bool onField = BattleState.OnField(nowIn, battleMode);
        if (onField) _lastField = now;
        // A genuine in-battle frame: live modes, or the slot0 marker with a paused/event excuse
        // (the marker alone lies -- it sticks at 0xFF after a battle QUIT). Feeds the heartbeat
        // and gates every module that writes battle memory.
        bool inLive = BattleState.InLiveBattle(slot0, battleMode, paused, eventId);
        if (inLive) _charm.Heartbeat(now);
        if (edge == BattleEdge.Entered)
            Log.Info($"battle: started (slot0={slot0:X} slot9={slot9:X} mode={battleMode})");

        // In-battle "Status" card (a paused, stable menu) -- paint the counter there too.
        bool battleStatus = BattleState.StatusCardOpen(nowIn, battleMode,
                                                       Mem.U8(Offsets.PauseFlag) == 1, Mem.U8(Offsets.SubmenuFlag) == 1);
        if (battleStatus && !_lastBattleStatus) _display.Invalidate();   // re-find the card's fresh buffers
        _lastBattleStatus = battleStatus;

        if (edge == BattleEdge.Exited)
        {
            Log.Info($"battle: ended -- saving kill tally, resetting battle trackers (slot0={slot0:X} slot9={slot9:X} mode={battleMode} paused={paused} event={eventId})");
            _tracker.ResetBattle();
            _turns.ResetBattle();
            _growth.ResetBattle();
            foreach (var sig in _signatures) sig.ResetBattle();
            _tally.Save();               // flush on battle end
            _display.Invalidate();       // re-find the menu's freshly-allocated render copies
        }
        // Barrage runs in AND out of battle: the learn screen / pre-battle menus read the
        // JobCommand table live, and the learned bit needs its hold against menu writebacks.
        _barrage.Tick();
        if (!nowIn)
        {
            _display.Tick(false);   // out of battle (slot9 cleared): keep the equip card painted
            return;
        }

        bool changed = _tracker.Poll(onField);   // every ~33ms tick so fast-forward deaths aren't missed
        _turns.Poll();                        // edge-detect each unit's turns (for timed signatures)
        var ctx = new TickContext(now, onField, inLive);
        foreach (var sig in _fieldSignatures) sig.Tick(in ctx);
        if (_tick++ % GrowthEveryNTicks == 0) _growth.Apply();   // growth holds stats; ~100ms is plenty
        if (changed) _tally.Save();

        // slot9 is still the battle sentinel, but once we've been OFF the live battlefield
        // for a beat (battleMode 0 = world-map party menu / post-battle), paint the card.
        if (BattleState.ShouldPaintCard(battleStatus, onField, (now - _lastField).TotalSeconds, FieldSettleSeconds))
            _display.Tick(true);
    }
}
