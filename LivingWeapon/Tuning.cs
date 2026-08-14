using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Living-Weapon growth tuning. Kept in one place so detection, growth, and display agree.
///
/// Kill thresholds are build-gated: a DEV build (BuildLinked.ps1 passes -p:LwDev=true, which
/// defines LWDEV) uses {1,2,3} so a weapon hits P3 in three kills, AND pre-seeds every weapon
/// to DevKillSeed (3 == P3) on load -- so every +3 signature is live the moment the weapon is
/// equipped. A PRODUCTION build (Publish.ps1, no flag) uses the real curve {5,10,15} and seeds nothing.
/// </summary>
internal static class Tuning
{
    /// <summary>Both threshold sets, ALWAYS compiled (so a test can reason about the dev curve even
    /// though tests compile under prod). The active one is selected by the LWDEV flag below.</summary>
    public static readonly int[] DevThresholds = { 1, 2, 3 };    // P3 by the third kill (fast verification)
    public static readonly int[] ProdThresholds = { 5, 10, 15 }; // gentle ramp (2026-08-11 retune): the
        // owner's first full playthrough reached 50 kills on only ONE weapon by mid-game, so most
        // weapons never awakened under the old {5,25,50} curve
#if LWDEV
    public static readonly int[] KillThresholds = DevThresholds;
    /// <summary>The binary's own build flavor, for the launch header (logging facelift). A
    /// compiled const, NOT build_flavor.txt: that file is BuildLinked's deploy-time guard
    /// marker, absent in a Publish-zip install and possibly stale against the running DLL.</summary>
    public const string BuildFlavor = "development";
    /// <summary>DEV: floor every known weapon to <see cref="DevKillSeed"/> kills on load.</summary>
    public const bool DevSeedAllKills = true;
    /// <summary>DEV: pool-anchored in-place equip-card Kills paint (LW-37). Once the writable
    /// UE string pool is located and fully covers every tracked weapon id, the per-paint
    /// whole-heap DisplaySweep is skipped. Mechanism observed live 2026-07-07 (memory
    /// lw37-equip-card-redirect-walled) AND observed live 2026-07-08 through this exact code path
    /// (card reads the live count, sweep retired, foreign surfaces untouched). Enabled in both
    /// build flavors; Engine reads this flag, tests default to the sweep path independent of it.</summary>
    public const bool PoolPaintEnabled = true;
    /// <summary>DEV: per-tick battle-event timeline (damage/heal/move) in the log.</summary>
    public const bool VerboseEvents = true;
    /// <summary>DEV pulse RETIRED 2026-06-10 after it verified the full write path on screen
    /// (band addressing + HP write + MP pair, watched live). Flip back to true only to re-run
    /// that experiment -- while true it force-heals every ~10s and drowns out the real trigger.
    /// External probes are Denuvo-walled, so the DLL remains the only instrument for it.</summary>
    public const bool FontDevPulse = false;
#else
    public static readonly int[] KillThresholds = ProdThresholds;
    /// <summary>The binary's own build flavor, for the launch header (see the LWDEV twin).</summary>
    public const string BuildFlavor = "production";
    /// <summary>Production seeds nothing -- the wielder earns every tier.</summary>
    public const bool DevSeedAllKills = false;
    /// <summary>Pool paint replaces the whole-heap DisplaySweep for the equip-card Kills meter.
    /// Live-verified 2026-07-08 in a DEV build (card reads the live count, sweep retired, foreign
    /// surfaces untouched); enabled in release too (see the LWDEV twin).</summary>
    public const bool PoolPaintEnabled = true;
    /// <summary>Production logs stay lean: kills/turns/grants only, no per-tick events.</summary>
    public const bool VerboseEvents = false;
    /// <summary>Never in production.</summary>
    public const bool FontDevPulse = false;
#endif

    /// <summary>DEV seed floor: every weapon starts at least this many kills. 3 (== P3 under the dev
    /// thresholds) so every +3 signature is live the moment the weapon is equipped.</summary>
    public const int DevKillSeed = 3;

