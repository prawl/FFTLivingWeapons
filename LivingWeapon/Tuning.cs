using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Living-Weapon growth tuning. Kept in one place so detection, growth, and display agree.
///
/// Kill thresholds are build-gated: a DEV build (BuildLinked.ps1 passes -p:LwDev=true, which
/// defines LWDEV) uses {1,2,3} so a weapon hits P3 in three kills, AND pre-seeds every weapon
/// to DevKillSeed (3 == P3) on load -- so every +3 signature is live the moment the weapon is
/// equipped. A PRODUCTION build (Publish.ps1, no flag) uses the real curve {5,25,50} and seeds nothing.
/// </summary>
internal static class Tuning
{
    /// <summary>Both threshold sets, ALWAYS compiled (so a test can reason about the dev curve even
    /// though tests compile under prod). The active one is selected by the LWDEV flag below.</summary>
    public static readonly int[] DevThresholds = { 1, 2, 3 };    // P3 by the third kill (fast verification)
    public static readonly int[] ProdThresholds = { 5, 25, 50 }; // escalating: a fast taste at P, an aspirational P3
#if LWDEV
    public static readonly int[] KillThresholds = DevThresholds;
    /// <summary>DEV: floor every known weapon to <see cref="DevKillSeed"/> kills on load.</summary>
    public const bool DevSeedAllKills = true;
    /// <summary>DEV: per-tick battle-event timeline (damage/heal/move) in the log.</summary>
    public const bool VerboseEvents = true;
    /// <summary>DEV pulse RETIRED 2026-06-10 after it verified the full write path on screen
    /// (band addressing + HP write + MP pair, watched live). Flip back to true only to re-run
    /// that experiment -- while true it force-heals every ~10s and drowns out the real trigger.
    /// External probes are Denuvo-walled, so the DLL remains the only instrument for it.</summary>
    public const bool FontDevPulse = false;
#else
    public static readonly int[] KillThresholds = ProdThresholds;
    /// <summary>Production seeds nothing -- the wielder earns every tier.</summary>
    public const bool DevSeedAllKills = false;
    /// <summary>Production logs stay lean: kills/turns/grants only, no per-tick events.</summary>
    public const bool VerboseEvents = false;
    /// <summary>Never in production.</summary>
    public const bool FontDevPulse = false;
#endif

    /// <summary>DEV seed floor: every weapon starts at least this many kills. 3 (== P3 under the dev
    /// thresholds) so every +3 signature is live the moment the weapon is equipped.</summary>
    public const int DevKillSeed = 3;

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

    /// <summary>kills -> tier (0..3) against the active thresholds, checked high to low.</summary>
    public static int TierFor(int kills) => TierForIn(kills, KillThresholds);

    /// <summary>The weapon's current tier straight off the shared kill tally (0 when untallied).
    /// The one lookup every signature module and the growth router key on.</summary>
    public static int TierOf(Dictionary<int, int> kills, int weaponId) =>
        TierFor(kills.TryGetValue(weaponId, out int k) ? k : 0);

    /// <summary>kills -> tier (0..3) against a given threshold set (lets tests check the dev curve).</summary>
    public static int TierForIn(int kills, int[] thresholds) =>
        kills >= thresholds[2] ? 3 : kills >= thresholds[1] ? 2 : kills >= thresholds[0] ? 1 : 0;

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

    /// <summary>Life Sap (Umbral Rod +3): fraction of the wielder's max HP restored when a kill
    /// is credited to the rod (clamped at full; never revives).</summary>
    public const double LifeSapPct = 0.25;

    /// <summary>Wyrmblood (Dragon Rod +3): each splash target mends its OWN maxHP / this divisor
    /// per wielder turn -- 8 == the vanilla Regen rate.</summary>
    public const int WyrmbloodDiv = 8;

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
    /// window lasts, counted off its live CT at band +0x25 (CharmLock's byte). PROVEN 2026-06-14: a
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
    /// skipped turn -- the bit-clear must out-last the engine's revive bookkeeping. 3s proven live
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

    // ── Treasure Master knobs ────────────────────────────────────────────────────

    /// <summary>Documented default for Config.TreasureAlwaysOn (the runtime value flows from
    /// LivingWeapon.Configuration.Config, loaded by Mod.cs at startup; this constant is the
    /// fallback when the config file can't be read).  Default OFF -- the Scholar's Ring
    /// (item id 260, RAccessory offset +0x12, probe-confirmed 2026-06-12) is the NORMAL gate:
    /// TreasureMaster arms each battle iff any roster slot's accessory reads 260 via RingGate.
    /// TreasureAlwaysOn=true is a force-on OVERRIDE that bypasses the ring check entirely
    /// (roster is not read).  Ship with the default false; only toggle for dev testing.</summary>
    public const bool TreasureAlwaysOn = false;

