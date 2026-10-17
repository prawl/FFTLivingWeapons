using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivingWeapon;

/// <summary>
/// The Living Weapon runtime. One background loop that counts kills and applies stat
/// growth in battle. Detection and growth share the in-memory <see cref="KillTally"/>,
/// which persists across sessions. Display painting is layered on top.
/// </summary>
internal sealed class Engine
{
    // Fast poll. At fast-forward a death's hp==0 window is brief and a 100ms loop missed kills.
    // Growth runs every Nth tick (the counter now lives on the "growth" TickPhase row).
    private const int PollMs = 33;

    private readonly KillTally _tally;
    private readonly PlaythroughReset _playthroughReset;   // Archives + resets the tally on a new-game opening
    private readonly Dictionary<int, int> _kills;
    private readonly LegendStore _legends;   // Reliquary Phase 1 deed ledger (docs/RELIQUARY_AC.md)
    private readonly Reliquary _reliquary;   // the battle-end summary reads BattleMarks
    private readonly LivingPoach _livingPoach;   // The Poach carcass-drop executor
    private readonly KillTracker _tracker;
    private readonly TurnTracker _turns;
    private readonly NaturalLedger _naturalLedger;   // the shared cross-battle written-target memory
    private readonly GrowthEngine _growth;
    private readonly Barrage _barrage;     // ticks in and out of battle (learn-screen hold)
    private readonly ShadowBlade _shadowBlade; // ticks pre-gate like Barrage
    private readonly Provoke _provoke;     // armed via id 33's signature block
    private readonly ProvokeHold _provokeHold;   // Reads arc 1's mark and gates on the band bit
    private readonly ISignature[] _signatures;        // every signature module, battle-exit reset order
    private readonly ISignature[] _fieldSignatures;   // the in-battle tick order (Barrage ticks pre-gate instead)
    private readonly Display _display;
    private readonly AttackCard _attackCard;   // The Attack-menu desc dossier painter
    private readonly WeaponPalette _weaponPalette;   // the per-turn weapon-sprite palette repaint
    private readonly WpTableHold _wpTableHold;   // turn-scoped WP write for the "wp"/"wp+faith" gun lanes
    private readonly IconGlow _iconGlow;   // Out-of-battle equip-icon glow-rim synced to kill tier
    private readonly ExtendedInventory _extended;   // extended inventory (ids 261+); boot-armed from InjectHooks
    private readonly BattleState _battle = new();      // debounced in/out edges (slot9 sticks; mode flickers)
    private CancellationTokenSource? _cts;
    private DateTime _lastField = DateTime.MinValue;   // last tick we were on the live battlefield
    private const double FieldSettleSeconds = 1.5;      // wait this long off-field before painting
    private bool _lastBattleStatus;                     // edge-detect entering the in-battle status card
    private int _lastMode = int.MinValue;               // edge-detect battleMode changes for the flight recorder (N2)

    // The tick fan-out table (built at the end of the ctor) and its reused per-tick
    // state. Per-phase throttle counters live on the TickPhase rows (e.g. "growth", "gunslinger").
    private readonly TickPhase[] _phases;
    private readonly TickPhaseState _tickState = new();
    private readonly GunSlinger _gunSlinger = null!;
    private readonly IGameMemory _live = null!;   // assigned in ctor, used by Tick
    private readonly LaunchGuard _launchGuard;   // gates every write until landmarks verify
    private readonly AnchorScout _anchorScout;   // verifier scout, ticks only once StoodDown; writes nothing
    private readonly BannerToast _toast;
    private readonly bool _bannerToastsEnabled;
    private readonly PromptSwap _promptSwap;
    private readonly PromptSwapHook _promptSwapHook;
#if LWDEV
    private readonly TurnOwnerSpike _turnOwnerSpike;   // passive turn-owner correlation recorder, dev-only
    private readonly StatusSpike _statusSpike;   // cold-call the status apply engine (F2 canary / F4 chest-convert), dev-only
    private readonly BodyDoubleSpike _bodyDoubleSpike;   // Duplicate a hovered unit into a real AI fighter (F5), dev-only
    private readonly ProvokeSpike _provokeSpike;   // Plant the provoke mark without the real granted command (F6 / provoke_request.txt), dev-only
    private readonly NumeralSpike _numeralSpike;   // cold-call the floating number-popup builder (F8 / numeral_request.txt), dev-only
#endif

