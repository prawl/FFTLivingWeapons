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
    // past it, missing kills. The kill scan is cheap; growth (heavier) runs every Nth tick
    // (LW-184: the old _tick/GrowthEveryNTicks counter now lives on the "growth" TickPhase row).
    private const int PollMs = 33;

    private readonly KillTally _tally;
    private readonly PlaythroughReset _playthroughReset;   // LW-51 Tier-1: archives + resets the tally on a new-game opening
    private readonly Dictionary<int, int> _kills;
    private readonly LegendStore _legends;   // Reliquary Phase 1 deed ledger (docs/RELIQUARY_AC.md)
    private readonly Reliquary _reliquary;   // promoted from a ctor local: the battle-end summary reads BattleMarks at the exit edge
    private readonly LivingPoach _livingPoach;   // LW-167 stage 2 (DISARMED): the Poach carcass-drop executor
    private readonly KillTracker _tracker;
    private readonly TurnTracker _turns;
    private readonly NaturalLedger _naturalLedger;   // LW-90: the shared cross-battle written-target memory
    private readonly GrowthEngine _growth;
    private readonly Barrage _barrage;     // named: ticks in AND out of battle (learn-screen hold), pre-gate
    private readonly ShadowBlade _shadowBlade; // named: ticks pre-gate like Barrage (JobCommand grant of Shadow Blade)
    private readonly Provoke _provoke;     // named: ticks pre-gate like ShadowBlade (LW-123 arc 1; armed via id 33's signature block, disarmed by removing it)
    private readonly ProvokeHold _provokeHold;   // named: LW-123 arc 2a -- reads arc 1's mark, gates on the band bit itself, not meta[33].Signature
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

    // LW-184: the declarative tick fan-out table (built once in the ctor, after every subsystem
    // below exists) and the reused per-tick blackboard its rows read/write. Replaces the old
    // _ringThrottleTick/RingThrottleEveryNTicks and _gunSlingerThrottleTick/
    // GunSlingerThrottleEveryNTicks counters (now on the "scholar-ring" and "gunslinger" rows).
    private readonly TickPhase[] _phases;
    private readonly TickPhaseState _tickState = new();
    private readonly GunSlinger _gunSlinger = null!;
    private readonly IGameMemory _live = null!;   // assigned in ctor, used by Tick
    private readonly LaunchGuard _launchGuard;   // LW-50: gates every write until landmarks verify
    private readonly AnchorScout _anchorScout;   // LW-82: verifier scout, ticks only once StoodDown; writes nothing
    private readonly BannerToast _toast;
    private readonly bool _bannerToastsEnabled;
    private readonly PromptSwap _promptSwap;
    private readonly PromptSwapHook _promptSwapHook;
#if LWDEV
    private readonly TurnOwnerSpike _turnOwnerSpike;   // LW-31 stage 2 passive turn-owner correlation recorder, dev-only
    private readonly StatusSpike _statusSpike;   // LW-58: cold-call the status apply engine (F2 canary / F4 chest-convert), dev-only
    private readonly BodyDoubleSpike _bodyDoubleSpike;   // LW-58 Canary 8: duplicate a hovered unit into a real AI fighter (F5), dev-only
    private readonly ProvokeSpike _provokeSpike;   // LW-123 arc 2a: plant the provoke mark without the real granted command (F6 / provoke_request.txt), dev-only
    private readonly NumeralSpike _numeralSpike;   // cold-call the floating number-popup builder (F8 / numeral_request.txt), dev-only