    /// <summary>Consecutive same-map-id ticks required before arming begins (~1s at 33ms).</summary>
    public const int TreasureArmStableTicks = 30;

    /// <summary>Ticks between full fingerprint revalidations while ARMED.</summary>
    public const int TreasureRevalidateEveryNTicks = 30;

    /// <summary>Maximum arming attempts before logging "waiting to arm" once per battle.
    /// Arming continues indefinitely after the cap -- the log is informational only.</summary>
    public const int TreasureArmAttemptCap = 60;

    /// <summary>Minimum number of Resting or Held ("ok") tile addresses required at arm time
    /// to proceed with arming. Below this quorum the module stays ARMING (cheap polling) until
    /// enough tiles scroll into view. Protects against a battle start where most tiles are
    /// off-screen (action camera / narrow view) without permanently disarming.</summary>
    public const int TreasureMinPlausibleAddrs = 4;

    /// <summary>Consecutive bad-map-id ticks while ARMED before a full state reset
    /// back to DISARMED. The map-id change IS the battle boundary for chained story battles
    /// (the debounced exit edge may never fire in those cases).</summary>
    public const int TreasureMapIdBadTicksToReset = 3;

    /// <summary>How many Tick() calls between dataset-stamp checks (applies regardless of
    /// phase or inLive). 30 ticks ≈ 1 s at the 33 ms loop. A changed stamp triggers a full
    /// reload + state reset so the next arm cycle uses fresh data.</summary>
    public const int TreasureStampCheckTicks = 30;

    /// <summary>FastHold re-stamp interval in ms (~2× per 60 fps animation frame ≈ 16 ms).
    /// Out-paces the running-water wipe that clears 0x80 between 33 ms loop re-stamps.</summary>
    public const int TreasureFastHoldMs = 8;

    /// <summary>Ultima (Materia Blade): tier (row 0..3) × HP% band (col 100 / 75-99 / 50-74 / 25-49 /
    /// &lt;25) -> PA multiplier PERCENT. round(naturalPA × pct/100) is the held PA. Always-on (every
    /// tier); the kill tier only RAISES the whole curve so a +3 blade isn't a death trap when hurt.
    /// Faithful to FF7's Ultima Weapon: damage swells with the wielder's current HP.</summary>
    public static readonly int[][] UltimaMul =
    {
        new[] { 115, 110, 80, 70, 50 },  // +0  (0-4 kills)
        new[] { 120, 113, 83, 73, 53 },  // +1  (5-24)
        new[] { 125, 116, 86, 76, 56 },  // +2  (25-49)
        new[] { 130, 120, 90, 80, 60 },  // +3  (50+)
    };

    /// <summary>Missing-HP formulas ignore every stat -> no growth lever.</summary>
    public static bool SkipFormula(int formula) => formula == 67 || formula == 69;

    /// <summary>Speed-scaling weapons (Swiftfang / Swiftedge).</summary>
    public static bool IsSpeedFormula(int formula) => formula == 99;

    /// <summary>Magic-cast weapons (magic guns) scale off Magick Attack.</summary>
    public static bool IsMagicCastFormula(int formula) => formula == 4;

    /// <summary>Plague (Venombolt +3): engine deals mhp/8 per-poison-tick; the runtime adds
    /// mhp*<see cref="PlagueExtraDamageNum"/>/<see cref="PlagueExtraDamageDen"/> on each
    /// victim turn, making the effective rate 1.75x (= 1 + 3/4 ≈ 7/8 + 3/32*7). Floored at 1
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

    /// <summary>Choir (Warlock's Staff +3): the support ability OR-set on adjacent allies so their magick casts instantly -- 227 = Non-charge (ability.en key 483), live-proven calc-gated. Swiftspell (226, half-charge) is the milder alt.</summary>
    public const int InstantCastSupportId = 227;

    /// <summary>Chain Lightning (Stormarc +3): maximum units the bolt arcs through after the
    /// primary hit. Each hop re-centers on the struck unit and picks the nearest unhit enemy.</summary>
    public const int RicochetMaxHops = 3;

    /// <summary>Chain Lightning (Stormarc +3): each hop deals this percent of the PREVIOUS
    /// hop's chip damage. Applies after the base ricochetPct, so damage decays each arc.</summary>
    public const int RicochetHopDecayPct = 60;

    /// <summary>Kobu (Kiyomori +3): ceiling for the wielder's current brave (band +0x0F).
    /// 97 keeps it below the engine's Pray/Steel hard cap (100) while still out-braving
    /// nearly any foe -- a unit at 98-100 is unbeatably brave but Kobu won't fully match
    /// it (acceptable: the blade is never fully "bought" by one ultra-brave target).</summary>
    public const int KobuBraveCap = 97;
}