    /// <param name="modDir">Mod deployment directory (meta.json lives here).</param>
    /// <param name="bannerToasts">Tier-up toast gate. Null falls back to Tuning.BannerToasts (the launcher toggle is gone).</param>
    /// <param name="devSeedKills">Dev-build kill seeding gate (compiled out of Release). Null seeds every weapon.</param>
    /// <param name="devForceFingerprintMismatch">Dev knob. Perturbs the expected PE build key so LaunchGuard
    /// reports an unknown build without standing down. Compiled always, wired under #if LWDEV in Mod.cs.</param>
    /// <param name="mem">Injectable memory seam. Null builds a <see cref="LiveMemory"/>; tests pass a fake.</param>
    /// <param name="notice">Stand-down notice for LaunchGuard. Null falls back to
    /// <see cref="StandDownNotice.Show"/>; tests pass a no-op so no Win32 dialog opens.</param>
    /// <param name="extended">the extended inventory. Null builds the live one; tests inject fakes.</param>
    /// <param name="devForceSensingMismatch">Dev drill (LW_FORCE_SENSING_MISMATCH, dev builds only): forces the
    /// sensing lane to read a mismatch so the refuse-to-clear path can be watched.</param>
    /// <param name="devForceGuardUnreadable">Dev drill (LW_FORCE_GUARD_UNREADABLE, dev builds only): the startup
    /// guard reads blind for about eighty seconds so the cannot-find-the-game warning and recovery line can be watched.</param>
    public Engine(string modDir, bool? bannerToasts = null, bool? devSeedKills = null,
        bool? devForceFingerprintMismatch = null, IGameMemory? mem = null, Action<string, string>? notice = null,
        ExtendedInventory? extended = null, bool? devForceSensingMismatch = null,
        bool? devForceGuardUnreadable = null)
    {
        // Save files live in the update-safe Reloaded/User/Mods/<ModId> dir so a mod-folder
        // replace cannot wipe them. Each store migrates its legacy file there before first load.
        var save = new SaveLocation(modDir);
        FlavorStamp.Write(save);   // stamp the running build's flavor where the deploy guard can see it
        ModLogger.Event(LogVerb.Save, $"Save files live at {save.SaveDir}.");

        _tally = KillTally.Load(save.Migrate("kills.json"));
        // Observe runs every tick post-arm, gated on the same inLive as the other modules.
        // The call sits ahead of BattleState.Step so its detection edge can force an exit.
        _playthroughReset = new PlaythroughReset(save, _tally);
        _kills = _tally.Kills;
        // Launch header L3. The tally as the disk held it, before the dev seed inflates it.
        ModLogger.Event(LogVerb.Save,
            LaunchHeader.ComposeTally(_tally.Total, _tally.Kills.Count, _tally.LoadedFrom));
        save.Migrate("legends.json");
        _legends = LegendStore.Load(save.SaveDir);   // Reliquary Phase 1 deed ledger, sibling of kills.json
        // Launch header L4. The legends load summary.
        ModLogger.Event(LogVerb.Save,
            LaunchHeader.ComposeLegends(_legends.WeaponCount, _legends.TotalMarks, _legends.LoadedFrom));
        var meta = MetaLoader.Load(modDir);
        // Move progress from a design's old item id to its new one. Runs after the loads and
        // before the dev seed, which would otherwise be summed into the new id.
        MigrateMovedDesigns(meta);
#if LWDEV
        // Dev build only. Seed every weapon to max tier. Compiled out of Release (a runtime `if` on the
        // const would be unreachable code, CS0162). The launcher toggle is gone, so devSeedKills
        // is always null here; the != false guard stays for a future opt-out.
        if (Tuning.DevSeedAllKills && devSeedKills != false)
        {
            Tuning.SeedKills(meta.Keys, _kills, Tuning.DevKillSeed);
            // Launch header L7 (development builds only).
            ModLogger.Event(LogVerb.Startup, $"Development build: every weapon's tally is seeded to at least {Tuning.DevKillSeed} kills; every weapon starts with its full powers for fast testing.");
        }
#endif
        _live = mem ?? new LiveMemory();   // the IGameMemory shared by every subsystem
        var live = _live;
        // Born disarmed (Mod.StartEngine cleared Mem.WritesEnabled); Tick() holds subsystems off
        // until the landmarks verify. recorder/requestFlush archive a stand-down to the flight ring.
        _launchGuard = new LaunchGuard(live, devForceFingerprintMismatch ?? false, notice: ResolveNotice(notice),
            recorder: Flight.Record, requestFlush: Flight.RequestFlush,
            forceSensingMismatch: devForceSensingMismatch ?? false,
            forceUnreadable: devForceGuardUnreadable ?? false);
        // Read-only scout that ticks only while StoodDown; it never arms or writes (a scout, never a healer).
        _anchorScout = new AnchorScout(live);
        _bannerToastsEnabled = bannerToasts ?? Tuning.BannerToasts;
        _toast = new BannerToast(meta, _kills, _bannerToastsEnabled);
        // Reliquary Phase 1 (docs/RELIQUARY_AC.md): deed recorder fed by KillTracker's CreditKill.
        // Marks are release-hidden on every surface, so its toast is null
        // (ReliquaryTests.Disabled_toasts_stay_fully_inert); BannerToast.Enqueue has no enabled gate, so null must
        // be passed. LegendStore still records. Pass `_bannerToastsEnabled ? _toast : null` to re-enable in
        // Reliquary Phase 2, together with the equip-card `legends:` below.
        _reliquary = new Reliquary(_legends, null, meta, Flight.Record);
        // Living Poach (ARMED 2026-08-12): poach.json map plus the executor. Both gates
        // read live memory (ReadKillerHasPoach; ReadWasBasicAttack per the LIVE_LEDGER row "The basic-Attack
        // discriminator"). DeedFanout (docs plan v2 point 4) lets KillTracker's single
        // IDeedSink feed both Reliquary and LivingPoach.
        var poachMap = new PoachMap(modDir);
        _livingPoach = new LivingPoach(meta, poachMap, live, _toast,
            killerHasPoach: id => LivingPoach.ReadKillerHasPoach(live, id),
            wasBasicAttack: id => LivingPoach.ReadWasBasicAttack(live, id));
        var deeds = new DeedFanout(_reliquary, _livingPoach);
        _promptSwap = new PromptSwap(_toast, live);
        _promptSwapHook = new PromptSwapHook(_promptSwap, live);
        _turns = new TurnTracker(live, Flight.Record);
        // verbose. True so the event timeline always reaches the file at Debug tier via [trace]; the
        // console stays at Info. The sink also records each ev. Line into the flight ring; the
        // dual-emit composes here at the composition root (B2) so BattleLog never references Flight.
        // HasLiveWielder reads the same live memory as the other roster/band consumers.
        _tracker = new KillTracker(_kills, live, new HashSet<int>(meta.Keys),
                                   new BattleLog(verbose: true, sink: s => { ModLogger.Debug(LogVerb.Trace, s); Flight.Record("ev", s); }),
                                   Flight.Record, deeds: deeds, hasLiveWielder: id => Wielder.HasLiveWielder(live, id));
        // Mushin stack count. One WielderKeyedStore shared by the trigger (Mushin) and the growth hold
        // (GrowthEngine.HoldMushin). NameId primary, fp fallback (see WielderKeyedStore).
        var mushinArmed = new WielderKeyedStore<Box<int>>();
        // One NaturalLedger shared by every capture-natural hold (GrowthEngine's five lanes plus
        // Iai), so a baked value is recognized after a battle restart. Reset in ResetBattleState and on the new-game edge.
        _naturalLedger = new NaturalLedger();
        _growth = new GrowthEngine(meta, _kills, _turns, live, mushinArmed, _naturalLedger);
        _barrage = new Barrage(meta, _kills, live);                 // Yoichi +3. Grant Barrage command to the wielder
        _shadowBlade = new ShadowBlade(meta, _kills, live);           // Sanguine +3. Grant Shadow Blade (HP-draining dark strike)
        _provoke = new Provoke(meta, _kills, live);                   // Defender +3. Grant Provoke (grants only while id 33 carries its signature block)
        _provokeHold = new ProvokeHold(_kills, live);                 // The hold that reads arc 1's mark and hides the party from the provoked enemy
        var extra = new ExtraTurn(_kills, live);                    // Zwill +3. A kill grants the killer an immediate extra turn
        var eagle = new EagleEye(meta, _kills, _tracker, live);     // Eclipsebolt +3. Hasten the wielder's OWN Doom procs to a 1-turn countdown (attributed only)
        var ricochet = new Ricochet(meta, _kills, _tracker, live);  // Stormarc +3. Bounce chip to nearest other enemy
        var maim = new Maim(meta, _kills, _tracker, live);          // Huntress +3. Struck enemies lose reactions N turns
        var kobu = new Kobu(meta, _kills, _tracker, live);          // Kiyomori +3. On a melee hit, if foe's brave exceeds wielder's, raise wielder's current brave to match
        var iai = new Iai(meta, _kills, live, _naturalLedger);       // Ame-no-Murakumo +3. Hold every deployed wielder's Speed above the field max for the opening turn, released by the engine actor pointer (arrival or acted-edge match) naming the wielder's own band entry
        var mushin = new Mushin(meta, _kills, mushinArmed, live);   // Kiku-ichimonji +3. A full wait turn (no move, no act) arms one charge, spent on the wielder's next own-turn action (the literal PSX turn-flag design, no tracker dependency)
        var plague = new Plague(meta, _kills, _tracker, mem: live); // Venombolt +3. Poison never fades, ticks harder
        var renewal = new Renewal(meta, _kills, _turns, live);     // Mending Staff +3. Turn-edge regen aura to allies within 1 tile (Chebyshev)
        var rapture = new Rapture(meta, _kills, _turns, live);      // Rod of Faith +3. Low-HP Master Teleportation window
        var font = new SpiritualFont(meta, _kills, _tracker, live); // Umbral Rod +3. A moved action restores HP and MP (id 56; the signature moved off the growth-only starter Wellspring, id 51)
        var feign = new FeignDeath(meta, _kills, live);             // Wrathblade +3. A lethal hit becomes a played-dead corpse, engine auto-revives at ~10% HP
        var larceny = new Larceny(meta, _kills, _tracker, _turns, live);  // Arcanum +3. Steal the struck foe's buff onto the wielder (fades after N of the wielder's own turns)
        var puppeteer = new Puppeteer(meta, _kills, _tracker, _turns, live, Flight.Record);  // Galewind +3. Dominate a struck enemy for N of its turns (Puppeteer; successor to Charm-Lock, whose dormant module was deleted). Flight.Record = the recon tap (puppet-turn signals).
        var benediction = new Benediction(meta, _kills, _tracker, live); // Sanctus Staff +3. Ally HP rises boosted 30% while a Sanctus Staff is the last player to act (sticky latch, survives the charged-heal resolve gap)
        var sanctuary = new Sanctuary(meta, _kills, live);               // Staff of the Magi +3. While the bearer lives, fallen allies are held from crystallizing
        var choir = new Choir(meta, _kills, live);                       // Warlock's Staff +3. Adjacent allies cast magick instantly (Non-charge aura)
        // Sunderer +3. A full-wait turn bars the tile at the wielder's back (docs/BULWARK_AC.md).
        // Built after _toast, which it takes.
        var bulwark = new Bulwark(meta, _kills, _toast, live);
        // Order matters. Reset runs extra..font with Barrage between Plague and Renewal. The in-battle
        // tick order excludes Barrage, which ticks pre-gate so learn screens are covered.
        _signatures = new ISignature[] { extra, eagle, ricochet, maim, kobu, iai, mushin, larceny, puppeteer, plague, _barrage, _shadowBlade, _provoke, _provokeHold, renewal, rapture, font, feign, benediction, sanctuary, choir, bulwark };
        _fieldSignatures = new ISignature[] { extra, eagle, ricochet, maim, kobu, iai, mushin, larceny, puppeteer, plague, _provokeHold, renewal, rapture, font, feign, benediction, sanctuary, choir, bulwark };
        save.Migrate("gunslinger.json");
        // Sack reads follow the extended inventory's bag base once it arms. The lambda
        // captures _extended, assigned below, and only runs later on a PrepRoster pass after BootArm.
        _gunSlinger = new GunSlinger(meta, _kills, save.SaveDir, live, recorder: Flight.Record, bagCountBase: () => _extended!.BagCountBase);
        // Marks are release-hidden on the equip card too, so legends is null here (the
        // Attack card already skips the deed ledger via AttackCard.Resolve markLabel=null). Pass legends as _legends
        // to re-enable in Reliquary Phase 2, which rebuilds StoryLines/EarnedAnchors (the three-way anchor, decision 2).
        _display = new Display(meta, _kills, live, legends: null, poolPaint: Tuning.PoolPaintEnabled,
                                recorder: Flight.Record, poolRegionSidecarPath: save.PathFor("pool_regions.json"));
        // The acting unit's Attack-menu dossier. Cursor-only since 2026-07-06 (owner-observed
        // wrong-weapon display; see AttackCard.cs). Flight.Record is its tripwire recorder tap.
        _attackCard = new AttackCard(live, _tracker.ResolveCursorPlayer,
                                      _tracker.SpriteOf, meta, _kills, Flight.Record);
        // Per-turn weapon-sprite palette repaint. takes the kill tally so authored colors
        // glow brighter at each kill tier.
        _weaponPalette = new WeaponPalette(meta, live, _kills);
        // The extended inventory (ids 261+), loaded from modDir/extended_inventory. Armed later from
        // InjectHooks (still before the game runs) because the boot arm cannot wait for LaunchGuard's roster
        // landmark (readable only once a save loads) and the menu registry is built at boot. Built before
        // WpTableHold, which resolves extended WP bytes through it.
        _extended = extended ?? new ExtendedInventory(new LiveCodePatcher(), new LiveNearAllocator(),
            ExtendedInventoryData.Load(modDir), ExtendedBagSidecar.Load(save.PathFor(ExtendedBagSidecar.FileName)));
        // Turn-scoped WP write for the "wp"/"wp+faith" gun lanes; GrowthEngine.Lanes.cs gives them no
        // combat-struct lane, so this is their whole growth. an extended id's WP byte lives in the
        // extended inventory's stub page, so the hold resolves it there.
        _wpTableHold = new WpTableHold(meta, _kills, live, _extended.WeaponRowAddr);
        // Icon glow (retargeted to the deployed loose tex): keeps each weapon's equip icon
        // (modDir/FFTIVC/<baseRel>) synced to its kill tier; file I/O goes through FileIconGlowStore.
        // IconCacheHide makes a pac-mirrored tier change visible this session (see IconCacheHide.cs); it reads
        // _tickState.NowIn live so it sees the current battle status.
        _iconGlow = new IconGlow(modDir, _kills, meta.Keys, new FileIconGlowStore(modDir),
            new IconCacheHide(live, () => _tickState.NowIn));
#if LWDEV
        // Shares the register KillerStamp/AttackCard already use (see TurnOwnerSpike.cs).
        _turnOwnerSpike = new TurnOwnerSpike(live, _tracker.Register);
        _statusSpike = new StatusSpike(live, modDir);   // cold-call research instrument; modDir carries the request-file lane
        _bodyDoubleSpike = new BodyDoubleSpike(live, save.SaveDir);   // duplicate-to-AI-fighter; SaveDir = rotation-proof forensics
        _provokeSpike = new ProvokeSpike(live, modDir);   // mark-planter; modDir carries the provoke_request.txt lane
        _numeralSpike = new NumeralSpike(live, modDir);   // number-popup cold-call instrument; modDir carries the numeral_request.txt lane
#endif
        // Built last; the rows close over the subsystems through this engine.
        _phases = BuildPhases(this);
        LogNames.Init(meta);
        // Launch header L5. The tracking line.
        ModLogger.Event(LogVerb.Startup, $"Living Weapons is tracking {meta.Count} weapon types.");
    }