#endif

    /// <param name="modDir">Mod deployment directory (meta.json lives here).</param>
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
    /// <param name="mem">LW-150 S5: injectable memory seam. Null (the default, what Mod.cs always
    /// passes) constructs a fresh <see cref="LiveMemory"/> exactly as before this param existed, so
    /// production stays byte-identical; tests inject a fake so every subsystem below -- including
    /// LaunchGuard -- arms and reads through the same memory Tick()'s sentinel reads use.</param>
    /// <param name="notice">LW-150 S5: the OS-level stand-down notice handed to LaunchGuard. Null
    /// (the default) falls back to <see cref="StandDownNotice.Show"/> right here at the production
    /// construction site, so a real build still raises the real message box on a mismatch; tests
    /// pass a captured no-op so `dotnet test` never pops a Win32 dialog.</param>
    public Engine(string modDir, bool? bannerToasts = null, bool? devSeedKills = null,
        bool? devForceFingerprintMismatch = null, IGameMemory? mem = null, Action<string, string>? notice = null)
    {
        // LW-51: save files now live in the update-safe Reloaded/User/Mods/<ModId> dir, not the
        // deploy mod dir, so a mod-folder-replace update can no longer wipe them. Each store's
        // legacy file (if any) migrates into that dir, non-destructively, before its first load.
        var save = new SaveLocation(modDir);
        FlavorStamp.Write(save);   // LW-134: stamp the running build's flavor where the deploy guard can see it
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
        // LW-150 S5: mem ?? new LiveMemory() keeps production byte-identical (Mod.cs never passes
        // mem, so this is always a fresh LiveMemory there). `live` stays the local alias every
        // subsystem below was already wired against, now backed by the injected fake in tests.
        _live = mem ?? new LiveMemory();   // the ONE IGameMemory, shared by every subsystem
        var live = _live;
        // LW-50: born disarmed (Mod.StartEngine already set Mem.WritesEnabled = false before this
        // ctor ran); Tick() below holds every subsystem off until the landmarks verify. LW-53:
        // recorder/requestFlush wire the guard's own arm/stand-down lifecycle into the flight
        // ring, so a stand-down leaves a durable archive.
        _launchGuard = new LaunchGuard(live, devForceFingerprintMismatch ?? false, notice: ResolveNotice(notice),
            recorder: Flight.Record, requestFlush: Flight.RequestFlush);
        // LW-82: read-only, ticks only while the guard is StoodDown (below); never arms, never
        // writes, never touches GuardState. Verifier scout, not healer (owner-locked decision).
        _anchorScout = new AnchorScout(live);
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
        // LW-167 Living Poach (stage 4, ARMED 2026-08-12): the poach.json map + the executor.
        // Both killerHasPoach and wasBasicAttack are wired to real per-weapon memory reads --
        // LivingPoach.ReadKillerHasPoach (the combat-struct support bit) and
        // LivingPoach.ReadWasBasicAttack (the killer's own action-record basic-Attack stamp, the
        // LIVE_LEDGER "The basic-Attack discriminator (LW-167 stage 4)" row) -- so
        // LivingPoachPolicy.Decide's AND-gate now reads live signals on every gate. DeedFanout is
        // the seam-widening adapter (docs plan v2 point 4): KillTracker's `deeds:` stays a single
        // IDeedSink, now fanning RecordDeed/DeedMiss to Reliquary and RecordPoachDeed to LivingPoach.
        var poachMap = new PoachMap(modDir);
        _livingPoach = new LivingPoach(meta, poachMap, live, _toast,
            killerHasPoach: id => LivingPoach.ReadKillerHasPoach(live, id),
            wasBasicAttack: id => LivingPoach.ReadWasBasicAttack(live, id));
        var deeds = new DeedFanout(_reliquary, _livingPoach);
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
                                   Flight.Record, deeds: deeds, hasLiveWielder: id => Wielder.HasLiveWielder(live, id));
        // Kiku-ichimonji's Mushin stack count: ONE WielderKeyedStore shared by reference between
        // the trigger (Mushin, banks/spends stacks) and the growth hold (GrowthEngine.HoldMushin,
        // reads it). LW-252 stage 5: nameId primary, fp fallback -- see WielderKeyedStore's class
        // doc; both sides deriving keys through this SAME instance is load-bearing.
        var mushinArmed = new WielderKeyedStore<Box<int>>();
        // LW-90: ONE NaturalLedger shared by reference between every capture-natural hold
        // (GrowthEngine's five lanes + Iai), so a value one subsystem baked is recognized by
        // whichever subsystem captures it after a battle restart. Reset/clear wiring below
        // (ResetBattleState + the new-game edge); the mushinArmed sharing idiom above.
        _naturalLedger = new NaturalLedger();
        _growth = new GrowthEngine(meta, _kills, _turns, live, mushinArmed, _naturalLedger);
        _barrage = new Barrage(meta, _kills, live);                 // Yoichi +3: grant Barrage command to the wielder
        _shadowBlade = new ShadowBlade(meta, _kills, live);           // Sanguine +3: grant Shadow Blade (HP-draining dark strike)
        _provoke = new Provoke(meta, _kills, live);                   // Defender +3: grant Provoke (LW-123 arc 1; grants only while id 33 carries its signature block)
        _provokeHold = new ProvokeHold(_kills, live);                 // LW-123 arc 2a: the hold that reads arc 1's mark and hides the party from the provoked enemy
        var extra = new ExtraTurn(_kills, live);                    // Zwill +3: a kill grants the killer an immediate extra turn
        var eagle = new EagleEye(meta, _kills, _tracker, live);     // Eclipsebolt +3: hasten the wielder's OWN Doom procs to a 1-turn countdown (LW-95: attributed only)
        var ricochet = new Ricochet(meta, _kills, _tracker, live);  // Stormarc +3: bounce chip to nearest other enemy
        var maim = new Maim(meta, _kills, _tracker, live);          // Huntress +3: struck enemies lose reactions N turns
        var kobu = new Kobu(meta, _kills, _tracker, live);          // Kiyomori +3: on a melee hit, if foe's brave exceeds wielder's, raise wielder's current brave to match
        var iai = new Iai(meta, _kills, live, _naturalLedger);       // Ame-no-Murakumo +3: hold every deployed wielder's Speed above the field max for the opening turn, released by the engine actor pointer (arrival or acted-edge match) naming the wielder's own band entry
        var mushin = new Mushin(meta, _kills, mushinArmed, live);   // Kiku-ichimonji +3: a full wait turn (no move, no act) arms one charge, spent on the wielder's next own-turn action (LW-4 round 5: the literal PSX turn-flag design, no tracker dependency)
        var plague = new Plague(meta, _kills, _tracker, mem: live); // Venombolt +3: poison never fades, ticks harder
        var renewal = new Renewal(meta, _kills, _turns, live);     // Mending Staff +3: turn-edge regen aura to allies within 1 tile (Chebyshev)
        var rapture = new Rapture(meta, _kills, _turns, live);      // Rod of Faith +3: low-HP Master Teleportation window
        var font = new SpiritualFont(meta, _kills, _tracker, live); // Umbral Rod +3: a moved action restores HP and MP (id 56; the signature moved off the growth-only starter Wellspring, id 51)
        var feign = new FeignDeath(meta, _kills, live);             // Wrathblade +3: a lethal hit becomes a played-dead corpse, engine auto-revives at ~10% HP
        var larceny = new Larceny(meta, _kills, _tracker, _turns, live);  // Arcanum +3: steal the struck foe's buff onto the wielder (fades after N of the wielder's own turns)
        var puppeteer = new Puppeteer(meta, _kills, _tracker, _turns, live, Flight.Record);  // Galewind +3: dominate a struck enemy for N of its turns (Puppeteer; successor to Charm-Lock, whose dormant module was deleted in LW-159). Flight.Record = the LW-5 recon tap (puppet-turn signals).
        var benediction = new Benediction(meta, _kills, _tracker, live); // Sanctus Staff +3: ally HP rises boosted 30% while a Sanctus Staff is the last player to act (sticky latch -- survives the charged-heal resolve gap)
        var sanctuary = new Sanctuary(meta, _kills, live);               // Staff of the Magi +3: while the bearer lives, fallen allies are held from crystallizing
        var choir = new Choir(meta, _kills, live);                       // Warlock's Staff +3: adjacent allies cast magick instantly (Non-charge aura)
        // Sunderer +3: a full-wait turn bars the one tile at the wielder's back (docs/BULWARK_AC.md).
        // Constructed after _toast (built above) so the toast injection is safe; no ordering dependency
        // with any other signature -- it never reads or writes another module's state.
        var bulwark = new Bulwark(meta, _kills, _toast, live);
        // Both orders are load-bearing and preserved verbatim from the hand-wired era:
        // reset runs extra..font with Barrage between Plague and Renewal; the in-battle tick
        // excludes Barrage, which ticks before the !nowIn early-return so learn screens are
        // included.
        _signatures = new ISignature[] { extra, eagle, ricochet, maim, kobu, iai, mushin, larceny, puppeteer, plague, _barrage, _shadowBlade, _provoke, _provokeHold, renewal, rapture, font, feign, benediction, sanctuary, choir, bulwark };
        _fieldSignatures = new ISignature[] { extra, eagle, ricochet, maim, kobu, iai, mushin, larceny, puppeteer, plague, _provokeHold, renewal, rapture, font, feign, benediction, sanctuary, choir, bulwark };
        save.Migrate("gunslinger.json");
        _gunSlinger = new GunSlinger(meta, _kills, save.SaveDir, live, recorder: Flight.Record);
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
        _statusSpike = new StatusSpike(live, modDir);   // LW-58 cold-call research instrument; modDir carries the request-file lane
        _bodyDoubleSpike = new BodyDoubleSpike(live, save.SaveDir);   // LW-58 Canary 8 duplicate-to-AI-fighter; SaveDir = rotation-proof forensics
        _provokeSpike = new ProvokeSpike(live, modDir);   // LW-123 arc 2a mark-planter; modDir carries the provoke_request.txt lane
        _numeralSpike = new NumeralSpike(live, modDir);   // number-popup cold-call instrument; modDir carries the numeral_request.txt lane
