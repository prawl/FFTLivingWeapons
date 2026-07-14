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
    private readonly PlaythroughReset _playthroughReset;   // LW-51 Tier-1: archives + resets the tally on a new-game opening
    private readonly Dictionary<int, int> _kills;
    private readonly LegendStore _legends;   // Reliquary Phase 1 deed ledger (docs/RELIQUARY_AC.md)
    private readonly Reliquary _reliquary;   // promoted from a ctor local: the battle-end summary reads BattleMarks at the exit edge
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
    private readonly AttackCard _attackCard;   // LW-31 stage 2: the Attack-menu desc dossier painter
    private readonly BattleState _battle = new();      // debounced in/out edges (slot9 sticks; mode flickers)
    private CancellationTokenSource? _cts;
    private DateTime _lastField = DateTime.MinValue;   // last tick we were on the live battlefield
    private const double FieldSettleSeconds = 1.5;      // wait this long off-field before painting
    private bool _lastBattleStatus;                     // edge-detect entering the in-battle status card
    private int _lastMode = int.MinValue;               // edge-detect battleMode changes for the flight recorder (N2)

    // Scholar's Ring grant: throttled to ~1 s (every 30 ticks at 33 ms) and only
    // runs out of battle.  A separate counter avoids coupling to _tick (which only
    // advances in-battle).
    private int _ringThrottleTick;
    private const int RingThrottleEveryNTicks = 30;
    private readonly GunSlinger _gunSlinger = null!;
    private int _gunSlingerThrottleTick;
    private const int GunSlingerThrottleEveryNTicks = 30;
    private readonly IGameMemory _live = null!;   // assigned in ctor, used by Tick
    private readonly LaunchGuard _launchGuard;   // LW-50: gates every write until landmarks verify
    private readonly BannerToast _toast;
    private readonly bool _bannerToastsEnabled;
    private readonly PromptSwap _promptSwap;
    private readonly PromptSwapHook _promptSwapHook;
#if LWDEV
    private readonly TurnOwnerSpike _turnOwnerSpike;   // LW-31 stage 2 passive turn-owner correlation recorder, dev-only
    private readonly StatusSpike _statusSpike;   // LW-58: cold-call the status apply engine (F2 canary / F4 treasure), dev-only
    private readonly BodyDoubleSpike _bodyDoubleSpike;   // LW-58 Canary 8: duplicate a hovered unit into a real AI fighter (F5), dev-only
