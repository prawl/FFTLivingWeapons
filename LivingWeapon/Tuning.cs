using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Growth tuning in one place so detection, growth, and display agree.
/// Kill thresholds are build-flavored. A DEV build (BuildLinked.ps1 passes -p:LwDev=true, which
/// defines LWDEV) uses {1,2,3} and seeds every weapon to DevKillSeed on load; a PRODUCTION build
/// (Publish.ps1) uses {5,10,15} and seeds nothing.
/// </summary>
internal static class Tuning
{
    /// <summary>Both threshold sets are always compiled so tests can use the dev curve; LWDEV picks the active one.</summary>
    public static readonly int[] DevThresholds = { 1, 2, 3 };    // P3 by the third kill (fast verification)
    public static readonly int[] ProdThresholds = { 5, 10, 15 }; // gentle ramp (2026-08-11 retune from {5,25,50}: one weapon reached 50 kills by mid-game)
#if LWDEV
    public static readonly int[] KillThresholds = DevThresholds;
    /// <summary>The binary's own build flavor, for the launch header. A const rather than build_flavor.txt,
    /// which is BuildLinked's deploy-time marker and can be absent or stale.</summary>
    public const string BuildFlavor = "development";
    /// <summary>DEV. Floor every known weapon to <see cref="DevKillSeed"/> kills on load.</summary>
    public const bool DevSeedAllKills = true;
    /// <summary>DEV. Pool-anchored in-place equip-card Kills paint. Once the writable UE string pool
    /// covers every tracked weapon id, the per-paint DisplaySweep is skipped. Observed 2026-07-07 (memory
    /// lw37-equip-card-redirect-walled) and 2026-07-08. Both flavors enable it; tests default to the sweep path.</summary>
    public const bool PoolPaintEnabled = true;
    /// <summary>DEV. Per-tick battle-event timeline (damage/heal/move) in the log.</summary>
    public const bool VerboseEvents = true;
    /// <summary>DEV pulse retired 2026-06-10 after it verified the write path (band addressing, HP write, MP pair).
    /// Set true only to rerun that experiment; while true it force-heals every ~10s. External probes are
    /// Denuvo-walled, so the DLL is the only instrument.</summary>
    public const bool FontDevPulse = false;
#else
    public static readonly int[] KillThresholds = ProdThresholds;
    /// <summary>The binary's own build flavor, for the launch header (see the LWDEV twin).</summary>
    public const string BuildFlavor = "production";
    /// <summary>Production seeds nothing -- the wielder earns every tier.</summary>
    public const bool DevSeedAllKills = false;
    /// <summary>Pool paint replaces the whole-heap DisplaySweep for the equip-card Kills meter; see the LWDEV
    /// twin (checked 2026-07-08 in a DEV build).</summary>
    public const bool PoolPaintEnabled = true;
    /// <summary>Production logs stay lean. Kills/turns/grants only, no per-tick events.</summary>
    public const bool VerboseEvents = false;
    /// <summary>Never in production.</summary>
    public const bool FontDevPulse = false;
#endif

    /// <summary>kill-tier glow look test. WeaponPalette brightens a grown weapon's palette toward white,
    /// one step per tier (index 0..3). Defaults carried over from tools/probes/lw305_bench_paint.py GLOW_TIER
    /// (commit 2dbab25), untuned pending the look-test (deferred 2026-08-24). Same in both flavors.</summary>
    public static readonly double[] GlowFactors = { 0.0, 0.25, 0.50, 0.75 };

    /// <summary>DEV seed floor 3 == P3 under the dev thresholds, so every +3 signature is live on equip.</summary>
    public const int DevKillSeed = 3;

    /// <summary>Reliquary Phase 1 (docs/RELIQUARY_AC.md): per-archetype kill count that earns that archetype's
    /// Mark (Slayer title). Both curves are always compiled and LWDEV picks one. A single element, since a
    /// Mark is earned once.</summary>
    public static readonly int[] DevMarkThresholds = { 2 };     // fast in-game verification
    public static readonly int[] ProdMarkThresholds = { 10 }; // placeholder, scaled 25 -> 10 with the 2026-08-11 retune; balance pass deferred
#if LWDEV
    public static readonly int[] MarkThresholds = DevMarkThresholds;
#else
    public static readonly int[] MarkThresholds = ProdMarkThresholds;
#endif