#endif
        // LW-184: built last, after every subsystem above exists -- BuildPhases's rows close over
        // them through this engine, so construction order here never matters, only that it runs after.
        _phases = BuildPhases(this);
        LogNames.Init(meta);
        // Launch header L5 (the kill-total half of the old line moved to L3, the load summary).
        ModLogger.Event(LogVerb.Startup, $"Living Weapons is tracking {meta.Count} weapon types.");
    }

    /// <summary>LW-184: the declarative fan-out table Tick()'s post-edge tail steps through, in
    /// order, every tick -- same gates, same cadence, same call order as the hand-rolled kit-lane
    /// trio / throttled gunslinger / <c>if (!nowIn) { ...; return; }</c> branch / the
    /// in-battle tail it replaces. Built once at the end of the ctor; every row reaches the
    /// subsystems through <paramref name="e"/>, dereferenced lazily at Run time. STATIC with a
    /// nullable engine (LW-186):
    /// BuildPhases(null) hands the table to shape-only inspection (EngineTickTableTests reads
    /// Name/Gate/After as pure data, no Engine constructed, no memory touched); every row's Run
    /// closure dereferences e lazily, so with null the shape is the real production table but Run
    /// must never be invoked. Pinned by EngineTests.Phases_match_the_hand_wired_tick_order.</summary>
    internal static TickPhase[] BuildPhases(Engine? e)
    {
        var phases = new List<TickPhase>
        {
            // Ticks in AND out of battle: the learn screen / pre-battle menus read the JobCommand
            // table live. Gated on the kit-lane guard (LW-112), not the main guard, so another mod
            // rewriting the table disables only this trio, never the whole engine.
            new TickPhase("kit-barrage", TickGates.KitLane, 1, false, Array.Empty<string>(),
                _ => e!._barrage.Tick()),
            // Pre-gate like Barrage: the learn screen / menus read the JobCommand table live too.
            new TickPhase("kit-shadowblade", TickGates.KitLane, 1, false, Array.Empty<string>(),
                _ => e!._shadowBlade.Tick()),
            // FindEmptySlot claims the FIRST empty JobCommand slot, so tick order IS slot order --
            // Provoke must run after Barrage and Shadow Blade (see BarrageShadowBladeCollisionTests).
            new TickPhase("kit-provoke", TickGates.KitLane, 1, false, new[] { "kit-barrage", "kit-shadowblade" },
                _ => e!._provoke.Tick()),

            // Pre-gate (Barrage's precedent); re-asserts a snapshotted twin in battle but never
            // snapshots/restores there. Live-verified 2026-07-04: the twin fires twice in battle
            // with this hold. Old name: GunSlingerThrottleEveryNTicks (30, ~1s @ 33ms).
            // secondsOffField mirrors the "paint" phase's own (s.Now - e!._lastField) read below
            // (same clock, same tick-ordering safety: _lastField is frozen for this whole tick
            // before any phase runs) -- LW-193 twilight guard, GunSlingerPolicy.IsTwilight.
            new TickPhase("gunslinger", TickGates.Always, 30, false, Array.Empty<string>(),
                s => e!._gunSlinger.PrepRoster(inBattle: s.NowIn, secondsOffField: (s.Now - e._lastField).TotalSeconds)),
            // Out of battle (slot9 cleared): keep the equip card painted.
            new TickPhase("display-out", TickGates.OutOfBattle, 1, false, Array.Empty<string>(),
                _ => e!._display.Tick(false)),

            // Every ~33ms tick so a fast-forward death's brief hp==0 window isn't missed.
            new TickPhase("kill-poll", TickGates.InBattle, 1, false, Array.Empty<string>(),
                s => s.Changed = e!._tracker.Poll(s.OnField)),
            // Edge-detects each unit's turns for the timed signatures.
            new TickPhase("turn-poll", TickGates.InBattle, 1, false, Array.Empty<string>(),
                _ => e!._turns.Poll()),
            new TickPhase("field-signatures", TickGates.InBattle, 1, false, new[] { "kill-poll", "turn-poll" },
                s =>
                {
                    var ctx = new TickContext(s.Now, s.OnField, s.InLive, s.BattleDisplayed);
                    foreach (var sig in e!._fieldSignatures) sig.Tick(in ctx);
                }),
            // Not an ISignature (no ResetBattle-order dependency): retries a corpse whose despawn
            // transiently refused and pins its crystal counter meanwhile (mirrors Sanctuary).
            new TickPhase("living-poach", TickGates.InBattle, 1, false, Array.Empty<string>(),
                _ => e!._livingPoach.Tick()),
            // Growth holds stats; ~100ms is plenty. Old name: _tick / GrowthEveryNTicks (3 ticks).
            new TickPhase("growth", TickGates.InBattle, 3, true, Array.Empty<string>(),
                _ => e!._growth.Apply()),
            // NOT onField-gated: the facing prompt this queues into can render during mode-1 cast
            // animations too. Delivery itself needs no Tick (fires from the game's own hook).
            new TickPhase("toast", TickGates.InBattle, 1, false, new[] { "kill-poll" },
                s => e!._toast.Tick(s.Changed)),
            // LW-31 stage 2: the Attack-menu desc dossier painter.
            new TickPhase("attack-card", TickGates.InBattle, 1, false, Array.Empty<string>(),
                _ => e!._attackCard.Tick()),
        };
#if LWDEV
        // Dev-only passive/cold-call research instruments, in-battle only (see each Spike class's
        // own doc for its F-key / request-file lane); compiled out of Release entirely.
        phases.Add(new TickPhase("turn-owner-spike", TickGates.InBattle, 1, false, Array.Empty<string>(),
            _ => e!._turnOwnerSpike.Tick()));
        phases.Add(new TickPhase("status-spike", TickGates.InBattle, 1, false, Array.Empty<string>(),
            s => e!._statusSpike.Tick(s.InLive)));
        phases.Add(new TickPhase("body-double-spike", TickGates.InBattle, 1, false, Array.Empty<string>(),
            s => e!._bodyDoubleSpike.Tick(s.InLive)));
        phases.Add(new TickPhase("provoke-spike", TickGates.InBattle, 1, false, Array.Empty<string>(),
            s => e!._provokeSpike.Tick(s.InLive)));
        phases.Add(new TickPhase("numeral-spike", TickGates.InBattle, 1, false, Array.Empty<string>(),
            s => e!._numeralSpike.Tick(s.InLive)));
#endif
        // Reads Changed from kill-poll (After). Mirrors kills.json's on-change save timing (Reliquary).
        phases.Add(new TickPhase("save-on-change", TickGates.InBattle, 1, false, new[] { "kill-poll" },
            s => { if (s.Changed) { e!._tally.Save(); e._legends.SaveIfDirty(); } }));
        // slot9 is still the battle sentinel, but once off-field a beat (battleMode 0 = world-map
        // party menu / post-battle), paint the card instead of just following counts.
        phases.Add(new TickPhase("paint", TickGates.InBattle, 1, false, Array.Empty<string>(),
            s =>
            {
                if (BattleState.ShouldPaintCard(s.BattleStatus, s.OnField, (s.Now - e!._lastField).TotalSeconds, FieldSettleSeconds))
                    e._display.Tick(true);
                else
                    e._display.PaintCountsIfChanged();   // LW-91 stage 2: a mid-battle kill still follows onto the equip card
            }));

        return phases.ToArray();
    }

    /// <summary>The notice-fallback resolution pulled out of the ctor's LaunchGuard wiring so a
    /// test can pin the null-coalesce itself: a verifier proved dropping the
    /// <c>?? StandDownNotice.Show</c> at the wiring site leaves the whole suite green while
    /// production would silently lose the stand-down message box (nothing else in the suite
    /// observes which delegate LaunchGuard actually got). Null (production; Mod.cs never passes
    /// notice) resolves to <see cref="StandDownNotice.Show"/>; a non-null value (tests) passes
    /// through unchanged.</summary>
    internal static Action<string, string> ResolveNotice(Action<string, string>? n) => n ?? StandDownNotice.Show;

    /// <summary>Test/diagnostic seam (LW-150 S5): the battle-exit reset order (see the ctor
    /// comment above the _signatures assignment for why both orders are load-bearing). Pinned by
    /// EngineTests so this sequence can only change on purpose.</summary>
    internal IReadOnlyList<Type> SignatureResetOrder => Array.ConvertAll(_signatures, s => s.GetType());

    /// <summary>Test/diagnostic seam (LW-150 S5): the in-battle field tick order -- excludes
    /// Barrage, ShadowBlade and Provoke (kit-lane trio, pre-gate), all of which still appear in
    /// <see cref="SignatureResetOrder"/>. Pinned by EngineTests.</summary>
    internal IReadOnlyList<Type> FieldTickOrder => Array.ConvertAll(_fieldSignatures, s => s.GetType());

    /// <summary>Test/diagnostic seam (LW-184): the declarative tick fan-out table Tick()'s
    /// post-edge tail steps through every tick. Pinned by EngineTests.</summary>
    internal IReadOnlyList<TickPhase> Phases => _phases;

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
        _livingPoach.ResetBattle();   // LW-167: clears the per-corpse poach dedupe latch
        foreach (var sig in _signatures) sig.ResetBattle();
        // LW-90: promote the ledger's current-attempt write-set. Safe on BOTH edges (this
        // method's contract): the promotion only fires for entries that recorded anything
        // since the last promotion, so the exit+enter double reset cannot wipe the history
        // the restarted battle's capture needs.
        _naturalLedger.OnBattleReset();
        _attackCard.ResetBattle();   // LW-31 stage 2: restore vanilla to any live Attack-menu copies; the cache stays warm for the next battle's re-verify