    /// <summary>Reliquary Phase 1 (docs/RELIQUARY_AC.md): the per-archetype kill count at which a
    /// weapon earns that archetype's Mark (Slayer title). Both curves ALWAYS compiled (mirrors
    /// DevThresholds/ProdThresholds above) so a test can reason about the dev curve even though
    /// tests compile under prod; the active one is selected by LWDEV below. A single-element
    /// array -- unlike the 3-tier kill curve, a Mark is earned once (no P1/P2/P3 progression).</summary>
    public static readonly int[] DevMarkThresholds = { 2 };     // fast in-game verification
    public static readonly int[] ProdMarkThresholds = { 10 };   // PLACEHOLDER -- deferred balance pass;
        // scaled down 25 -> 10 alongside the 2026-08-11 kill-curve retune (still a placeholder, not tuned)
#if LWDEV
    public static readonly int[] MarkThresholds = DevMarkThresholds;
#else
    public static readonly int[] MarkThresholds = ProdMarkThresholds;
#endif

    /// <summary>Reliquary Phase 1: headroom for VictimClass.Archetype's non-Unknown count (today
    /// 4: Caster/Human/Monster/Undead). 6 leaves room for the AC's deferred dragon/species
    /// archetypes without a re-tune; enforced by a unit test (TuningTests.MaxArchetypes_...).</summary>
    public const int MaxArchetypes = 6;

    /// <summary>AttackCard (LW-91): how long a cached table copy may fail its SyncHit verify
    /// before RepaintAll evicts it, instead of the old instant-evict-then-re-census. SyncHit for
    /// an already-cached hit runs only inside RepaintAll -- a compose-change edge, or the 1000ms
    /// maintenance cadence (AttackCard.MaintenanceMs) -- so steady state is about one verify pass
    /// per second; the census that will find any genuine replacement is already armed the moment
    /// the strike episode starts (RepaintAll's episode-start term), so this window is off the
    /// recovery critical path. 3000ms costs a genuinely dead buffer only about three guarded
    /// reads before it is dropped, while covering the transient live-buffer misread that was the
    /// dominant failure on the 2026-07-21 recon tape (a real handle re-verified clean within a
    /// couple of passes, well under this window).</summary>
    public const long AttackCardEvictAfterMs = 3000;

    /// <summary>Ticks an armed delayed actor (Dragoon Jump / charged action) survives before it
    /// decays. The bit-clear == kill-lands; credit fires at deadStreak >= DeadNeeded (3), so ~3-4
    /// ticks of margin covers the gap between landing and corpse confirmation. Kept TIGHT (12 ticks
    /// ~400ms at 33ms) so an unrelated later kill cannot consume the armed actor.</summary>
    public const int DelayedActorWindow = 12;

    /// <summary>Arm window (ticks) for the UNTRACKED cross-turn charge (summoner's summon) no-credit
    /// stamp. DELIBERATELY wider than DelayedActorWindow: the untracked arm only ever sets the
    /// no-credit verdict (it never credits a weapon), so a wide window can at worst MISS an unrelated
    /// armed kill that matures inside it -- never mis-credit. Wider hedges the unproven gap between the
    /// summon's Charging-bit clear and the lethal-damage HP->0 edge (the Jump window is tuned tight
    /// against over-CREDIT, which does not apply here). TUNE from the live charging_probe.py measurement
    /// of the bit-clear -> death gap.</summary>
    public const int UntrackedDelayedWindow = 45;   // ~1.5s at the 33ms tick

    /// <summary>KillerStamp (death-edge culprit stamp, KillerStamp.cs): coarse staleness BACKSTOP
    /// for the register hypothesis (register ticks == KillTracker.Poll calls, ~33ms nominal /
    /// ~130ms effective). The PRIMARY gate is ordering (the arrival must be strictly after the
    /// latch's own resolve tick); this window is a secondary hedge only. Observed killer
    /// arrival-&gt;edge gaps were 2-8 ticks across every verified tape kill, so 90 is deliberately
    /// loose -- tune from the stamp-override flight taps.</summary>
    public const int RegisterKillWindow = 90;

    /// <summary>kills -> tier (0..3) against the active thresholds, checked high to low.</summary>
    public static int TierFor(int kills) => TierForIn(kills, KillThresholds);

    /// <summary>The weapon's current tier straight off the shared kill tally (0 when untallied).
    /// The one lookup every signature module and the growth router key on.</summary>
    public static int TierOf(Dictionary<int, int> kills, int weaponId) =>
        TierFor(kills.TryGetValue(weaponId, out int k) ? k : 0);