    /// <summary>Reliquary Phase 1. Headroom for VictimClass.Archetype's non-Unknown count (4 today:
    /// Caster/Human/Monster/Undead), leaving room for the AC's deferred dragon/species archetypes.
    /// Enforced by TuningTests.MaxArchetypes_...</summary>
    public const int MaxArchetypes = 6;

    /// <summary>CardSites. Consecutive Unreadable anchor-verify misses before PaintAll evicts a
    /// cached equip-card paint site. The old bare-bool AnchorIsLive evicted on a single unreadable frame.
    /// Sits outside the #if LWDEV split because it is a paint-cache hygiene knob, not a difficulty/pacing one.
    /// Tuned from the 2026-08-17 tape. The site count fell 836 -> 726 -> 690 over three maintenance passes
    /// while CoversAllMeta() (Display.PoolPaint.cs) kept reporting complete, since each id has ~5.8 pool
    /// copies (CardSites.cs MaxSites). Three strikes rides that out and still evicts a dead site within a
    /// few 1000ms beats (Display.MaintenanceMs).</summary>
    public const int CardEvictStrikes = 3;

    /// <summary>Display.Heartbeat.cs. Maintenance beats (Display.MaintenanceMs, ~1s) a changed
    /// weapon id may stay in the pending set before RunMaintenance drops it with one Debug line. A watchdog
    /// cap, not a retry budget. It bounds the wait on the premise behind the pending-clear predicate, which
    /// pool region the card materializes from (docs/LIVE_LEDGER.md [card-materializes-from-named-pool], filed
    /// Uncertain 2026-08-17). If that premise is wrong an id never settles, and this turns that into a logged
    /// give-up.</summary>
    public const int CardPendingMaxBeats = 10;

    /// <summary>Display.PoolPaint.cs ReOfferDrainedRegions. Kill switch for the per-region
    /// drain re-offer. It reuses ScanPoolRegion (docs/LIVE_LEDGER.md [flavor-line-overwrite-displays],
    /// PROVEN) on only the regions that fell short of their latch.</summary>
    public const bool CardReOfferEnabled = true;

    /// <summary>AttackCard. How long a cached table copy may fail SyncHit verify before RepaintAll
    /// evicts it (formerly instant evict, then re-census). SyncHit on a cached hit runs only in RepaintAll
    /// (compose-change edge or the 1000ms AttackCard.MaintenanceMs), and the census is already armed when the
    /// strike episode starts, so the window is off the recovery path. 3000ms covers the transient misread on
    /// the 2026-07-21 recon tape (a real handle re-verified within a couple of passes).</summary>
    public const long AttackCardEvictAfterMs = 3000;

    /// <summary>Ticks an armed delayed actor (Dragoon Jump / charged action) survives before decaying. Credit
    /// fires at deadStreak >= DeadNeeded (3), so ~3-4 ticks of margin covers landing to corpse confirmation.
    /// Kept tight (12 ticks, ~400ms at 33ms) so an unrelated later kill cannot consume it.</summary>
    public const int DelayedActorWindow = 12;

    /// <summary>Arm window (ticks) for the untracked cross-turn charge (summoner's summon) no-credit stamp.
    /// Wider than DelayedActorWindow because this arm only sets the no-credit verdict, so a wide window can
    /// miss a kill but never mis-credit. It hedges the unmeasured gap between the Charging-bit clear and the
    /// lethal HP->0 edge. Tune from the charging_probe.py measurement.</summary>
    public const int UntrackedDelayedWindow = 45;   // ~1.5s at the 33ms tick

    /// <summary>KillerStamp (KillerStamp.cs): coarse staleness backstop for the register hypothesis (register
    /// ticks == KillTracker.Poll calls, ~33ms nominal, ~130ms effective). Ordering is the primary gate (arrival
    /// strictly after the latch's resolve tick). Killer arrival-&gt;edge gaps were 2-8 ticks on every verified
    /// tape kill, so 90 is loose. Tune from the stamp-override flight taps.</summary>
    public const int RegisterKillWindow = 90;

    /// <summary>kills -> tier (0..3) against the active thresholds, checked high to low.</summary>
    public static int TierFor(int kills) => TierForIn(kills, KillThresholds);

    /// <summary>The weapon's current tier off the shared kill tally (0 when untallied).</summary>
    public static int TierOf(Dictionary<int, int> kills, int weaponId) =>
        TierFor(kills.TryGetValue(weaponId, out int k) ? k : 0);

