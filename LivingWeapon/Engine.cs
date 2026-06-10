using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivingWeapon;

/// <summary>
/// The Living Weapon runtime. One background loop: in battle it counts kills and
/// applies stat growth; the per-weapon tally persists across sessions. Detection
/// and growth share the in-memory tally (no IPC). Display painting is layered on
/// top in a later phase. Tally persistence lives in Engine.Tally.cs.
/// </summary>
internal sealed partial class Engine
{
    // Poll fast: at fast-forward a death's hp==0 window is brief, and a 100ms loop sailed
    // past it, missing kills. The kill scan is cheap; growth (heavier) runs every Nth tick.
    private const int PollMs = 33;
    private const int GrowthEveryNTicks = 3;   // ~100ms; stat-hold doesn't need 33ms
    private int _tick;

    private readonly string _tallyPath;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly TurnTracker _turns;
    private readonly GrowthEngine _growth;
    private readonly CharmLock _charm;
    private readonly ExtraTurn _extra;
    private readonly EagleEye _eagle;
    private readonly Ricochet _ricochet;
    private readonly Maim _maim;
    private readonly Barrage _barrage;
    private readonly LifeSap _lifeSap;
    private readonly Wyrmblood _wyrmblood;
    private readonly Rapture _rapture;
    private readonly SpiritualFont _font;
    private readonly Display _display;
    private readonly BattleState _battle = new();      // debounced in/out edges (slot9 sticks; mode flickers)
    private CancellationTokenSource? _cts;
    private DateTime _lastField = DateTime.MinValue;   // last tick we were on the live battlefield
    private const double FieldSettleSeconds = 1.5;      // wait this long off-field before painting
    private bool _lastBattleStatus;                     // edge-detect entering the in-battle status card