    /// <summary>kills -> tier (0..3) against a given threshold set (lets tests check the dev curve).</summary>
    public static int TierForIn(int kills, int[] thresholds) =>
        kills >= thresholds[2] ? 3 : kills >= thresholds[1] ? 2 : kills >= thresholds[0] ? 1 : 0;

    /// <summary>The kill count needed to reach the tier just past <paramref name="kills"/>'s current
    /// one, against the active thresholds. Null at max tier (3): there is nowhere further to
    /// climb. Backs the Attack card's tier-progress meter (AttackCardTail.ComposeTail): threading it
    /// through TierFor's own tier math keeps the meter's "X to next" number from ever drifting out
    /// of sync with the tier boundaries it displays.</summary>
    public static int? NextThresholdFor(int kills) => NextThresholdForIn(kills, KillThresholds);

    /// <summary>NextThresholdFor against a given threshold set (lets tests check the dev curve).</summary>
    public static int? NextThresholdForIn(int kills, int[] thresholds)
    {
        int tier = TierForIn(kills, thresholds);
        return tier >= 3 ? null : thresholds[tier];
    }

    /// <summary>DEV ONLY: floor every known weapon's kill count to <paramref name="floor"/>. Purely
    /// additive -- never lowers an already-higher count (so a weapon that actually climbed past it
    /// keeps its progress). Lets every weapon sit at max tier for fast in-game testing.</summary>
    public static void SeedKills(IEnumerable<int> weaponIds, Dictionary<int, int> kills, int floor)
    {
        foreach (int id in weaponIds)
            if (!kills.TryGetValue(id, out int k) || k < floor) kills[id] = floor;
    }

    /// <summary>tier -> bonus as a fraction of the wielder's natural stat (PA / MA).
    /// Deliberately CONSERVATIVE: an investment mechanic must start under-tuned, because nerfing
    /// earned (kill-grown) power is the most-hated kind of nerf. Easier to buff up than claw back.</summary>
    public static readonly double[] Factor = { 0.00, 0.10, 0.20, 0.30 };

    /// <summary>Speed grows gentler still -- it double-dips (damage AND turn frequency).</summary>
    public static readonly double[] SpeedFactor = { 0.00, 0.05, 0.10, 0.15 };

    /// <summary>tier -> the 2-char name suffix painted on the card ("  " renders as nothing).</summary>
    public static readonly string[] Suffix = { "  ", "+ ", "+2", "+3" };

    /// <summary>Renewal (Mending Staff +3): fraction of max HP each ally within the aura is
    /// healed per wielder turn edge (round away-from-zero, floor 1).</summary>
    public const double RenewalPct = 0.10;

    /// <summary>Spiritual Font (Umbral Rod +3): fraction of max HP the wielder regains at a
    /// completed-turn edge where their grid position changed (the runtime writes the restore
    /// itself -- the engine honors only ONE movement passive, so the font bits are retired).</summary>
    public const double FontHpPct = 0.10;

    /// <summary>Spiritual Font: fraction of max MP regained on the same moved-turn edge. MP writes
    /// ride the band +0x18/+0x1A pair (live-verified 2026-06-10), gated per battle (SpiritualFont.MpLayoutOk).</summary>
    public const double FontMpPct = 0.10;

    /// <summary>Plague (Venombolt +3): how far apart (ms) the poison-bit edge and the wielder's
    /// acted window may land and still latch. The engine applies poison during attack resolution,
    /// which can precede the observed window (actor-resolution lag) or trail it (animation tail);
    /// a strict same-tick overlap missed every proc live (2026-06-10: four open windows, zero
    /// latches, a chocobo cleansed the "permanent" poison).</summary>
    public const long PlagueGraceMs = 2000;

    /// <summary>Rapture (Rod of Faith +3): the window arms when the wielder's HP drops strictly
    /// below this fraction of max. Held UNTIL RECOVERY (no turn cap -- the 3-turn clock was
    /// retired 2026-06-10: the band CT it read never ticked live, while the recovery release
    /// was player-verified the same session).</summary>
    public const double RaptureHpPct = 0.30;