    /// <summary>kills -> tier (0..3) against a given threshold set (lets tests check the dev curve).</summary>
    public static int TierForIn(int kills, int[] thresholds) =>
        kills >= thresholds[2] ? 3 : kills >= thresholds[1] ? 2 : kills >= thresholds[0] ? 1 : 0;

    /// <summary>Kills needed for the tier after <paramref name="kills"/>'s, against the active thresholds; null
    /// at max tier. Backs the Attack card's tier-progress meter (AttackCardTail.ComposeTail) and reuses
    /// TierFor's math so the meter cannot drift from the tier boundaries.</summary>
    public static int? NextThresholdFor(int kills) => NextThresholdForIn(kills, KillThresholds);

    /// <summary>NextThresholdFor against a given threshold set (lets tests check the dev curve).</summary>
    public static int? NextThresholdForIn(int kills, int[] thresholds)
    {
        int tier = TierForIn(kills, thresholds);
        return tier >= 3 ? null : thresholds[tier];
    }

    /// <summary>DEV only. Floor every known weapon's kill count to <paramref name="floor"/>. Never lowers a
    /// higher count.</summary>
    public static void SeedKills(IEnumerable<int> weaponIds, Dictionary<int, int> kills, int floor)
    {
        foreach (int id in weaponIds)
            if (!kills.TryGetValue(id, out int k) || k < floor) kills[id] = floor;
    }

    /// <summary>tier -> bonus as a fraction of the wielder's natural stat (PA / MA). Starts conservative since
    /// nerfing earned power is the most-hated kind of nerf; easier to buff than claw back.</summary>
    public static readonly double[] Factor = { 0.00, 0.10, 0.20, 0.30 };

    /// <summary>Speed grows gentler since it adds both damage and turn frequency.</summary>
    public static readonly double[] SpeedFactor = { 0.00, 0.05, 0.10, 0.15 };

    /// <summary>tier -> multiplier for the "pa+ma"/"pa+ma+brave" multi-lane weapons (nine Poles,
    /// Materia Blade, Terrastaff, ten Katanas, Sasori). Its own curve, since the single-stat rate on each of
    /// two or three stats would out-grow the single-lane design.</summary>
    public static readonly double[] MultiFactor = { 0.00, 0.07, 0.13, 0.20 };

    /// <summary>tier -> flat current-brave bonus the "pa+ma+brave" katana lane holds at
    /// Offsets.CBraveCurrent, capped at <see cref="BraveLaneCap"/> and never below natural.</summary>
    public static readonly int[] BraveBonus = { 0, 4, 8, 12 };

    /// <summary>ceiling for the katana lane's held current brave. Separate from <see cref="KobuBraveCap"/>
    /// although both are 97. Kobu's cap belongs to Kiyomori's compose-driven raise (KobuPolicy), this one to the
    /// flat-bonus hold every "pa+ma+brave" katana carries.</summary>
    public const int BraveLaneCap = 97;

    /// <summary>tier -> flat current-faith bonus the "wp+faith" magic-gun lane holds at
    /// Offsets.CFaithCurrent, capped at <see cref="FaithLaneCap"/> and never below natural.</summary>
    public static readonly int[] FaithBonus = { 0, 4, 8, 12 };

    /// <summary>ceiling for the magic-gun lane's held CURRENT faith.</summary>
    public const int FaithLaneCap = 85;

    /// <summary>ceiling for the "hp" lane's held u16 MaxHp (Offsets.CMaxHp). HP grows on
    /// <see cref="Factor"/>; this only caps the result, since a Knight Sword's target can land well past a
    /// sane u16 value.</summary>
    public const int HpCeiling = 999;

    /// <summary>tier -> flat WP bonus WpTableHold writes into the resident item-stats table
    /// (Offsets.ItemStatsBase) for the turn-scoped "wp"/"wp+faith" gun lanes; restored to the baked WP when
    /// the wielder's turn ends.</summary>
    public static readonly int[] WpBonus = { 0, 1, 2, 3 };

    /// <summary>consecutive Band.FlagOwner-unresolved ticks (a transient band-scan gap) a WpTableHold
    /// bump survives before the baked WP is restored; same rule as BulwarkUnresolvedTicks.</summary>
    internal const int WpUnresolvedRestoreTicks = 3;