    /// <summary>carry a save's kills and deeds across a design that changed item id, once, on
    /// the first launch after the move. The pairing comes from meta.json (<c>migratedFrom</c>, baked from
    /// data/items.json); <see cref="TallyMigration"/> owns the rule. Saves only what moved.</summary>
    private void MigrateMovedDesigns(Dictionary<int, WeaponMeta> meta)
    {
        var plan = TallyMigration.Plan(meta);
        if (plan.Count == 0) return;
        int kills = TallyMigration.MoveKills(plan, _kills);
        int deeds = _legends.Migrate(plan);
        if (kills > 0) _tally.Save();
        if (deeds > 0) _legends.SaveIfDirty();
        if (kills > 0 || deeds > 0)
            ModLogger.Event(LogVerb.Save,
                $"Some weapons changed item id in this version; {kills} weapon tall{(kills == 1 ? "y" : "ies")} "
                + $"and {deeds} deed record(s) were moved over so no earned progress was lost "
                + $"({string.Join(", ", Plans(plan))}).");
    }

    /// <summary>"old -&gt; new" for the migration log line, in a stable order.</summary>
    private static IEnumerable<string> Plans(Dictionary<int, int> plan)
    {
        var keys = new List<int>(plan.Keys);
        keys.Sort();
        foreach (int k in keys) yield return $"{k} -> {plan[k]}";
    }