    /// <summary>Rapture: the granted movement ability -- 243 = Master Teleportation (ability.en
    /// key 499). CONFIRMED LIVE 2026-06-10: the player teleported, so the engine honors the bit.
    /// Fallback: flip to 242 (plain Teleport) if the arm-time read-back ever logs MISS.</summary>
    public const int RaptureMoveId = 243;

    /// <summary>Feign Death (Wrathblade +3): how many of the wielder's OWN turns the played-dead
    /// window lasts, counted off its live CT at band +0x25 (Offsets.ACtSlam). PROVEN 2026-06-14: a
    /// shadow count tracked active turns cleanly (1@16s, 2@22s, 3@31s). The wielder's +0x09 reads flat
    /// 0 (the Rapture wall); +0x25 reads clean during ACTIVE play and only freezes when the player
    /// sits idle -- which a real battle never does mid-turn.</summary>
    public const int FeignPossumTurns = 2;

    /// <summary>Feign Death: the wielder's CT (+0x25) at which it counts as "up next in the queue" --
    /// climbed toward its turn, another unit still active. The finishing blow waits for this so the
    /// force-killed corpse is dead-and-scheduled for only a bounded climb before its turn fires the
    /// Reraise (a 90-step dead-climb from CT ~10 CRASHED the engine 2026-06-14; an 8-step from CT 92
    /// revived cleanly). The wielder's climbing CT reads noisy/variable (peaks seen 55-92), so a HIGH
    /// threshold (75) skips low-reading climbs -> the wielder burns several alive turns before the
    /// strike lands. 50 strikes on the FIRST climb toward turn 3 (~50-step dead-climb) -- the bet is
    /// that window is still short enough to dodge the crash. Tune up if it crashes, down if too slow.</summary>
    public const int FeignUpNextCt = 50;

    /// <summary>Feign Death: wall-clock SAFETY CAP on the played-dead window -- only fires if the CT
    /// stops advancing (the player idles), so the possum can never last forever. The turn count
    /// (<see cref="FeignPossumTurns"/>) is the real lever.</summary>
    public const double FeignPossumSeconds = 90.0;

    /// <summary>Feign Death: after the engine raises the wielder (Reraise fires at CT 100), hold the
    /// dead/KO bit CLEARED for this long so the stand-up leaves no corpse head-marker (hearts) and no
    /// skipped turn -- the bit-clear must out-last the engine's revive bookkeeping. 3s observed live
    /// 2026-06-14.</summary>
    public const double FeignRecoverSeconds = 3.0;

    /// <summary>Iai (Ame-no-Murakumo +3): how far above the field-max Speed to hold the wielder's
    /// Speed at battle-open. 1 = strictly above (ties lose; +1 secures the opening turn) while
    /// keeping the post-turn refill rate slow enough for the 33 ms poll to safely revert before
    /// a second turn is granted (flat 99 makes the refill race unwinnable at ~30 ms/refill).</summary>
    public const int IaiSpeedMargin = 1;

    /// <summary>Iai: upper sane bound for Speed reads and write targets. Reads above this from the
    /// field-max scan are discarded (one garbage-high read cannot pin the wielder to the clamp).
    /// Write targets are clamped to 1..IaiSpeedSaneMax before every guarded W8 call.</summary>
    public const int IaiSpeedSaneMax = 99;

    /// <summary>Iai (Ame-no-Murakumo +3): wall-clock safety cap on the opening-turn Speed hold.
    /// Backstops the pointer-based release (Iai.Policy.ReleaseSignal, rebuilt 2026-07-01): the
    /// stale-equal+wait-only double-corner (neither an arrival nor an acted-edge ever fires) and
    /// a twin-address mismatch (Wielder.Locate resolved a frozen (0,0) copy) both leave the
    /// pointer never matching the wielder's entry -- this cap guarantees the hold terminates
    /// anyway rather than pinning the wielder fastest for the whole battle.</summary>
    public const double IaiHoldCapSeconds = 90.0;

    /// <summary>Afterimage (Swiftedge +3): flat Speed gained per completed wielder turn while the
    /// ramp is intact. Swiftedge's damage is Speed x WP (formula 99), so each stack is +1xWP damage;
    /// a legible flat number beats a percentage on a card.</summary>
    public const int AfterimageSpeedPerTurn = 1;

    /// <summary>Afterimage: the most stacks the ramp can hold (turns' worth). Caps the Speed swing at
    /// AfterimageSpeedPerTurn x this -- 5 keeps a fully-ramped Swiftedge fast but not unbounded.</summary>
    public const int AfterimageSpeedCap = 5;