    /// <summary>tier -> the 2-char name suffix painted on the card ("  " renders as nothing).</summary>
    public static readonly string[] Suffix = { "  ", "+ ", "+2", "+3" };

    /// <summary>Renewal (Mending Staff +3): fraction of max HP each ally within the aura is
    /// healed per wielder turn edge (round away-from-zero, floor 1).</summary>
    public const double RenewalPct = 0.10;

    /// <summary>Spiritual Font (Umbral Rod +3): fraction of max HP regained at a completed-turn edge where the
    /// grid position changed. The runtime writes the restore itself since the engine honors only one movement
    /// passive, so the font bits are retired.</summary>
    public const double FontHpPct = 0.10;

    /// <summary>Spiritual Font. Fraction of max MP regained on the same edge. MP writes ride the band
    /// +0x18/+0x1A pair (checked 2026-06-10), gated per battle by SpiritualFont.MpLayoutOk.</summary>
    public const double FontMpPct = 0.10;

    /// <summary>Plague (Venombolt +3): how far apart (ms) the poison-bit edge and the wielder's acted window may
    /// land and still latch. The engine applies poison during attack resolution, which can precede the observed
    /// window (actor-resolution lag) or trail it (animation tail). A strict same-tick overlap missed every proc
    /// on 2026-06-10 (four open windows, zero latches).</summary>
    public const long PlagueGraceMs = 2000;

    /// <summary>Rapture (Rod of Faith +3): the window arms when HP drops below this fraction of max and holds
    /// until recovery. The 3-turn clock was retired 2026-06-10 since the band CT it read never ticked.</summary>
    public const double RaptureHpPct = 0.30;

    /// <summary>Rapture. The granted movement ability, 243 = Master Teleportation (ability.en key 499); the
    /// player teleported on 2026-06-10. Fallback 242 (plain Teleport) if the arm-time read-back logs MISS.</summary>
    public const int RaptureMoveId = 243;

    /// <summary>Feign Death (Wrathblade +3): how many of the wielder's own turns the played-dead window lasts,
    /// counted off its CT at band +0x25 (Offsets.ACtSlam). A shadow count tracked active turns cleanly on
    /// 2026-06-14 (1@16s, 2@22s, 3@31s). The +0x09 reads flat 0 (the Rapture wall); +0x25 only freezes when
    /// the player idles.</summary>
    public const int FeignPossumTurns = 2;

    /// <summary>Feign Death. The wielder's CT (+0x25) at which it counts as up next. The finishing blow waits
    /// for it so the corpse climbs only a bounded distance before Reraise fires (a 90-step climb from CT ~10
    /// crashed the engine on 2026-06-14; 8 steps from CT 92 revived cleanly). CT reads noisy (peaks 55-92) and
    /// 75 burned several alive turns, so 50 strikes on the first climb toward turn 3. Tune up if it crashes.</summary>
    public const int FeignUpNextCt = 50;

    /// <summary>Feign Death. Wall-clock cap on the played-dead window, firing only if CT stops advancing (the
    /// player idles). <see cref="FeignPossumTurns"/> is the normal limit.</summary>
    public const double FeignPossumSeconds = 90.0;

    /// <summary>Feign Death. After the engine raises the wielder (Reraise fires at CT 100), hold the dead/KO bit
    /// cleared this long so the stand-up leaves no corpse head-marker (hearts) and no skipped turn. The clear
    /// must out-last the engine's revive bookkeeping; 3s observed 2026-06-14.</summary>
    public const double FeignRecoverSeconds = 3.0;

    /// <summary>Iai (Ame-no-Murakumo +3): how far above the field-max Speed to hold the wielder at battle-open.
    /// 1 is strictly above (ties lose), and keeps the post-turn refill slow enough for the 33 ms poll to revert
    /// before a second turn is granted (a flat 99 makes that race unwinnable at ~30 ms/refill).</summary>
    public const int IaiSpeedMargin = 1;

    /// <summary>Iai. Upper sane bound for Speed reads (higher field-max reads are discarded) and writes (clamped
    /// to 1..IaiSpeedSaneMax before every guarded W8 call).</summary>
    public const int IaiSpeedSaneMax = 99;