    /// <summary>the declarative fan-out table Tick() steps through every tick, in order. Static
    /// with a nullable engine so EngineTickTableTests can read Name/Gate/After without
    /// constructing an Engine; with null, Run closures must never be invoked. Pinned by
    /// EngineTests.Phases_match_the_hand_wired_tick_order.</summary>
    internal static TickPhase[] BuildPhases(Engine? e)
    {
        var phases = new List<TickPhase>
        {
            // Ticks in and out of battle (learn screen reads the JobCommand table live). Gated on the
            // kit-lane guard so another mod rewriting the table disables only this trio.
            new TickPhase("kit-barrage", TickGates.KitLane, 1, false, Array.Empty<string>(),
                _ => e!._barrage.Tick()),
            new TickPhase("kit-shadowblade", TickGates.KitLane, 1, false, Array.Empty<string>(),
                _ => e!._shadowBlade.Tick()),
            // FindEmptySlot claims the first empty JobCommand slot, so Provoke must tick after Barrage and
            // Shadow Blade (see BarrageShadowBladeCollisionTests).
            new TickPhase("kit-provoke", TickGates.KitLane, 1, false, new[] { "kit-barrage", "kit-shadowblade" },
                _ => e!._provoke.Tick()),

            // Pre-gate; re-asserts a snapshotted twin in battle but never snapshots/restores there. Observed
            // 2026-07-04 (twin fires twice in battle). Cadence 30 (~1s). secondsOffField reads the same _lastField as the "paint" phase (the twilight guard,
            // GunSlingerPolicy.IsTwilight).
            new TickPhase("gunslinger", TickGates.Always, 30, false, Array.Empty<string>(),
                s => e!._gunSlinger.PrepRoster(inBattle: s.NowIn, secondsOffField: (s.Now - e._lastField).TotalSeconds)),
            new TickPhase("display-out", TickGates.OutOfBattle, 1, false, Array.Empty<string>(),
                _ => e!._display.Tick(false)),
            // Out-of-battle equip-icon loose-tex sync (IconGlow.cs). Cadence 30 because kills
            // only change in battle and a slow disk read must not run every 33ms.
            new TickPhase("icon-glow", TickGates.OutOfBattle, 30, false, Array.Empty<string>(),
                _ => e!._iconGlow.Tick()),
            // The resumable pool-region locate, every tick in and out of battle, independent of the
            // paint paths. A synchronous walk measured 7 to 10 seconds before this arc. Heap reads only, so
            // Always rather than InBattle. Retune round (R1/R3): the sole production driver (MaybePoolPaint no
            // longer steps it); s.NowIn picks the budget.
            new TickPhase("pool-locate", TickGates.Always, 1, false, Array.Empty<string>(),
                s => e!._display.StepPoolLocate(s.NowIn)),
            // Extended inventory post-load half (ExtendedInventory.Tick.cs): the two damage caps settle
            // once their pages read vanilla (after a save loads) and new-id bag counts ride the sidecar.
            // Cadence 30 (~1s). Always gate because the bag changes out of battle (shops) and in it (steal,
            // break); both rows no-op until the boot arm armed.
            new TickPhase("extended-caps", TickGates.Always, 30, false, Array.Empty<string>(),
                _ => e!._extended.StepPostLoadCaps()),
            new TickPhase("extended-bag", TickGates.Always, 30, false, Array.Empty<string>(),
                _ => e!._extended.StepBagSidecar(e._live)),
            // Keep the shop-table mirror's vanilla half following the game's table (512-byte compare).
            new TickPhase("extended-shops", TickGates.Always, 30, false, Array.Empty<string>(),
                _ => e!._extended.StepShopSync()),

            // Every tick so a fast-forward death's brief hp==0 window is not missed.
            new TickPhase("kill-poll", TickGates.InBattle, 1, false, Array.Empty<string>(),
                s => s.Changed = e!._tracker.Poll(s.OnField)),
            new TickPhase("turn-poll", TickGates.InBattle, 1, false, Array.Empty<string>(),
                _ => e!._turns.Poll()),
            new TickPhase("field-signatures", TickGates.InBattle, 1, false, new[] { "kill-poll", "turn-poll" },
                s =>
                {
                    var ctx = new TickContext(s.Now, s.OnField, s.InLive, s.BattleDisplayed);
                    foreach (var sig in e!._fieldSignatures) sig.Tick(in ctx);
                }),
            // Retries a corpse whose despawn refused and pins its crystal counter (like Sanctuary).
            new TickPhase("living-poach", TickGates.InBattle, 1, false, Array.Empty<string>(),
                _ => e!._livingPoach.Tick()),
            // Growth holds stats; every 3rd tick (~100ms) is plenty.
            new TickPhase("growth", TickGates.InBattle, 3, true, Array.Empty<string>(),
                _ => e!._growth.Apply()),
            // Not onField-gated. The facing prompt can render during mode-1 cast animations. Delivery fires from the game's own hook.
            new TickPhase("toast", TickGates.InBattle, 1, false, new[] { "kill-poll" },
                s => e!._toast.Tick(s.Changed)),
            // The Attack-menu desc dossier painter.
            new TickPhase("attack-card", TickGates.InBattle, 1, false, Array.Empty<string>(),
                _ => e!._attackCard.Tick()),
            // The per-turn weapon-sprite palette repaint (WeaponPalette.cs).
            new TickPhase("weapon-palette", TickGates.InBattle, 1, false, Array.Empty<string>(),
                s => e!._weaponPalette.Tick(s.InLive)),
            // The turn-scoped WP write for the "wp"/"wp+faith" gun lanes (WpTableHold.cs).
            new TickPhase("wp-table-hold", TickGates.InBattle, 1, false, Array.Empty<string>(),
                s => e!._wpTableHold.Tick(s.InLive)),
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
        // Reads Changed from kill-poll; saves the tally and the Reliquary ledger when a kill landed.
        phases.Add(new TickPhase("save-on-change", TickGates.InBattle, 1, false, new[] { "kill-poll" },
            s => { if (s.Changed) { e!._tally.Save(); e._legends.SaveIfDirty(); } }));
        // slot9 is still the battle sentinel; once off-field a beat (battleMode 0. World-map menu or post-battle), paint the card.
        phases.Add(new TickPhase("paint", TickGates.InBattle, 1, false, Array.Empty<string>(),
            s =>
            {
                if (BattleState.ShouldPaintCard(s.BattleStatus, s.OnField, (s.Now - e!._lastField).TotalSeconds, FieldSettleSeconds))
                    e._display.Tick(true);
                else
                    e._display.PaintCountsIfChanged();   // A mid-battle kill still follows onto the equip card
            }));

        return phases.ToArray();
    }

    /// <summary>Static so a test can pin the <c>?? StandDownNotice.Show</c> fallback; dropping it once left the suite green.</summary>
    internal static Action<string, string> ResolveNotice(Action<string, string>? n) => n ?? StandDownNotice.Show;

    /// <summary>Test seam. The battle-exit reset order. Pinned by EngineTests.</summary>
    internal IReadOnlyList<Type> SignatureResetOrder => Array.ConvertAll(_signatures, s => s.GetType());

    /// <summary>Test seam. The in-battle field tick order. Excludes the pre-gate kit-lane trio
    /// (Barrage, ShadowBlade, Provoke), which still appear in <see cref="SignatureResetOrder"/>.</summary>
    internal IReadOnlyList<Type> FieldTickOrder => Array.ConvertAll(_fieldSignatures, s => s.GetType());

    /// <summary>Test seam. The tick fan-out table. Pinned by EngineTests.</summary>
    internal IReadOnlyList<TickPhase> Phases => _phases;

    /// <summary>Late-injected by Mod.Start/StartEx once the loader resolves reloaded.sharedlib.hooks.
    /// Arms the facing-prompt swap here (always on, the launcher toggle is gone).</summary>
    public void InjectHooks(Reloaded.Hooks.Definitions.IReloadedHooks? hooks)
    {
        // The extended inventory arms now, before the game runs, behind per-site old-byte checks;
        // it cannot wait for LaunchGuard (the roster landmark needs a loaded save, and the menu registry is
        // built at boot). A null hooks is passed through so the refusal reason is logged.
        _extended.BootArm(hooks);
        if (hooks == null) return;
        // Arm through LaunchGuard's hook handshake so the hook waits for the Armed edge, or never
        // fires if the guard stands down.
        if (_bannerToastsEnabled) _launchGuard.OfferHookArm(() => _promptSwapHook.Arm(hooks));
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            // Launch header L6. Liveness canary that closes the header.
            ModLogger.Event(LogVerb.Startup, "The runtime loop has started.");
            while (!token.IsCancellationRequested)
            {
                // A persistent fault would repeat every 33ms; the console dedup (C1) collapses it to once per battle.
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

    /// <summary>Clear all per-battle state. Kill tracker, turn clocks, growth holds, signature ledgers.
    /// Fires on both battle edges so a restart that skips a clean Exit still starts clean (Larceny
    /// stolen-buff carryover, 2026-06-15). Idempotent.</summary>
    private void ResetBattleState()
    {
        // A toast never outlives its battle. Runs first so dropped-toast Flight records land on
        // this battle's tape, ahead of the exit edge's Flush(battle-exit).
        _toast.DropPendingAtBattleEnd();
        _tracker.ResetBattle();
        _turns.ResetBattle();
        _growth.ResetBattle();
        _reliquary.ResetBattle();   // per-battle Marks ledger (the exit edge composes its summary before this runs)
        _livingPoach.ResetBattle();   // clears the per-corpse poach dedupe latch
        foreach (var sig in _signatures) sig.ResetBattle();
        // Promote the ledger's write-set. Safe on both edges. It only fires for entries that
        // recorded something since the last promotion, so the exit+enter double reset keeps the history.
        _naturalLedger.OnBattleReset();
        _attackCard.ResetBattle();   // Restore vanilla to any live Attack-menu copies; the cache stays warm for the next battle's re-verify
        _weaponPalette.ResetBattle();   // the banks were just refreshed from file; the paint latch is stale by definition
        _wpTableHold.ResetBattle();     // the resident stats table gets no battle-load refresh, so a physical restore is mandatory
#if LWDEV
        _bodyDoubleSpike.ResetBattle();   // the bind + decoy CT-hold never survive a battle edge
#endif
    }

    internal void Tick()
    {
        Flight.DrainPending();   // cheap flag check every tick; the actual I/O (if any is pending) runs here, never on the requester's thread

        // Before any landmark verifies, only retry the guard. once permanently stood down,
        // the read-only anchor scout ticks instead (diagnostics only). A still-Verifying guard ticks neither.
        if (!_launchGuard.Armed)
        {
            _launchGuard.Step();
            if (_launchGuard.State == GuardState.StoodDown) _anchorScout.Tick();
            return;
        }
        // The kit-lane guard steps only after the main guard armed.
        _launchGuard.StepKitLane();
        // The sensing lane (battle-sentinel verification) gates the new-game detector below.
        _launchGuard.StepSensing();

        uint slot0 = _live.U32(Offsets.Slot0);
        uint slot9 = _live.U32(Offsets.Slot9);
        int battleMode = _live.U8(Offsets.BattleMode);
        if (_lastMode == int.MinValue) _lastMode = battleMode;
        else if (battleMode != _lastMode) { Flight.Record("mode", $"battleMode {_lastMode} -> {battleMode}"); _lastMode = battleMode; }
        bool paused = _live.U8(Offsets.PauseFlag) == 1;
        int eventId = _live.U16(Offsets.EventId);   // out-of-live. Dialogue/cutscene; in-combat. NameId alias
        var now = DateTime.Now;
        // A genuine in-battle frame. Live modes, or the slot0 marker with a targeting/paused/event excuse.
        // The marker alone lies. Pre-1.5 it stuck at 0xFF after a battle QUIT, and the 1.5 post-quit value
        // is unverified (Offsets.Slot0InBattleMarker). Computed before BattleState.Step so
        // PlaythroughReset's detection edge can reach Step as a forced exit on the same tick.
        bool inLive = BattleState.InLiveBattle(slot0, battleMode, paused, eventId);
        // Gated on inLive so a mid-battle dialogue frame aliasing eventId==2 is not read as the
        // new-game opening. its detection edge feeds Step as a forced exit, flushing stale per-battle
        // state (the KillTracker weapon latch) that an in-session New Game would carry into the Orbonne opener.
        // The detector clears data only once the sensing lane verified the battle sentinels
        // (LaunchGuard.Sensing.cs). Fired = data cleared; Refused = signal qualified, nothing touched. State
        // hygiene below runs on either; only the natural ledger waits for Fired.
        var reset = _playthroughReset.Observe(eventId, battleMode, inLive, _launchGuard.SensingArmed);
        bool newGame = reset != ResetVerdict.None;
        if (newGame && _battle.In)
            ModLogger.Event(LogVerb.BattleEnd, "A new game signal was seen while battle state was still live; forcing the battle exit edge to flush stale attribution state.");
        // A main-menu New Game fires no Exited edge (BattleState's In==false branch cannot return
        // it), leaving stale painted suffix bytes in the equip-card pool for the cleared tally to inherit.
        // Invalidate here is idempotent and harmless if the forced-exit edge also invalidates. the
        // same edge re-baselines the toast queue so a post-reset first kill is not read as a rollback.
        if (newGame) { _display.Invalidate(); _toast.Rebaseline(); }
        if (reset == ResetVerdict.Fired) _naturalLedger.Clear();   // a new game's units share nameIds with the old save's; stale residue must not correct them
        // Enter is instant; exit is debounced (battleMode flickers, slot9 sticks) unless forceExit fires
        // (a detected new game). nowIn is sticky through mid-battle dips.
        BattleEdge edge = _battle.Step(slot0, slot9, battleMode, paused, eventId, now, forceExit: newGame);
        bool nowIn = _battle.In;
        bool onField = BattleState.OnField(nowIn, battleMode);
        if (onField) _lastField = now;
        bool battleDisplayed = BattleState.BattleDisplayed(slot9, battleMode);
        if (edge == BattleEdge.Entered) OnBattleEnter(slot0, slot9, battleMode);

        // In-battle Status card (a paused, stable menu): paint the counter there too. The
        // PauseFlag is re-read here, fresher than `paused` above, because the game mutates it on other
        // threads between the two; folding into one read would shift StatusCardOpen by a tick.
        bool battleStatus = BattleState.StatusCardOpen(nowIn, battleMode,
                                                       _live.U8(Offsets.PauseFlag) == 1, _live.U8(Offsets.SubmenuFlag) == 1);
        if (battleStatus && !_lastBattleStatus) _display.Invalidate();   // re-find the card's fresh buffers
        _lastBattleStatus = battleStatus;

        if (edge == BattleEdge.Exited) OnBattleExit(slot0, slot9, battleMode, paused, eventId);

        // Everything past the two battle edges is a fan-out over _phases (see BuildPhases): one
        // reused blackboard filled each tick, stepped in table order.
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

    /// <summary>The debounced (or new-game forced) battle-enter edge body.</summary>
    private void OnBattleEnter(uint slot0, uint slot9, int battleMode)
    {
        ModLogger.NoteBattleEdge();   // console-only per-battle dedup reset (C1); file sink unaffected
        ModLogger.EventWithTrace(LogVerb.BattleStart, "Battle started.",
            $"battle-start sentinels (slot0={slot0:X} slot9={slot9:X} mode={battleMode})");
        // Archive the ring before the new battle's events pour in. Sessions ending in a process kill never
        // fire the exit flush, so the enter edge rescues the previous battle's tail (observed 2026-07-04:
        // three sessions, zero archives).
        Flight.FlushBattleStart();
        // Reset on enter too. A battle restart can re-enter without a clean Exit (pre-1.5 the slot0/slot9
        // sentinels stuck, the quit-stick trap; the 1.5 restart values are unverified). Without it Larceny's
        // stale ledger re-applied stolen buffs in the new battle (2026-06-15).
        ResetBattleState();
        // Drop the pool-paint coverage latch on entry too. Defense in depth. A save-load after the
        // previous exit's Invalidate() can swap the buffers under the latch with no exit edge to drop it.
        // A fast reload that eats the debounce gets no enter edge at all; the count-gated
        // re-check in Display.PoolPaint.cs (MaybePoolPaint) covers that. The pool scan stacks on top of
        // Flight.FlushBattleStart's archive on this tick; accepted.
        _display.Invalidate();
    }

    /// <summary>The debounced (or new-game forced) battle-exit edge body.</summary>
    private void OnBattleExit(uint slot0, uint slot9, int battleMode, bool paused, int eventId)
    {
        // Match-report summary, composed from the per-battle counters before ResetBattleState() wipes them.
        // On a forced exit the tally was already archived and cleared this tick, so the summary may
        // be empty; accepted.
        string summary = BattleSummary.Compose(
            _tracker.BattleCredits, _kills, _reliquary.BattleMarks, _tracker.FallbackCredits,
            _turns.GlobalTurns, LogNames.Weapon, Tuning.TierFor);
        ModLogger.EventWithTrace(LogVerb.BattleEnd, summary,
            $"battle-end sentinels (slot0={slot0:X} slot9={slot9:X} mode={battleMode} paused={paused} event={eventId})");
        // Re-emit the identity census on every exit (normal and forced). The enter-side census
        // can miss a whole battle when oracle coverage never completes.
        _tracker.EmitExitCensus();
        ResetBattleState();
        _tally.Save();               // flush on battle end
        _legends.SaveIfDirty();      // Reliquary ledger, saved with the tally
        // S2. Flight flush is exit-only; ResetBattleState() runs on both edges, so it cannot live there.
        Flight.FlushBattleEnd();
        _display.Invalidate();       // re-find the menu's freshly-allocated render copies
        ModLogger.NoteBattleEdge();  // console-only per-battle dedup reset (C1); file sink unaffected
    }
}