    /// <summary>Larceny (Arcanum +3): how many of the WIELDER's OWN completed turns a stolen buff is
    /// worn before it fades -- counted by TurnTracker.Turns for the wielder's fingerprint (the proven
    /// acted-edge per-unit counter). The GLOBAL-turn clock this replaced did not expire the buff in a
    /// normal fight (haste hung on, 2026-06-16); a deployed wielder always takes turns, so no wall-clock
    /// backstop is needed. 3 matches the card text ("wear 3 turns"). THE live-tune knob.</summary>
    public const int LarcenyHoldTurns = 3;

    /// <summary>Puppeteer (Galewind +3): after a puppet is applied, the wielder cannot dominate another
    /// enemy until this many GLOBAL turns (any unit's turn -- TurnTracker.GlobalTurns) have passed since
    /// the dominate. At 4: the dominate turn, then 3 turns where it cannot fire, then it re-arms on the
    /// 4th turn. The anti-snowball cap. (Global turns, NOT the wielder's own turns -- the acting
    /// fingerprint flickered to the puppet, so a wielder-keyed cooldown ran backwards.)</summary>
    public const int PuppeteerCooldownTurns = 4;

    /// <summary>Puppeteer (Galewind +3): fallback release clock when the acting wielder could not be
    /// fingerprinted at dominate time (LastActorFingerprint was default). In that rare case the
    /// wielder-turn clock has no fp to ride, so the possession expires after this many GLOBAL turns
    /// (~one full round). Bounds the possession safely without a per-unit clock.</summary>
    public const int PuppeteerWielderlessFallbackTurns = 12;

    /// <summary>Caster gear grows Magick Attack instead of Physical (a mage kills with spells).</summary>
    public static bool IsCaster(string category) => category == "Rod" || category == "Staff";

    // ── Toasts ────────────────────────────────────────────────────────

    /// <summary>Compiled BannerToasts default, now the SOLE source since LW-52 removed the launcher
    /// toggle: Mod.cs no longer passes a config value, so Engine's ctor falls back to this constant.
    /// Default ON: the tier-up callout toast (BannerToast.cs) is the headline feature, always on.</summary>
    public const bool BannerToasts = true;

    /// <summary>Ultima (Materia Blade): tier (row 0..3) Ã— HP% band (col 100 / 75-99 / 50-74 / 25-49 /
    /// &lt;25) -> PA multiplier PERCENT. round(naturalPA Ã— pct/100) is the held PA. Always-on (every
    /// tier); the kill tier only RAISES the whole curve so a +3 blade isn't a death trap when hurt.
    /// Faithful to FF7's Ultima Weapon: damage swells with the wielder's current HP.</summary>
    public static readonly int[][] UltimaMul =
    {
        new[] { 115, 110, 80, 70, 50 },  // +0  (0-4 kills)
        new[] { 120, 113, 83, 73, 53 },  // +1  (5-9)
        new[] { 125, 116, 86, 76, 56 },  // +2  (10-14)
        new[] { 130, 120, 90, 80, 60 },  // +3  (15+)
    };

    /// <summary>Missing-HP formulas ignore every stat -> no growth lever.</summary>
    public static bool SkipFormula(int formula) => formula == 67 || formula == 69;

    /// <summary>Speed-scaling weapons (Swiftfang / Swiftedge).</summary>
    public static bool IsSpeedFormula(int formula) => formula == 99;

    /// <summary>Magic-cast weapons (magic guns) scale off Magick Attack.</summary>
    public static bool IsMagicCastFormula(int formula) => formula == 4;

    /// <summary>Plague (Venombolt +3): engine deals mhp/8 per-poison-tick; the runtime adds
    /// mhp*<see cref="PlagueExtraDamageNum"/>/<see cref="PlagueExtraDamageDen"/> on each
    /// victim turn, making the effective rate 1.75x (= 1 + 3/4 â‰ˆ 7/8 + 3/32*7). Floored at 1
    /// so the augment never lands the kill.</summary>
    public const int PlagueExtraDamageNum = 3;
    public const int PlagueExtraDamageDen = 32;