    /// <summary>Iai (Ame-no-Murakumo +3): wall-clock cap on the opening-turn Speed hold. Backstops the
    /// pointer-based release (Iai.Policy.ReleaseSignal, rebuilt 2026-07-01), which never fires in two cases:
    /// the stale-equal+wait-only double-corner, and a twin-address mismatch (Wielder.Locate resolved a frozen
    /// (0,0) copy). Without the cap the wielder stays fastest all battle.</summary>
    public const double IaiHoldCapSeconds = 90.0;

    /// <summary>Afterimage (Swiftedge +3): flat Speed gained per completed wielder turn while the ramp is
    /// intact. Swiftedge's damage is Speed x WP (formula 99), so each stack adds 1xWP.</summary>
    public const int AfterimageSpeedPerTurn = 1;

    /// <summary>Afterimage. The most stacks the ramp holds. Caps the Speed swing at AfterimageSpeedPerTurn x
    /// this.</summary>
    public const int AfterimageSpeedCap = 5;

    /// <summary>Larceny (Arcanum +3): how many of the wielder's own completed turns a stolen buff lasts, counted
    /// by TurnTracker.Turns for the wielder's fingerprint. The global-turn clock it replaced left haste on in a
    /// normal fight (2026-06-16). 3 matches the card text ("wear 3 turns"). Live-tune knob.</summary>
    public const int LarcenyHoldTurns = 3;

    /// <summary>Puppeteer (Galewind +3): after a puppet is applied, the wielder cannot dominate another enemy
    /// until this many global turns (any unit's turn, TurnTracker.GlobalTurns) have passed since the dominate.
    /// At 4. The dominate turn, three turns where it cannot fire, then it re-arms on the 4th. Anti-snowball cap.
    /// Global rather than wielder turns, since the acting fingerprint flickered to the puppet and a
    /// wielder-keyed cooldown ran backwards.</summary>
    public const int PuppeteerCooldownTurns = 4;

    /// <summary>Puppeteer (Galewind +3): fallback release clock when the wielder could not be fingerprinted at
    /// dominate time (LastActorFingerprint was default); the possession expires after this many global turns.</summary>
    public const int PuppeteerWielderlessFallbackTurns = 12;

    // ── Toasts ────────────────────────────────────────────────────────

    /// <summary>Compiled BannerToasts default and sole source now that the launcher toggle is gone (Engine's
    /// ctor falls back to it). On, since the tier-up callout toast (BannerToast.cs) is the headline feature.</summary>
    public const bool BannerToasts = true;

    /// <summary>Ultima (Materia Blade): tier (row 0..3) x HP% band (col 100 / 75-99 / 50-74 / 25-49 /
    /// &lt;25) -> PA multiplier percent; the held PA is round(naturalPA x pct/100). Applies at every tier, and
    /// the kill tier raises the curve so a +3 blade is not a death trap when hurt. Modeled on FF7's Ultima
    /// Weapon.</summary>
    public static readonly int[][] UltimaMul =
    {
        new[] { 115, 110, 80, 70, 50 },  // +0  (0-4 kills)
        new[] { 120, 113, 83, 73, 53 },  // +1  (5-9)
        new[] { 125, 116, 86, 76, 56 },  // +2  (10-14)
        new[] { 130, 120, 90, 80, 60 },  // +3  (15+)
    };

    /// <summary>Plague (Venombolt +3): the engine deals mhp/8 per poison tick; the runtime adds
    /// mhp*<see cref="PlagueExtraDamageNum"/>/<see cref="PlagueExtraDamageDen"/> on each victim turn, for an
    /// effective 1.75x (1 + 3/4). Floored at 1 so the augment never lands the kill.</summary>
    public const int PlagueExtraDamageNum = 3;
    public const int PlagueExtraDamageDen = 32;

    /// <summary>Poison timer initial value written by the engine on application. The runtime re-stamps it
    /// whenever the timer reads lower, defeating natural expiry and cures; held through a two-healer battle
    /// (memory poison-status-bytes).</summary>
    public const byte PoisonTimerInit = 36;

    /// <summary>Sanctuary (Staff of the Magi +3), value held in the crystal counter (band -0x15 / combat
    /// +0x07) while the bearer is alive, keeping fallen allies revivable.</summary>
    public const byte SanctuaryHearts = 3;