    public Engine(string modDir)
    {
        _tallyPath = Path.Combine(modDir, "kills.json");
        _kills = LoadTally(_tallyPath);
        var meta = MetaLoader.Load(modDir);
        if (Tuning.DevSeedAllKills)   // DEV build: every weapon starts at max tier for fast verification
        {
            Tuning.SeedKills(meta.Keys, _kills, Tuning.DevKillSeed);
            Log.Info($"DEV: seeded {meta.Count} weapons to >= {Tuning.DevKillSeed} kills (one kill from P3).");
        }
        _turns = new TurnTracker(new LiveMemory());
        _tracker = new KillTracker(_kills, new LiveMemory(), new HashSet<int>(meta.Keys),
                                   new BattleLog(Tuning.VerboseEvents));
        _growth = new GrowthEngine(meta, _kills, _turns);
        _charm = new CharmLock(meta, _kills);   // counts turns off the target's own CT (not TurnTracker)
        _extra = new ExtraTurn(_kills);         // Zwill +3: a kill grants the killer an immediate extra turn
        _eagle = new EagleEye(meta, _kills);    // Eclipsebolt +3: hasten any enemy Doom to a 1-turn countdown
        _ricochet = new Ricochet(meta, _kills, _tracker);  // Stormarc +3: bounce chip to nearest other enemy
        _maim = new Maim(meta, _kills, _tracker);          // Huntress +3: struck enemies lose reactions N turns
        _barrage = new Barrage(meta, _kills);              // Yoichi +3: grant Barrage command to the wielder
        _lifeSap = new LifeSap(meta, _kills);              // Umbral +3: a kill heals the wielder 25% max HP
        _wyrmblood = new Wyrmblood(meta, _kills, _turns);  // Dragon Rod +3: turn-edge regen splash (1 tile)
        _rapture = new Rapture(meta, _kills, _turns);      // Rod of Faith +3: low-HP Master Teleportation window
        _font = new SpiritualFont(meta, _kills, _turns);   // Wellspring +3: a moved turn restores HP and MP
        _display = new Display(meta, _kills);
        Log.Info($"loaded {meta.Count} weapon metas; {Sum(_kills)} kills in tally.");
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
        // slot9 stays stuck on the world-map party menu, so it can't tell combat from a
        // menu. battleMode does: 2/3/4 = live battlefield, 0 = world map / menus.
        bool onField = nowIn && (battleMode == 2 || battleMode == 3 || battleMode == 4);
        if (onField) _lastField = now;
        // Heartbeat on ANY genuine in-battle frame -- slot0==0xFF covers cast/attack targeting
        // (battleMode 1/5), where gating on {2,3,4} alone starves the beat and false-drops a live
        // lock the moment the player dwells on a target. Goes quiet only on the post-battle world map.
        if (CharmLock.InLiveBattle(slot0, battleMode)) _charm.Heartbeat(now);
        if (edge == BattleEdge.Entered)
            Log.Info($"battle: enter slot0={slot0:X} slot9={slot9:X} mode={battleMode}");

        // In-battle "Status" card (a paused, stable menu) -- paint the counter there too.
        // Open status card = paused submenu in the action-menu context (battleMode 3).
        // menuCursor is the card's own cursor once open (not 3), so don't gate on it.
        bool battleStatus = nowIn && battleMode == 3
                            && Mem.U8(Offsets.PauseFlag) == 1 && Mem.U8(Offsets.SubmenuFlag) == 1;
        if (battleStatus && !_lastBattleStatus) _display.Invalidate();   // re-find the card's fresh buffers
        _lastBattleStatus = battleStatus;

        if (edge == BattleEdge.Exited)
        {
            Log.Info($"battle: exit slot0={slot0:X} slot9={slot9:X} mode={battleMode} paused={paused} event={eventId}");
            _tracker.ResetBattle();
            _turns.ResetBattle();
            _growth.ResetBattle();
            _charm.ResetBattle();
            _extra.ResetBattle();
            _eagle.ResetBattle();
            _ricochet.ResetBattle();
            _maim.ResetBattle();
            _barrage.ResetBattle();
            _lifeSap.ResetBattle();
            _wyrmblood.ResetBattle();
            _rapture.ResetBattle();
            _font.ResetBattle();
            SaveTally();                 // flush on battle end
            _display.Invalidate();       // re-find the menu's freshly-allocated render copies
        }
        // Barrage runs in AND out of battle: the learn screen / pre-battle menus read the
        // JobCommand table live, and the learned bit needs its hold against menu writebacks.
        _barrage.Tick();
        if (!nowIn)
        {
            _display.Tick();   // out of battle (slot9 cleared): keep the equip card painted
            return;
        }

        bool changed = _tracker.Poll(onField);   // every ~33ms tick so fast-forward deaths aren't missed
        _turns.Poll();                        // edge-detect each unit's turns (for timed signatures)
        _charm.Tick(now);                     // charm-lock: hold/clear each tick to beat the on-hit clear
        _extra.Tick(now);                     // Zwill +3: on a kill, slam the killer's CT to 100 (extra turn)
        _eagle.Tick();                        // Eclipsebolt +3: force enemy Doom countdowns down to 1
        _ricochet.Tick(onField);              // Stormarc +3: bounce chip to nearest other enemy on damage
        _maim.Tick(onField);                  // Huntress +3: struck enemies lose reactions for N turns
        _lifeSap.Tick();                      // Umbral +3: a kill restores the wielder 25% of max HP
        _wyrmblood.Tick(onField);             // Dragon Rod +3: wielder turn edge mends self + adjacent allies
        _rapture.Tick(onField);               // Rod of Faith +3: below 30% HP, Master Teleportation for 3 turns
        _font.Tick(onField, CharmLock.InLiveBattle(slot0, battleMode));   // Wellspring +3: a moved turn restores the wielder's HP and MP
        if (_tick++ % GrowthEveryNTicks == 0) _growth.Apply();   // growth holds stats; ~100ms is plenty
        if (changed) SaveTally();

        // slot9 is still the battle sentinel, but once we've been OFF the live battlefield
        // for a beat (battleMode 0 = world-map party menu / post-battle), paint the card.
        // RPM/WPM make the scan/paint fail-safe, so doing this in a churny menu can't crash;
        // the settle window just avoids needless work during a mid-combat battleMode flicker.
        if (battleStatus || (!onField && (now - _lastField).TotalSeconds > FieldSettleSeconds))
            _display.Tick();
    }
}