    /// <summary>Poison timer initial value written by the engine on application.
    /// The runtime re-stamps this whenever the timer reads below it, defeating natural expiry
    /// and cures. Proven live (memory poison-status-bytes): held through a two-healer battle.</summary>
    public const byte PoisonTimerInit = 36;

    /// <summary>Sanctuary (Staff of the Magi +3): the value held in the crystal counter (band -0x15
    /// / combat +0x07) while the bearer is alive -- keeps fallen allies permanently revivable.</summary>
    public const byte SanctuaryHearts = 3;

    /// <summary>Bulwark (Sunderer +3, docs/BULWARK_AC.md A7): consecutive ticks the wielder may
    /// fail to resolve (Wielder.ResolveDeployedMainHand returning 0 -- benched, or the LW-136
    /// two-deployed refusal) before an active plant releases on its own. A single missed tick
    /// alone must never tear down a genuine hold.</summary>
    public const int BulwarkUnresolvedTicks = 10;

    /// <summary>Bulwark (Sunderer +3): the BannerToast dedupe/event key reserved for the plant
    /// announcement. BannerToast's EVENT-KEY CONVENTION (see BannerToast.cs) already spends the
    /// tier slot two ways -- real tier crossings use 1..3, Weapon Chronicle milestones use the
    /// NEGATED milestone -- so a plant toast needs its own key outside both ranges. 4 is free:
    /// tiers 1..3 and every milestone (negative) are already taken.</summary>
    public const int BulwarkToastKey = 4;

    /// <summary>Choir (Warlock's Staff +3): the support ability OR-set on adjacent allies so their magick casts instantly -- 227 = Non-charge (ability.en key 483), live-proven calc-gated. Swiftspell (226, half-charge) is the milder alt.</summary>
    public const int InstantCastSupportId = 227;

    /// <summary>Chain Lightning (Stormarc +3): maximum units the bolt arcs through after the
    /// primary hit. Each hop re-centers on the struck unit and picks the nearest unhit enemy.</summary>
    public const int RicochetMaxHops = 3;

    /// <summary>Chain Lightning (Stormarc +3): each hop deals this percent of the PREVIOUS
    /// hop's chip damage. Applies after the base ricochetPct, so damage decays each arc.</summary>
    public const int RicochetHopDecayPct = 60;

    /// <summary>Mushin (Kiku-ichimonji +3): the PA-factor bonus added on top of the wielder's tier
    /// factor for the one charged hit after a full wait turn (no move, no act, judged by
    /// MushinPolicy.ShouldArm on the literal PSX turn-flag design, LW-4 round 5). One charge =
    /// round(natural*2.05) PA at tier 3, about 1.6x a normal +3 swing, for one whole forgone turn:
    /// the per-charge rate stays the same investment-mechanic bias as <see cref="Factor"/> (easier
    /// to buff up than claw back an earned mechanic).</summary>
    public const double MushinBonus = 0.75;

    /// <summary>Mushin (Kiku-ichimonji +3): the charge is a single bool (0 or 1), never higher:
    /// one full wait, one charged hit, matching the card's literal text. Named MaxStacks (not
    /// "Cap") because MushinPolicy.PaHeld stays generic over an arbitrary stack count; only the
    /// runtime caller (GrowthEngine.Mushin.cs) ever passes 1.</summary>
    public const int MushinMaxStacks = 1;

    /// <summary>Mushin (Kiku-ichimonji +3): the kill tier at which the full-wait charge unlocks.
    /// Matches every other Living Weapon capstone signature (Iai, Kobu, ...), the tier-3 payoff.</summary>
    public const int MushinAtTier = 3;

    /// <summary>Kobu (Kiyomori +3): ceiling for the wielder's current brave (band +0x0F).
    /// 97 keeps it below the engine's Pray/Steel hard cap (100) while still out-braving
    /// nearly any foe -- a unit at 98-100 is unbeatably brave but Kobu won't fully match
    /// it (acceptable: the blade is never fully "bought" by one ultra-brave target).</summary>
    public const int KobuBraveCap = 97;

    // -- Provoke hold (LW-123 arc 2a): docs/PROVOKE_AC.md / the arc 2a plan's "Locked design
    //    decisions". Arc 1 (Provoke.cs) plants the mark; this knob set tunes the hold that reads it. --