    /// <summary>Bulwark (Sunderer +3, docs/BULWARK_AC.md A7): consecutive ticks the wielder may fail to resolve
    /// (Wielder.ResolveDeployedMainHand returning 0, benched or the two-deployed refusal) before an
    /// active plant releases. One missed tick must not tear down a hold.</summary>
    public const int BulwarkUnresolvedTicks = 10;

    /// <summary>Bulwark (Sunderer +3): BannerToast event key for the plant announcement. Tiers use 1..3 and
    /// Chronicle milestones are negated (BannerToast.cs), so 4 is the next free key.</summary>
    public const int BulwarkToastKey = 4;

    /// <summary>Choir (Warlock's Staff +3): support ability OR-set on adjacent allies so their magick casts
    /// instantly, 227 = Non-charge (ability.en key 483), calc-gated. Swiftspell (226, half-charge) is the
    /// milder alternative.</summary>
    public const int InstantCastSupportId = 227;

    /// <summary>Chain Lightning (Stormarc +3): maximum arcs after the primary hit; each hop re-centers on the
    /// struck unit and picks the nearest unhit enemy.</summary>
    public const int RicochetMaxHops = 3;

    /// <summary>Chain Lightning (Stormarc +3): each hop deals this percent of the previous hop's chip damage,
    /// applied after the base ricochetPct.</summary>
    public const int RicochetHopDecayPct = 60;

    /// <summary>Mushin (Kiku-ichimonji +3): PA-factor bonus on top of the tier factor for the one charged hit
    /// after a full wait turn (no move, no act, MushinPolicy.ShouldArm on the PSX turn flags).
    /// One charge is round(natural*2.05) PA at tier 3, about 1.6x a normal +3 swing.</summary>
    public const double MushinBonus = 0.75;

    /// <summary>Mushin (Kiku-ichimonji +3): the charge is a single bool (one wait, one hit). Named MaxStacks
    /// because MushinPolicy.PaHeld is generic over a stack count; only GrowthEngine.Mushin.cs passes 1.</summary>
    public const int MushinMaxStacks = 1;

    /// <summary>Mushin (Kiku-ichimonji +3): kill tier at which the full-wait charge unlocks, like the other capstones.</summary>
    public const int MushinAtTier = 3;

    /// <summary>Kobu (Kiyomori +3): ceiling for the wielder's current brave (band +0x0F). 97 stays below the
    /// engine's Pray/Steel hard cap (100) while out-braving nearly any foe.</summary>
    public const int KobuBraveCap = 97;

    // -- Provoke hold. Docs/PROVOKE_AC.md. Arc 1 (Provoke.cs) plants the mark; these knobs
    //    tune the hold that reads it. --

    /// <summary>Ship switch for the whole Provoke feature. False makes the hold inert. It never arms,
    /// hides nobody, and writes nothing, including the per-tick player-side mark scrub.
    ///
    /// 2.3.2 shipped this false because docs/PROVOKE_AC.md had not been run. Removing the Defender's
    /// items.json signature block stops the command but not the hold, which gates on the mark bit rather
    /// than meta[33].Signature (arc 2b's plumbing was pending). An enemy carrying status id 0 would hide the
    /// party for a tier-3 Defender; that rests on the unverified premise that vanilla never sets id 0 (the
    /// cast-nothing control battle in docs/PROVOKE_AC.md has not been run). Flipping it takes three edits:
    /// this switch, the id 33 signature block in data/items.json (re-bake meta), and tools/patch_names.py.
    /// ProvokeHoldTests pins the value against the baked block; tools/audit_nxd_bakes.py covers the card text.</summary>
    public const bool ProvokeEnabled = true;

    /// <summary>how much further off the leading enemy's ETA must be than the
    /// marked enemy's own before the hold reveals. "Clearly further away" rather than a photo finish, so a
    /// one-position ordering error in TurnOrder's ranking still leaves the party hidden. A const, so changing
    /// it is a rebuild.</summary>
    public const int ProvokeRevealMarginTicks = 2;

    /// <summary>master switch for the CT+Speed turn-order lookahead (TurnOrder.cs). True runs the
    /// five-branch rule (D1 revised, ProvokeHold.Policy.ActionFor). False skips branches 3/4, so TurnOrder is
    /// never consulted and ActionFor falls to branch 5 (hide unless a player-side seat owns the turn or the
    /// marked enemy is the current actor), the fallback the old WINDOW mode shipped. A rollback lever if the
    /// live pass finds the ranking model unreliable.</summary>
    public const bool ProvokeLookahead = true;

