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
    private readonly CharmLock _charm;     // named: ticked pre-gate on battleDisplayed, outside the field-signature order
    private readonly Barrage _barrage;     // named: ticks in AND out of battle (learn-screen hold), pre-gate
    private readonly ShadowBlade _shadowBlade; // named: ticks pre-gate like Barrage (JobCommand grant of Shadow Blade)
    private readonly TreasureMaster _treasure;
    private readonly ISignature[] _signatures;        // every signature module, battle-exit reset order
    private readonly ISignature[] _fieldSignatures;   // the in-battle tick order (Barrage ticks pre-gate instead)
    private readonly Display _display;
    private readonly BattleState _battle = new();      // debounced in/out edges (slot9 sticks; mode flickers)
    private CancellationTokenSource? _cts;
    private DateTime _lastField = DateTime.MinValue;   // last tick we were on the live battlefield
    private const double FieldSettleSeconds = 1.5;      // wait this long off-field before painting
    private bool _lastBattleStatus;                     // edge-detect entering the in-battle status card

    // Scholar's Ring grant: throttled to ~1 s (every 30 ticks at 33 ms) and only
    // runs out of battle.  A separate counter avoids coupling to _tick (which only
    // advances in-battle).
    private int _ringThrottleTick;
    private const int RingThrottleEveryNTicks = 30;
    private readonly GunSlinger _gunSlinger = null!;
    private int _gunSlingerThrottleTick;
    private const int GunSlingerThrottleEveryNTicks = 30;
    private readonly IGameMemory _live = null!;   // assigned in ctor, used by Tick

    /// <param name="modDir">Mod deployment directory (meta.json / treasure.json live here).</param>
    /// <param name="treasureAlwaysOn">Override for the Treasure Master AlwaysOn gate, read from
    /// Config.TreasureAlwaysOn at startup.  Null falls back to Tuning.TreasureAlwaysOn.</param>
    public Engine(string modDir, bool? treasureAlwaysOn = null)
    {
        _tally = KillTally.Load(Path.Combine(modDir, "kills.json"));
        _kills = _tally.Kills;
        var meta = MetaLoader.Load(modDir);
#if LWDEV
        // DEV build only: seed every weapon to max tier for fast verification. Compiled out of
        // Release entirely (Tuning.DevSeedAllKills is a const false there, so a runtime `if` would
        // leave provably-unreachable code -- CS0162).
        if (Tuning.DevSeedAllKills)
        {
            Tuning.SeedKills(meta.Keys, _kills, Tuning.DevKillSeed);
            Log.Info($"DEV: force-seeded all {meta.Count} weapons to at least {Tuning.DevKillSeed} kills -- every weapon starts with +3 effects active for fast testing.");
        }
#endif
        var live = new LiveMemory();   // the ONE production IGameMemory, shared by every subsystem
        _live = live;
        _turns = new TurnTracker(live);
        _tracker = new KillTracker(_kills, live, new HashSet<int>(meta.Keys),
                                   new BattleLog(Tuning.VerboseEvents));
        _growth = new GrowthEngine(meta, _kills, _turns, live);
        _charm = new CharmLock(meta, _kills, live);                 // Galewind +3: one charm held unbreakable (own-CT turns)
        _barrage = new Barrage(meta, _kills, live);                 // Yoichi +3: grant Barrage command to the wielder
        _shadowBlade = new ShadowBlade(meta, _kills, live);           // Sanguine +3: grant Shadow Blade (HP-draining dark strike)
        var extra = new ExtraTurn(_kills, live);                    // Zwill +3: a kill grants the killer an immediate extra turn
        var eagle = new EagleEye(meta, _kills, live);               // Eclipsebolt +3: hasten any enemy Doom to a 1-turn countdown
        var ricochet = new Ricochet(meta, _kills, _tracker, live);  // Stormarc +3: bounce chip to nearest other enemy
        var maim = new Maim(meta, _kills, _tracker, live);          // Huntress +3: struck enemies lose reactions N turns
        var kobu = new Kobu(meta, _kills, _tracker, live);          // Kiyomori +3: on a melee hit, if foe's brave exceeds wielder's, raise wielder's current brave to match
        var iai = new Iai(meta, _kills, live);                       // Ame-no-Murakumo +3: hold every deployed wielder's Speed above the field max for the opening turn, released by the engine actor pointer (arrival or acted-edge match) naming the wielder's own band entry
        var plague = new Plague(meta, _kills, _tracker, mem: live); // Venombolt +3: poison never fades, ticks harder
        var lifeSap = new LifeSap(meta, _kills, mem: live);         // Umbral +3: a kill heals the wielder 25% max HP
        var wyrmblood = new Wyrmblood(meta, _kills, _turns, live);  // Dragon Rod +3: turn-edge regen splash (1 tile)
        var renewal = new Renewal(meta, _kills, _turns, live);     // Mending Staff +3: turn-edge regen aura to allies within 1 tile (Chebyshev)
        var rapture = new Rapture(meta, _kills, _turns, live);      // Rod of Faith +3: low-HP Master Teleportation window
        var font = new SpiritualFont(meta, _kills, _tracker, live); // Wellspring +3: a moved action restores HP and MP
        var feign = new FeignDeath(meta, _kills, live);             // Wrathblade +3: a lethal hit becomes a played-dead corpse, engine auto-revives at ~10% HP
        var larceny = new Larceny(meta, _kills, _tracker, _turns, live);  // Arcanum +3: steal the struck foe's buff onto the wielder (fades after N of the wielder's own turns)
        var puppeteer = new Puppeteer(meta, _kills, _tracker, _turns, live);  // Galewind +3: dominate a struck enemy for N of its turns (Puppeteer; replaces Charm-Lock -- _charm goes dormant once Galewind's meta carries puppeteerTurns)
        var benediction = new Benediction(meta, _kills, _tracker, live); // Sanctus Staff +3: ally HP rises boosted 30% while a Sanctus Staff is the last player to act (sticky latch -- survives the charged-heal resolve gap)
        var sanctuary = new Sanctuary(meta, _kills, live);               // Staff of the Magi +3: while the bearer lives, fallen allies are held from crystallizing
        var choir = new Choir(meta, _kills, live);                       // Warlock's Staff +3: adjacent allies cast magick instantly (Non-charge aura)
        var treasureJson = Path.Combine(modDir, "treasure.json");
        _treasure = new TreasureMaster(
            load:         () => TreasureDb.Load(modDir),
            datasetStamp: () => { try { return File.GetLastWriteTimeUtc(treasureJson); }
                                  catch { return null; } },
            mem:      live,
            alwaysOn: treasureAlwaysOn);
        _treasure.StartFastHold();
        // Both orders are load-bearing and preserved verbatim from the hand-wired era:
        // reset runs charm..font with Barrage between Plague and LifeSap; the in-battle tick
        // excludes Barrage (ticks before the !nowIn early-return, learn screens included),
        // excludes TreasureMaster (ticks pre-gate on battleDisplayed, not inLive -- formation
        // and enemy turns are included; world map excluded), and excludes CharmLock (ticks
        // pre-gate on battleDisplayed like TreasureMaster, so a held charm is not dropped
        // mid-combat between turns). Both TreasureMaster and CharmLock stay in _signatures
        // so ResetBattle still fires on the debounced battle-exit edge.
        _signatures = new ISignature[] { _charm, extra, eagle, ricochet, maim, kobu, iai, larceny, puppeteer, plague, _barrage, _shadowBlade, lifeSap, wyrmblood, renewal, rapture, font, feign, benediction, sanctuary, choir, _treasure };
        _fieldSignatures = new ISignature[] { extra, eagle, ricochet, maim, kobu, iai, larceny, puppeteer, plague, lifeSap, wyrmblood, renewal, rapture, font, feign, benediction, sanctuary, choir };
        _gunSlinger = new GunSlinger(meta, _kills, modDir, live);
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

    /// <summary>Clear all per-battle state -- kill tracker, turn clocks, growth holds, and every
    /// signature's hold/ledger. Fired on BOTH the battle-enter and battle-exit edges so a restart that
    /// skips a clean Exit still starts the new battle clean (fixes the Larceny stolen-buff carryover,
    /// 2026-06-15). Idempotent: a normal Exit->Enter double-resets harmlessly.</summary>
    private void ResetBattleState()
    {
        _tracker.ResetBattle();
        _turns.ResetBattle();
        _growth.ResetBattle();
        foreach (var sig in _signatures) sig.ResetBattle();
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
        // (the marker alone lies -- it sticks at 0xFF after a battle QUIT). Gates every module that
        // writes battle memory.
        bool inLive = BattleState.InLiveBattle(slot0, battleMode, paused, eventId);
        bool battleDisplayed = BattleState.BattleDisplayed(slot9, battleMode);
        if (edge == BattleEdge.Entered)
        {
            Log.Info($"battle: started (slot0={slot0:X} slot9={slot9:X} mode={battleMode})");
            // Reset per-battle state on ENTER too. A battle RESTART can re-enter WITHOUT a clean Exit
            // (the slot0/slot9 sentinels stick -- slot0-quit-stick-trap), which left Larceny's stolen-buff
            // ledger alive into the new battle: the engine wipes statuses at battle start, but Larceny's
            // per-tick Drive re-applied them from the stale ledger, so they carried over and never faded.
            // Resetting here makes a fresh battle start clean however the prior one ended (2026-06-15).
            ResetBattleState();
        }

        // In-battle "Status" card (a paused, stable menu) -- paint the counter there too.
        bool battleStatus = BattleState.StatusCardOpen(nowIn, battleMode,
                                                       Mem.U8(Offsets.PauseFlag) == 1, Mem.U8(Offsets.SubmenuFlag) == 1);
        if (battleStatus && !_lastBattleStatus) _display.Invalidate();   // re-find the card's fresh buffers
        _lastBattleStatus = battleStatus;

        if (edge == BattleEdge.Exited)
        {
            Log.Info($"battle: ended -- saving kill tally, resetting battle trackers (slot0={slot0:X} slot9={slot9:X} mode={battleMode} paused={paused} event={eventId})");
            ResetBattleState();
            _tally.Save();               // flush on battle end
            _display.Invalidate();       // re-find the menu's freshly-allocated render copies
        }
        // Barrage runs in AND out of battle: the learn screen / pre-battle menus read the
        // JobCommand table live, and the learned bit needs its hold against menu writebacks.
        _barrage.Tick();
        _shadowBlade.Tick();   // pre-gate like Barrage: the learn screen / menus read the JobCommand table live
        // Treasure Master gates on "a battle map is on screen" (slot9 armed + mode != 0) rather
        // than strict InLiveBattle.  This makes it stable through formation, enemy turns, and
        // cast animations (all mode 1 with slot9 stuck) while still excluding the world map
        // (mode 0).  It ticks here -- before the !nowIn early-return -- so it fires on
        // formation and enemy turns that nowIn might not cover.
        _treasure.Tick(now, battleDisplayed);
        // CharmLock gates on battleDisplayed (mode != 0), not strict InLiveBattle. A held charm survives
        // the between-turn mode-0 lulls because Tick merely IDLES when battleDisplayed is false (it does
        // NOT time the lock out -- there is no heartbeat), then resumes holding when the map redraws.
        // ResetBattle (battle-exit edge, via _signatures) is the teardown. Same pre-gate slot as TreasureMaster.
        _charm.Tick(now, battleDisplayed);
        if (!nowIn)
        {
            // Scholar's Ring: ensure the player always has at least one (idempotent).
            // Throttled to ~1 s -- no need to hammer inventory every 33 ms.
            if (++_ringThrottleTick >= RingThrottleEveryNTicks)
            {
                _ringThrottleTick = 0;
                ScholarRing.Grant(_live);
            }
            // Gun Slinger: keep the twin Blaster + Dual Wield in the wielder's roster
            // slots between battles. Throttled to ~1 s (snapshot+restore logic is
            // idempotent; hammering it every 33 ms is wasteful).
            if (++_gunSlingerThrottleTick >= GunSlingerThrottleEveryNTicks)
            {
                _gunSlingerThrottleTick = 0;
                _gunSlinger.PrepRoster();
            }
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