#endif

    /// <param name="modDir">Mod deployment directory (meta.json / treasure.json live here).</param>
    /// <param name="treasureAlwaysOn">Override for the Treasure Master AlwaysOn gate, read from
    /// Config.TreasureAlwaysOn at startup.  Null falls back to Tuning.TreasureAlwaysOn.</param>
    /// <param name="bannerToasts">Tier-up callout toast gate. LW-52 removed the launcher toggle, so
    /// Mod.cs no longer passes this; null falls back to Tuning.BannerToasts (toasts always on). Kept
    /// as a parameter for the BannerToast enabled-gate tests.</param>
    /// <param name="devSeedKills">Dev kill-tally seeding gate. DEV builds only (compiled out of
    /// Release entirely). LW-52 removed the launcher toggle, so Mod.cs no longer passes this; null
    /// seeds every weapon's tally as before (false would start every tally untouched).</param>
    /// <param name="devForceFingerprintMismatch">LW-50 dev-only test knob (compiled always, wired
    /// only under #if LWDEV at the Mod.cs call site): true perturbs the expected PE build key so
    /// LaunchGuard stands down as if the game had been patched, even though memory truly matches.
    /// Null/false is the normal path.</param>
    public Engine(string modDir, bool? treasureAlwaysOn = null, bool? bannerToasts = null, bool? devSeedKills = null,
        bool? devForceFingerprintMismatch = null)
    {
        // LW-51: save files now live in the update-safe Reloaded/User/Mods/<ModId> dir, not the
        // deploy mod dir, so a mod-folder-replace update can no longer wipe them. Each store's
        // legacy file (if any) migrates into that dir, non-destructively, before its first load.
        var save = new SaveLocation(modDir);
        ModLogger.Event(LogVerb.Save, $"Save files live at {save.SaveDir}.");

        _tally = KillTally.Load(save.Migrate("kills.json"));
        // LW-51 Tier-1: shares this SaveLocation/KillTally pair by reference (never re-reads or
        // re-constructs either); Observe is called every tick post-arm from Tick(), gated on the
        // SAME inLive predicate every other module trusts (LW-56 moved Observe's call site above
        // BattleState.Step so its detection edge can reach Step as a forced-exit signal the very
        // same tick; the inLive gating itself is unchanged).
        _playthroughReset = new PlaythroughReset(save, _tally);
        _kills = _tally.Kills;
        // Launch header L3 (logging facelift): the kill-tally load summary, BEFORE the dev seed
        // below can inflate the counts -- this line states what the DISK held.
        ModLogger.Event(LogVerb.Save,
            LaunchHeader.ComposeTally(_tally.Total, _tally.Kills.Count, _tally.LoadedFrom));
        save.Migrate("legends.json");
        _legends = LegendStore.Load(save.SaveDir);   // Reliquary Phase 1 deed ledger, sibling of kills.json
        // Launch header L4: the legends load summary.
        ModLogger.Event(LogVerb.Save,
            LaunchHeader.ComposeLegends(_legends.WeaponCount, _legends.TotalMarks, _legends.LoadedFrom));
        var meta = MetaLoader.Load(modDir);
#if LWDEV
        // DEV build only: seed every weapon to max tier for fast verification. Compiled out of
        // Release entirely (Tuning.DevSeedAllKills is a const false there, so a runtime `if` would
        // leave provably-unreachable code -- CS0162). LW-52 removed the launcher toggle, so
        // devSeedKills is always null here (Mod.cs omits it) and DEV always seeds; the param and its
        // != false guard are kept so a future caller could still opt out.
        if (Tuning.DevSeedAllKills && devSeedKills != false)
        {
            Tuning.SeedKills(meta.Keys, _kills, Tuning.DevKillSeed);
            // Launch header L7 (development builds only).
            ModLogger.Event(LogVerb.Startup, $"Development build: every weapon's tally is seeded to at least {Tuning.DevKillSeed} kills; every weapon starts with its full powers for fast testing.");
        }
#endif
        var live = new LiveMemory();   // the ONE production IGameMemory, shared by every subsystem
        _live = live;
        // LW-50: born disarmed (Mod.StartEngine already set Mem.WritesEnabled = false before this
        // ctor ran); Tick() below holds every subsystem off until the landmarks verify. LW-53:
        // recorder/requestFlush wire the guard's own arm/stand-down lifecycle into the flight
        // ring, so a stand-down leaves a durable archive.
        _launchGuard = new LaunchGuard(live, devForceFingerprintMismatch ?? false, notice: StandDownNotice.Show,
            recorder: Flight.Record, requestFlush: Flight.RequestFlush);
        _bannerToastsEnabled = bannerToasts ?? Tuning.BannerToasts;
        _toast = new BannerToast(meta, _kills, _bannerToastsEnabled);
        // Reliquary Phase 1 (docs/RELIQUARY_AC.md): the deed-recording seam KillTracker's
        // CreditKill reports every credited kill's captured victim to. LW-35 (owner direction):
        // Marks are release-hidden on EVERY surface, the deed toast included, so pass null for
        // Reliquary's toast. A null toast leaves the Mark-announce path fully inert (proven by
        // ReliquaryTests.Disabled_toasts_stay_fully_inert; BannerToast.Enqueue has no _enabled
        // gate of its own), while the LegendStore still records every deed and the milestone /
        // unlock toasts on _toast (via BannerToast.Tick) are untouched. Pass
        // `_bannerToastsEnabled ? _toast : null` to re-enable (Reliquary Phase 2), matching the
        // equip-card `legends:` re-enable below.
        _reliquary = new Reliquary(_legends, null, meta, Flight.Record);
        _promptSwap = new PromptSwap(_toast, live);
        _promptSwapHook = new PromptSwapHook(_promptSwap, live);
        _turns = new TurnTracker(live, Flight.Record);
        // verbose: true (was Tuning.VerboseEvents, DEV-only const) -- the event timeline is now
        // always captured to the file at Debug tier via the [trace] verb (Debug writes
        // livingweapon.log unconditionally); it never reaches the console since LW-52 pinned the
        // console to Info. Deliberate Release-behavior change, see BattleLog's class doc.
        // Dual-emit composes HERE at the composition root (B2): BattleLog itself never references
        // Flight -- its sink just also records every ev: line into the flight ring.
        // LW-56: hasLiveWielder wires Wielder.HasLiveWielder over the SAME live memory every other
        // roster/band consumer in this ctor uses (GunSlinger/GrowthEngine's precedent below), so
        // CreditKill's gate reads the real battlefield, not a second independent memory view.
        _tracker = new KillTracker(_kills, live, new HashSet<int>(meta.Keys),
                                   new BattleLog(verbose: true, sink: s => { ModLogger.Debug(LogVerb.Trace, s); Flight.Record("ev", s); }),
                                   Flight.Record, deeds: _reliquary, hasLiveWielder: id => Wielder.HasLiveWielder(live, id));
        // Kiku-ichimonji's Mushin stack count: ONE dictionary shared by reference between the
        // trigger (Mushin, banks/spends stacks) and the growth hold (GrowthEngine.HoldMushin,
        // reads it), keyed by wielder fingerprint (lvl,br,fa) like Iai's fp-keyed _holds.
        var mushinArmed = new Dictionary<(int lvl, int br, int fa), int>();
        _growth = new GrowthEngine(meta, _kills, _turns, live, mushinArmed);
        _charm = new CharmLock(meta, _kills, live);                 // Galewind +3: one charm held unbreakable (own-CT turns)
        _barrage = new Barrage(meta, _kills, live);                 // Yoichi +3: grant Barrage command to the wielder
        _shadowBlade = new ShadowBlade(meta, _kills, live);           // Sanguine +3: grant Shadow Blade (HP-draining dark strike)
        var extra = new ExtraTurn(_kills, live);                    // Zwill +3: a kill grants the killer an immediate extra turn
        var eagle = new EagleEye(meta, _kills, live);               // Eclipsebolt +3: hasten any enemy Doom to a 1-turn countdown
        var ricochet = new Ricochet(meta, _kills, _tracker, live);  // Stormarc +3: bounce chip to nearest other enemy
        var maim = new Maim(meta, _kills, _tracker, live);          // Huntress +3: struck enemies lose reactions N turns
        var kobu = new Kobu(meta, _kills, _tracker, live);          // Kiyomori +3: on a melee hit, if foe's brave exceeds wielder's, raise wielder's current brave to match
        var iai = new Iai(meta, _kills, live);                       // Ame-no-Murakumo +3: hold every deployed wielder's Speed above the field max for the opening turn, released by the engine actor pointer (arrival or acted-edge match) naming the wielder's own band entry
        var mushin = new Mushin(meta, _kills, mushinArmed, live);   // Kiku-ichimonji +3: a full wait turn (no move, no act) arms one charge, spent on the wielder's next own-turn action (LW-4 round 5: the literal PSX turn-flag design, no tracker dependency)
        var plague = new Plague(meta, _kills, _tracker, mem: live); // Venombolt +3: poison never fades, ticks harder
        var lifeSap = new LifeSap(meta, _kills, mem: live);         // Umbral +3: a kill heals the wielder 25% max HP
        var wyrmblood = new Wyrmblood(meta, _kills, _turns, live);  // Dragon Rod +3: turn-edge regen splash (1 tile)
        var renewal = new Renewal(meta, _kills, _turns, live);     // Mending Staff +3: turn-edge regen aura to allies within 1 tile (Chebyshev)
        var rapture = new Rapture(meta, _kills, _turns, live);      // Rod of Faith +3: low-HP Master Teleportation window
        var font = new SpiritualFont(meta, _kills, _tracker, live); // Wellspring +3: a moved action restores HP and MP
        var feign = new FeignDeath(meta, _kills, live);             // Wrathblade +3: a lethal hit becomes a played-dead corpse, engine auto-revives at ~10% HP
        var larceny = new Larceny(meta, _kills, _tracker, _turns, live);  // Arcanum +3: steal the struck foe's buff onto the wielder (fades after N of the wielder's own turns)
        var puppeteer = new Puppeteer(meta, _kills, _tracker, _turns, live, Flight.Record);  // Galewind +3: dominate a struck enemy for N of its turns (Puppeteer; replaces Charm-Lock -- _charm goes dormant once Galewind's meta carries puppeteerTurns). Flight.Record = the LW-5 recon tap (puppet-turn signals).
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
        _signatures = new ISignature[] { _charm, extra, eagle, ricochet, maim, kobu, iai, mushin, larceny, puppeteer, plague, _barrage, _shadowBlade, lifeSap, wyrmblood, renewal, rapture, font, feign, benediction, sanctuary, choir, _treasure };
        _fieldSignatures = new ISignature[] { extra, eagle, ricochet, maim, kobu, iai, mushin, larceny, puppeteer, plague, lifeSap, wyrmblood, renewal, rapture, font, feign, benediction, sanctuary, choir };
        save.Migrate("gunslinger.json");
        _gunSlinger = new GunSlinger(meta, _kills, save.SaveDir, live);
        // LW-35 (owner direction): Marks are release-hidden on EVERY card surface. The Attack card
        // already stopped consuming the deed ledger (AttackCard.Resolve sets markLabel=null); the
        // equip card stops the same way, by NOT wiring legends into Display's StoryLines. The
        // LegendStore keeps recording (the Reliquary above still writes and saves _legends); only
        // this DISPLAY surface stops painting the story line. Pass legends: _legends to re-enable
        // (Reliquary Phase 2), which rebuilds StoryLines/EarnedAnchors (the three-way anchor, decision 12).
        _display = new Display(meta, _kills, live, legends: null, poolPaint: Tuning.PoolPaintEnabled);
        // LW-31: the acting unit's Attack-menu dossier. Wired here (not beside _tracker) because
        // it needs the tracker's cursor-resolve + sprite seam. CURSOR-ONLY since 2026-07-06
        // (owner-observed wrong-weapon display; see AttackCard.cs's class doc): the tracker's
        // register no longer feeds this surface at all. LW-55: Flight.Record is the tripwire's
        // recorder tap (the KillTracker/LaunchGuard idiom above), a no-op until Flight.Init runs.
        _attackCard = new AttackCard(live, _tracker.ResolveCursorPlayer,
                                      _tracker.SpriteOf, meta, _kills, Flight.Record);
#if LWDEV
        // Shares the SAME register KillerStamp/AttackCard already trust (see TurnOwnerSpike.cs's
        // class doc for why a second register is deliberately avoided).
        _turnOwnerSpike = new TurnOwnerSpike(live, _tracker.Register);
        _statusSpike = new StatusSpike(live);   // LW-58 cold-call research instrument
        _bodyDoubleSpike = new BodyDoubleSpike(live, save.SaveDir);   // LW-58 Canary 8 duplicate-to-AI-fighter; SaveDir = rotation-proof forensics
#endif
        LogNames.Init(meta);
        // Launch header L5 (the kill-total half of the old line moved to L3, the load summary).
        ModLogger.Event(LogVerb.Startup, $"Living Weapons is tracking {meta.Count} weapon types.");
    }

    /// <summary>Late-injected by Mod.Start/StartEx once the loader resolves
    /// reloaded.sharedlib.hooks (controllers are not resolvable at construction time).
    /// Production arms the facing-prompt swap here (gated on the compiled BannerToasts default,
    /// always on since LW-52 removed the launcher toggle).</summary>
    public void InjectHooks(Reloaded.Hooks.Definitions.IReloadedHooks hooks)
    {
        // LW-50: the arm is deferred through LaunchGuard's hook handshake so a hook installed
        // before the fingerprint guard decides anything waits for the Armed edge (or never fires
        // at all if the guard stands down).
        if (_bannerToastsEnabled) _launchGuard.OfferHookArm(() => _promptSwapHook.Arm(hooks));
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            // Launch header L6: the liveness canary that closes the header.
            ModLogger.Event(LogVerb.Startup, "The runtime loop has started.");
            while (!token.IsCancellationRequested)
            {
                // A persistent fault would repeat every 33ms; the console dedup (C1, keyed on
                // the rendered content) collapses it to once per battle while the file keeps
                // every occurrence.
                try { Tick(); }
                catch (Exception ex) { ModLogger.Error(LogVerb.Engine, "One engine update was skipped; an internal error occurred: " + ex.Message); }
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
        _reliquary.ResetBattle();   // per-battle Marks ledger (the exit edge composes its summary BEFORE this runs)
        foreach (var sig in _signatures) sig.ResetBattle();
        _attackCard.ResetBattle();   // LW-31 stage 2: restore vanilla to any live Attack-menu copies; the cache stays warm for the next battle's re-verify
#if LWDEV
        _bodyDoubleSpike.ResetBattle();   // LW-58: the bind + decoy CT-hold never survive a battle edge
#endif
        // The toast QUEUE deliberately survives battle edges (Patrick-confirmed ruling A) --
        // PromptSwap is stateless (no per-battle state to reset).
    }

    private void Tick()
    {
        Flight.DrainPending();   // cheap flag check every tick; the actual I/O (if any is pending) runs here, never on the requester's thread

        // LW-50: before any landmark verifies, only retry the guard and do nothing else this
        // tick. Once armed, this check is a single cheap bool read (Step is never called again).
        if (!_launchGuard.Armed) { _launchGuard.Step(); return; }

        uint slot0 = Mem.U32(Offsets.Slot0);
        uint slot9 = Mem.U32(Offsets.Slot9);
        int battleMode = Mem.U8(Offsets.BattleMode);
        // Mode-CHANGE tap (N2): new code -- Engine only edge-detected battle enter/exit before this.
        // First sighting baselines silently (mirrors BattleLog.Observe); only real changes record.
        if (_lastMode == int.MinValue) _lastMode = battleMode;
        else if (battleMode != _lastMode) { Flight.Record("mode", $"battleMode {_lastMode} -> {battleMode}"); _lastMode = battleMode; }
        bool paused = Mem.U8(Offsets.PauseFlag) == 1;
        int eventId = Mem.U16(Offsets.EventId);   // out-of-live: dialogue/cutscene; in-combat: nameId alias
        var now = DateTime.Now;
        // A genuine in-battle frame: live modes, or the slot0 marker with a paused/event excuse
        // (the marker alone lies -- it sticks at 0xFF after a battle QUIT). Gates every module that
        // writes battle memory. Computed BEFORE BattleState.Step (LW-56) so PlaythroughReset's own
        // detection edge, below, can reach Step as a forced-exit signal on this same tick.
        bool inLive = BattleState.InLiveBattle(slot0, battleMode, paused, eventId);
        // LW-51 Tier-1: runs every tick post-arm (Tick() only early-returns pre-arm above), gated
        // on the SAME inLive this tick's other modules already trust, so a mid-battle dialogue
        // frame aliasing eventId==2 can never be mistaken for the new-game opening. LW-56: this
        // call now runs before BattleState.Step (moved up from below), so its detection edge
        // (Observe's return value, true exactly on the HoldTicks-th held tick) can reach Step as
        // a forced-exit signal on the same tick a new game qualifies: flushing stale per-battle
        // attribution state (the KillTracker weapon latch) that a mid-battle New Game would
        // otherwise carry into the Orbonne opener, since no ordinary battle-exit edge ever fires
        // for an in-session New Game.
        bool newGame = _playthroughReset.Observe(eventId, battleMode, inLive);
        if (newGame && _battle.In)
            ModLogger.Event(LogVerb.BattleEnd, "A new game was detected while battle state was still live; forcing the battle exit edge to flush stale attribution state.");
        // LW-59: a main-menu New Game (no battle live at reset) fires no Exited edge below
        // (BattleState's In==false branch cannot return Exited), so it never reached
        // _display.Invalidate() otherwise, leaving stale painted suffix bytes in the equip-card
        // pool for the freshly-cleared tally to inherit. Invalidate unconditionally here: Observe
        // returns true exactly once per detection, Invalidate is idempotent, and the mid-battle
        // case's forced-exit edge below also invalidates the same tick, harmlessly. LW-70: the
        // same once-per-detection edge also re-baselines the toast queue, so a post-reset first
        // kill reads as a fresh crossing instead of a rollback below the stale pre-reset snapshot.
        if (newGame) { _display.Invalidate(); _toast.Rebaseline(); }
        // Enter is instant; exit is debounced (battleMode flickers, slot9 sticks), UNLESS forceExit
        // fires (LW-56: a detected new game bypasses the debounce entirely). nowIn is sticky through
        // mid-battle dips, flipping only on the debounced or forced edges.
        BattleEdge edge = _battle.Step(slot0, slot9, battleMode, paused, eventId, now, forceExit: newGame);
        bool nowIn = _battle.In;
        bool onField = BattleState.OnField(nowIn, battleMode);
        if (onField) _lastField = now;
        bool battleDisplayed = BattleState.BattleDisplayed(slot9, battleMode);
        if (edge == BattleEdge.Entered)
        {
            ModLogger.NoteBattleEdge();   // console-only per-battle dedup reset (C1); file sink unaffected
            // Console gets the clean edge; the raw hex sentinels ride the [trace] companion
            // (numeric ids belong in parens on file lines only).
            ModLogger.EventWithTrace(LogVerb.BattleStart, "Battle started.",
                $"battle-start sentinels (slot0={slot0:X} slot9={slot9:X} mode={battleMode})");
            // Archive the ring BEFORE the new battle's events pour in: sessions ending in a process
            // kill (deploys, crashes) never fire the exit-edge flush, so the enter edge is the
            // reliable rescue point for the PREVIOUS battle's tail (live-observed 2026-07-04: three
            // sessions, zero archives, all ended by kills). Loop thread -- synchronous is the norm here.
            Flight.FlushBattleStart();
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
            // THE match-report summary: composed from the per-battle counters BEFORE
            // ResetBattleState() wipes them (order is load-bearing). Sentinels ride the trace
            // companion, same split as the enter edge. (LW-56: on a FORCED exit, the tally was
            // already archived and cleared moments earlier this same tick by PlaythroughReset, so
            // this summary can compose against an already-empty tally; cosmetic, accepted.)
            string summary = BattleSummary.Compose(
                _tracker.BattleCredits, _kills, _reliquary.BattleMarks, _tracker.FallbackCredits,
                _turns.GlobalTurns, LogNames.Weapon, Tuning.TierFor);
            ModLogger.EventWithTrace(LogVerb.BattleEnd, summary,
                $"battle-end sentinels (slot0={slot0:X} slot9={slot9:X} mode={battleMode} paused={paused} event={eventId})");
            // LW-56 D11/A3: re-emit the identity census on the exit edge, unconditionally, before
            // ResetBattleState()/the flight flush. The enter-side census (KillTracker.Poll,
            // behind the oracle coverage-done latch) can miss an entire battle when coverage never
            // completes, so the exit edge is the reliable place a census always lands on tape.
            // Covers the normal AND the LW-56 forced exit alike (both return Exited here).
            _tracker.EmitExitCensus();
            ResetBattleState();
            _tally.Save();               // flush on battle end
            _legends.SaveIfDirty();      // Reliquary: mirrors kills.json's battle-exit save timing
            // S2: the battle-exit edge ONLY -- ResetBattleState() fires on BOTH edges (enter+exit),
            // so the flush lives here beside _tally.Save(), not inside that shared method.
            Flight.FlushBattleEnd();
            _display.Invalidate();       // re-find the menu's freshly-allocated render copies
            ModLogger.NoteBattleEdge();  // console-only per-battle dedup reset (C1); file sink unaffected
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
        // Gun Slinger runs PRE-GATE (2026-07-04, Barrage's precedent) -- it originally ran only in
        // the out-of-battle branch and the twin pistol did not hold into combat. It now also runs in
        // battle (inBattle: nowIn) but RE-ASSERTS ONLY there: it may rewrite a twin it already
        // snapshotted, never snapshots fresh or restores in battle, so an unreliable mid-battle
        // roster read can NEVER corrupt the persisted "original gear" store. Snapshot/restore stay
        // out of battle, where equipment legitimately changes. Live-verified 2026-07-04: the twin
        // fires twice in battle with this hold. The ~1 s throttle stands.
        if (++_gunSlingerThrottleTick >= GunSlingerThrottleEveryNTicks)
        {
            _gunSlingerThrottleTick = 0;
            _gunSlinger.PrepRoster(inBattle: nowIn);
        }
        if (!nowIn)
        {
            // Scholar's Ring: ensure the player always has at least one (idempotent).
            // Throttled to ~1 s -- no need to hammer inventory every 33 ms.
            if (++_ringThrottleTick >= RingThrottleEveryNTicks)
            {
                _ringThrottleTick = 0;
                ScholarRing.Grant(_live);
            }
            _display.Tick(false);   // out of battle (slot9 cleared): keep the equip card painted
            return;
        }

        bool changed = _tracker.Poll(onField);   // every ~33ms tick so fast-forward deaths aren't missed
        _turns.Poll();                        // edge-detect each unit's turns (for timed signatures)
        var ctx = new TickContext(now, onField, inLive);
        foreach (var sig in _fieldSignatures) sig.Tick(in ctx);
        if (_tick++ % GrowthEveryNTicks == 0) _growth.Apply();   // growth holds stats; ~100ms is plenty
        // NOT onField-gated: the facing prompt this queues into can render during the mode-1
        // cast-animation frames too (BannerToast's class doc / the migrated BannerSpike lesson) --
        // gating on onField would sleep through it. Delivery itself needs no Tick: PromptSwapHook
        // fires from the game's own SetTextString call, not from this loop.
        _toast.Tick(changed);
        _attackCard.Tick();   // LW-31 stage 2: the Attack-menu desc painter
#if LWDEV
        _turnOwnerSpike.Tick();   // LW-31 stage 2: passive correlation recorder, in-battle only (menus out of battle don't matter here)
        _statusSpike.Tick(inLive);   // LW-58: cold-call the status apply engine on F2/F4 (inLive-gated + paused; targets live band units)
        _bodyDoubleSpike.Tick(inLive);   // LW-58 Canary 8: F5 duplicates the hovered unit into a real AI fighter (inLive-gated + paused), Ctrl+F5 despawns
#endif
        if (changed)
        {
            _tally.Save();
            _legends.SaveIfDirty();   // Reliquary: mirrors kills.json's on-change save timing
        }

        // slot9 is still the battle sentinel, but once we've been OFF the live battlefield
        // for a beat (battleMode 0 = world-map party menu / post-battle), paint the card.
        if (BattleState.ShouldPaintCard(battleStatus, onField, (now - _lastField).TotalSeconds, FieldSettleSeconds))
            _display.Tick(true);
    }
}