    /// <summary>THE SHIP SWITCH for the whole Provoke feature (LW-133). False = the hold is wholly
    /// inert: it never arms, hides nobody, and performs no write at all, not even the every-tick
    /// player-side mark scrub. True = armed, which is what LW-123's acceptance pass needs.
    ///
    /// History, because the switch exists for a reason that has not gone away: 2.3.2 was a
    /// game-compatibility release and shipped this false, since nobody had played Provoke and
    /// docs/PROVOKE_AC.md had never been run. Deleting the Defender's items.json signature block is
    /// what stops the COMMAND being granted, but it does NOT stop this hold, which deliberately
    /// gates on the MARK BIT rather than on meta[33].Signature (arc 2b's data plumbing was pending
    /// when it was written). So the data edit alone leaves the hold live, and any enemy found
    /// carrying status id 0 would hide the party for a player whose Defender reached tier 3 -- and
    /// tier is earned from KILLS, independently of the signature block. That rests on "vanilla
    /// never sets status id 0", still an UNVERIFIED premise: the cast-nothing control battle that
    /// would settle it (docs/PROVOKE_AC.md) has not been run. Until it has, any release that means
    /// to carry Provoke to players is resting on that premise, and any release that does not must
    /// set this false again rather than relying on the data edit alone.
    ///
    /// Flipping it is a deliberate three-part edit either way, not a flag flip: this switch, the
    /// id 33 signature block in data/items.json (re-bake meta after), and tools/patch_names.py so
    /// the equip card matches what the weapon actually grants. ProvokeHoldTests pins the value AND
    /// pins this switch against the baked signature block, so the code and data halves cannot
    /// drift apart; tools/audit_nxd_bakes.py covers the card text.</summary>
    public const bool ProvokeEnabled = true;

    /// <summary>LW-127 (D1 revised, branch 4): how much further off the leading enemy's ETA must be
    /// than the marked enemy's own ETA before the hold reveals -- "clearly further away", not a photo
    /// finish, so a one-position ordering error in TurnOrder's ranking still leaves the party hidden.
    /// A const like every Tuning knob below, so changing it is a rebuild, never a runtime toggle.</summary>
    public const int ProvokeRevealMarginTicks = 2;

    /// <summary>LW-127: the master switch for the CT+Speed turn-order lookahead (TurnOrder.cs). True
    /// runs the full five-branch rule (D1 revised, ProvokeHold.Policy.ActionFor); false skips
    /// branches 3/4 entirely -- TurnOrder is never consulted, so ActionFor always falls to its
    /// branch 5 default (hide unless a player-side seat owns the turn or the marked enemy is the
    /// current actor), which is the same fallback shape the old WINDOW mode shipped. A rollback
    /// lever, not a player option: if the live pass finds the ranking model unreliable, flipping this
    /// off restores the known fallback without reverting the whole feature. A const, so flipping it
    /// is a rebuild, not a runtime toggle.</summary>
    public const bool ProvokeLookahead = true;

    /// <summary>Provoke hold: the marked enemy's own completed turns (the falling edge of the
    /// actor-pointer identity match gated on an enemy turn, decision 10 -- see
    /// ProvokeHold.MarkedIsActor; never the TurnQueue stat-fingerprint, which collides on identical
    /// enemies) before the hold releases as EnemyTurnDone. AC 1b's target is 3; v1 ships 1.</summary>
    public const int ProvokeTurns = 1;

    /// <summary>Provoke hold: LIVE battle-time safety cap (decision 11 -- accrued only on ticks where
    /// Offsets.PauseFlag reads unpaused, so a long menu/deliberation never burns down the clock) before
    /// an armed-but-stuck hold force-releases as a Watchdog.
    ///
    /// RAISED 30 -> 90 on 2026-07-27, measured rather than guessed. The owner's live pass armed the
    /// hold at 04:30:40 and the marked enemy did not take its turn until roughly 04:31:11, 31
    /// seconds later, because a shout lands during YOUR turn and the goaded enemy can sit most of a
    /// round away in the queue. Under the old cap that healthy hold would have force-released about
    /// a second before the enemy acted, and logged the WARN that means "a release condition was
    /// missed" -- a permanent false bug report on a working feature. This cap exists for a hold that
    /// is genuinely stuck (the Invisible bit never expires on its own), not for one that is merely
    /// waiting, and every real release path (turn done, death, disable, bearer loss, battle end)
    /// fires long before it. Raising ProvokeTurns above 1 would need this raised again, since the
    /// clock accrues across the WHOLE hold, not per turn.</summary>
    public const double ProvokeWatchdogSeconds = 90.0;