#if LWDEV
        _bodyDoubleSpike.ResetBattle();   // LW-58: the bind + decoy CT-hold never survive a battle edge
#endif
        // The toast QUEUE deliberately survives battle edges (Patrick-confirmed ruling A) --
        // PromptSwap is stateless (no per-battle state to reset).
    }

    internal void Tick()
    {
        Flight.DrainPending();   // cheap flag check every tick; the actual I/O (if any is pending) runs here, never on the requester's thread

        // LW-50: before any landmark verifies, only retry the guard and do nothing else this
        // tick. Once armed, this check is a single cheap bool read (Step is never called again).
        // LW-82: once the guard has permanently stood down, the read-only anchor scout ticks
        // instead (diagnostics only; it never arms and never writes). A still-Verifying guard
        // ticks neither.
        if (!_launchGuard.Armed)
        {
            _launchGuard.Step();
            if (_launchGuard.State == GuardState.StoodDown) _anchorScout.Tick();
            return;
        }
        // LW-112: the kit-lane guard steps only once the main guard armed; cheap no-op once terminal.
        _launchGuard.StepKitLane();

        uint slot0 = _live.U32(Offsets.Slot0);
        uint slot9 = _live.U32(Offsets.Slot9);
        int battleMode = _live.U8(Offsets.BattleMode);
        // Mode-CHANGE tap (N2): new code -- Engine only edge-detected battle enter/exit before this.
        // First sighting baselines silently (mirrors BattleLog.Observe); only real changes record.
        if (_lastMode == int.MinValue) _lastMode = battleMode;
        else if (battleMode != _lastMode) { Flight.Record("mode", $"battleMode {_lastMode} -> {battleMode}"); _lastMode = battleMode; }
        bool paused = _live.U8(Offsets.PauseFlag) == 1;
        int eventId = _live.U16(Offsets.EventId);   // out-of-live: dialogue/cutscene; in-combat: nameId alias
        var now = DateTime.Now;
        // A genuine in-battle frame: live modes, or the slot0 in-battle marker with a targeting/
        // paused/event excuse. The marker alone lies: pre-1.5 it stuck at the then-marker 0xFF
        // after a battle QUIT, and the 1.5 post-quit value is unverified (LW-42; see
        // Offsets.Slot0InBattleMarker). Gates every module that
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
        if (newGame) { _display.Invalidate(); _toast.Rebaseline(); _naturalLedger.Clear(); }   // LW-90: a new game's units share nameIds with the old save's; stale residue must not correct them
        // Enter is instant; exit is debounced (battleMode flickers, slot9 sticks), UNLESS forceExit
        // fires (LW-56: a detected new game bypasses the debounce entirely). nowIn is sticky through
        // mid-battle dips, flipping only on the debounced or forced edges.
        BattleEdge edge = _battle.Step(slot0, slot9, battleMode, paused, eventId, now, forceExit: newGame);
        bool nowIn = _battle.In;
        bool onField = BattleState.OnField(nowIn, battleMode);
        if (onField) _lastField = now;
        bool battleDisplayed = BattleState.BattleDisplayed(slot9, battleMode);
        if (edge == BattleEdge.Entered) OnBattleEnter(slot0, slot9, battleMode);

        // In-battle "Status" card (a paused, stable menu) -- paint the counter there too.
        // LW-150 S5: the PauseFlag re-read here is DELIBERATELY a second, fresher read, not the
        // `paused` value above -- the tick can run battle-state steps and flush I/O between the
        // two while the game mutates the flag on its own threads; collapsing them would shift
        // StatusCardOpen by a tick (locked ruling -- do not fold into one read).
        bool battleStatus = BattleState.StatusCardOpen(nowIn, battleMode,
                                                       _live.U8(Offsets.PauseFlag) == 1, _live.U8(Offsets.SubmenuFlag) == 1);
        if (battleStatus && !_lastBattleStatus) _display.Invalidate();   // re-find the card's fresh buffers
        _lastBattleStatus = battleStatus;

        if (edge == BattleEdge.Exited) OnBattleExit(slot0, slot9, battleMode, paused, eventId);

        // LW-184: everything past the two battle edges is a declarative fan-out over _phases
        // (see BuildPhases) -- one reused blackboard filled fresh every tick, then stepped in
        // table order. Replaces the hand-rolled kit-lane trio / throttled gunslinger /
        // the old `if (!nowIn) { ...; return; }` branch / the in-battle tail through the paint
        // decision; the OutOfBattle/InBattle gates reproduce that early return exactly (nothing
        // that ran after the old return ran out of battle, and nothing in the old !nowIn branch
        // ran in battle).
        _tickState.Now = now;
        _tickState.OnField = onField;
        _tickState.InLive = inLive;
        _tickState.BattleDisplayed = battleDisplayed;
        _tickState.NowIn = nowIn;
        _tickState.BattleStatus = battleStatus;
        _tickState.KitLaneArmed = _launchGuard.KitLaneArmed;
        _tickState.Changed = false;
        foreach (var p in _phases) p.Step(_tickState);
    }

    /// <summary>The debounced (or LW-56 forced) battle-enter edge body -- extracted verbatim out
    /// of Tick() by the LW-184 phase-table refactor (same statements, same comments, same order).</summary>
    private void OnBattleEnter(uint slot0, uint slot9, int battleMode)
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
        // (pre-1.5 the slot0/slot9 sentinels stuck, the quit-stick trap; the 1.5 restart values
        // are unverified, so the defensive reset stays), which left Larceny's stolen-buff
        // ledger alive into the new battle: the engine wipes statuses at battle start, but Larceny's
        // per-tick Drive re-applied them from the stale ledger, so they carried over and never faded.
        // Resetting here makes a fresh battle start clean however the prior one ended (2026-06-15).
        ResetBattleState();
        // LW-163: drop the pool-paint coverage latch on entry too, not just on exit. WHY this
        // is defense-in-depth, not the primary fix: when the PREVIOUS battle's exit edge did
        // fire, ITS Invalidate() (below, the Exited block) re-latched coverage onto whatever
        // menu buffers were live during the world-map window that followed, and a save-load
        // right after that can swap those buffers out from under the latch before another
        // exit edge ever comes along to drop it again; this enter edge is the first hook after
        // such a load that can drop the stale latch. It structurally CANNOT rescue a battle
        // that starts inside a fully starved bracket (a fast reload that eats the debounce
        // entirely, LW-108's class): BattleState.Step only ever returns Entered from its !In
        // branch, so no enter edge fires there at all, which is exactly why the count-gated
        // re-check in Display.PoolPaint.cs (MaybePoolPaint) carries the actual fix. Also worth
        // naming, not hiding: this same tick already runs Flight.FlushBattleStart's synchronous
        // archive above, so the pool scan this Invalidate can trigger stacks on top of that on
        // the shared background-loop tick; accepted, same as every other enter-tick cost here.
        _display.Invalidate();
    }

    /// <summary>The debounced (or LW-56 forced) battle-exit edge body -- extracted verbatim out
    /// of Tick() by the LW-184 phase-table refactor (same statements, same comments, same order).</summary>
    private void OnBattleExit(uint slot0, uint slot9, int battleMode, bool paused, int eventId)
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
}