    /// <summary>Provoke hold. The marked enemy's own completed turns (falling edge of the actor-pointer identity
    /// match gated on an enemy turn, decision 10, see ProvokeHold.MarkedIsActor) before the hold releases as
    /// EnemyTurnDone. The count rides the actor-pointer identity match and never the TurnQueue
    /// stat-fingerprint, which collides on identical enemies. AC 1b's target is 3; v1 ships 1.</summary>
    public const int ProvokeTurns = 1;

    /// <summary>Provoke hold. Live battle-time safety cap (decision 11; accrues only while Offsets.PauseFlag
    /// reads unpaused) before a stuck hold force-releases as a Watchdog. Raised 30 -> 90 on 2026-07-27. The
    /// hold armed at 04:30:40 and the marked enemy acted at about 04:31:11, so the old cap would have
    /// released a healthy hold and logged the WARN meaning a release condition was missed. The clock accrues
    /// across the whole hold, so raising ProvokeTurns above 1 needs it raised again.</summary>
    public const double ProvokeWatchdogSeconds = 90.0;

    /// <summary>Provoke hold. Consecutive ticks the marked enemy may fail to re-locate (a transient band-scan
    /// miss) before the hold concludes it is gone (EnemyGone) and releases. One missed tick must not release.</summary>
    public const int ProvokeMarkedMissTicks = 3;

    // -- Living Poach, armed at stage 4 (2026-08-12): Engine wires wasBasicAttack to
    //    LivingPoach.ReadWasBasicAttack, so every gate below is active in production. --

    /// <summary>Living Poach. Damage formulas through which the vanilla Poach support (Key 471, id 215)
    /// is dormant (formula matrix, observed 2026-08-12). A weapon on one of these cannot be poached by
    /// vanilla, so the runtime feature may arm only on this set (the double-fire guard, see
    /// LivingPoachPolicy.Decide). Must equal DORMANT_FORMULAS in tools/lib/flavor.py;
    /// check_dormant_poach_formulas_lockstep in analyze.py pins the pair.</summary>
    internal static readonly int[] DormantPoachFormulas = { 45, 46, 47, 48, 67, 69, 99 };

    /// <summary>True when <paramref name="formula"/> is in <see cref="DormantPoachFormulas"/>; LivingPoach
    /// checks it before considering a poach.</summary>
    internal static bool IsDormantPoachFormula(int formula) => Array.IndexOf(DormantPoachFormulas, formula) >= 0;

    /// <summary>Living Poach. BannerToast event key reserved for a successful poach's carcass
    /// announcement. Distinct from tiers 1..3, <see cref="BulwarkToastKey"/> (4), and the negated Chronicle
    /// milestones.</summary>
    internal const int PoachToastKey = 5;

    /// <summary>Living Poach. The killer's Poach support ability id (Key 471, 256, observed 2026-08-12),
    /// fed through Signatures.SupportBit like Choir's InstantCastSupportId to find the (byteOffset, mask) in
    /// the 4-byte support bitfield (Offsets.CSupport / Offsets.ASupport).</summary>
    internal const int PoachSupportAbilityId = 215;

    /// <summary>Offsets.ArecAbil value meaning the killer's action record names the basic Attack
    /// command, not an ability (docs/LIVE_LEDGER.md "The basic-Attack discriminator" row,
    /// 2026-08-12, probe tools/probes/arec_watch.py). A kind==5 stamp with abil==0 held from before the fatal
    /// blow through credit on every basic-Attack kill, while an ability kill (Rend Weapon, id 141) held its own
    /// id. See LivingPoach.ReadWasBasicAttack.</summary>
    internal const int BasicAttackAbilityId = 0;

    /// <summary>Provoke hold (R2): status ids meaning the provoked enemy cannot take its provoked turn, so the
    /// hold releases instead of waiting for the watchdog. Petrify=8, Confuse=11, Stop=30, Charm=34, Sleep=35,
    /// DontAct=37. Death is already EnemyDead. Frog/Berserk/Blind/Slow/DontMove/DeathSentence are excluded
    /// because the unit still attacks the only visible target (the bearer). Read on the composed layer
    /// (StatusApply.Composed + StatusByte/StatusMask(id)).</summary>
    public static readonly int[] ProvokeDisablingStatusIds = { 8, 11, 30, 34, 35, 37 };
}