    /// <summary>Provoke hold: consecutive ticks the marked enemy may fail to re-locate (a transient
    /// band-scan miss -- an unreadable frame for a tick, say) before the hold concludes it is genuinely
    /// gone (EnemyGone) and releases. One missed tick alone must never release the hold.</summary>
    public const int ProvokeMarkedMissTicks = 3;

    // -- Living Poach (LW-167): docs plan v2's "Locked design". ARMED at stage 4 (2026-08-12):
    //    Engine now wires wasBasicAttack to LivingPoach.ReadWasBasicAttack, the real per-credit
    //    action-record discriminator, so every gate below is live in production. --

    /// <summary>LW-167 Living Poach: damage formulas the game's own vanilla Poach support (Key
    /// 471, live id 215) is DORMANT (owner-observed formula matrix, LW-166) through -- a weapon
    /// shipping on any of these can never let VANILLA poach it, so this is exactly the set the
    /// runtime feature is allowed to arm on
    /// (owner-observed live 2026-08-12, LW-166's formula matrix; the keystone double-fire guard --
    /// see LivingPoachPolicy.Decide's doc). MUST equal tools/lib/flavor.py's own DORMANT_FORMULAS:
    /// analyze.py's lockstep gate (check_dormant_poach_formulas_lockstep) pins the two together so
    /// this Python/C# pair can never drift apart.</summary>
    internal static readonly int[] DormantPoachFormulas = { 45, 46, 47, 48, 67, 69, 99 };

    /// <summary>True when <paramref name="formula"/> is one of <see cref="DormantPoachFormulas"/>
    /// -- the keystone gate LivingPoach checks before ever considering a poach.</summary>
    internal static bool IsDormantPoachFormula(int formula) => Array.IndexOf(DormantPoachFormulas, formula) >= 0;

    /// <summary>LW-167 Living Poach: the BannerToast dedupe/event key reserved for a successful
    /// poach's carcass announcement. Outside every other reserved key: tiers 1..3, Bulwark's 4
    /// (<see cref="BulwarkToastKey"/>), and every negated Weapon Chronicle milestone.</summary>
    internal const int PoachToastKey = 5;

    /// <summary>LW-167 Living Poach: the killer's Poach SUPPORT ability id (live id 215 = Key 471
    /// - 256, owner-observed live 2026-08-12) -- fed through Signatures.SupportBit the same way
    /// Choir's InstantCastSupportId is, to find the (byteOffset, mask) in the 4-byte support
    /// bitfield (Offsets.CSupport / Offsets.ASupport).</summary>
    internal const int PoachSupportAbilityId = 215;

    /// <summary>LW-167 stage 4: the Offsets.ArecAbil value meaning "the killer's action record
    /// names the basic Attack command, not an ability" -- LIVE_LEDGER's "The basic-Attack
    /// discriminator (LW-167 stage 4)" row (2026-08-12, owner probe tools/probes/arec_watch.py):
    /// a kind==5 (performing) stamp with abil==0 held from before the fatal blow through the
    /// credit moment on every observed basic-Attack kill, while an ability kill (Rend Weapon,
    /// id 141) held its own ability id the whole way. See LivingPoach.ReadWasBasicAttack.</summary>
    internal const int BasicAttackAbilityId = 0;

    /// <summary>Provoke hold (R2, owner round-2 feedback): status ids that mean the provoked enemy can
    /// no longer carry out its provoked turn, so the hold releases rather than linger to the watchdog.
    /// Petrify=8, Confuse=11, Stop=30, Charm=34, Sleep=35, DontAct=37 -- Death is already EnemyDead;
    /// Frog/Berserk/Blind/Slow/DontMove/DeathSentence are deliberately EXCLUDED because the disabled
    /// unit still attacks the only visible target (the bearer), so the provoke still lands. Read on the
    /// COMPOSED layer (StatusApply.Composed + StatusByte/StatusMask(id)).</summary>
    public static readonly int[] ProvokeDisablingStatusIds = { 8, 11, 30, 34, 35, 37 };
}
