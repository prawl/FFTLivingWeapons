namespace LivingWeapon;

/// <summary>
/// Decisions behind Galewind's Puppeteer, plus its one guarded agency flag write (SetAgency).
/// State, latch, agency hold, turn count, cooldown and restore live in Puppeteer.cs.
/// </summary>
internal sealed partial class Puppeteer
{
    // ---- target gate. Who can be puppeted ----
    // Any struck enemy can be puppeted, since the job byte is unreliable on the IC build (a real 828 HP
    // boss read job 37 at combat +0x03, below the GenericJobLo (74) floor). The enemy check
    // (EnemyFingerprintCache.Contains) and the fingerprint gates in Puppeteer.cs do the filtering.
    // The band helpers below stay so a narrower gate can return in IsDominatable.

    /// <summary>Lowest generic-human job id (Squire) on the live IC job byte (roster +0x02 / combat +0x03).</summary>
    public const int GenericJobLo = 74;   // 0x4A

    /// <summary>Highest generic-human job id (93 = Mime). The Job sheet puts the Chocobo at 94 and the Black Chocobo at 95.</summary>
    public const int GenericJobHi = 93;   // 0x5D

    /// <summary>True when the job id is a generic human class.</summary>
    public static bool IsGenericHumanJob(int jobId) => jobId >= GenericJobLo && jobId <= GenericJobHi;

    /// <summary>First monster-class job id (94 = Chocobo, just above the human band).</summary>
    public const int MonsterJobLo = 94;    // 0x5E

    /// <summary>Top of the monster band, excludes 0x91 Construct 8 and the 0xA0+ named-character jobs.</summary>
    public const int MonsterJobHi = 144;   // 0x90

    /// <summary>True when the job id is a monster class. The band (94-144) excludes 0x91 Construct 8
    /// and the 0xA0+ named-character jobs; the IC Lucavi job list was never mapped.</summary>
    public static bool IsMonsterJob(int jobId) => jobId >= MonsterJobLo && jobId <= MonsterJobHi;

    /// <summary>Every struck enemy can be puppeted, whatever its job (a boss read 37, below the 74 floor).
    /// To narrow it, use <c>IsGenericHumanJob(jobId) || IsMonsterJob(jobId)</c>; jobId is still passed.</summary>
    public static bool IsDominatable(int jobId) => true;

    // ---- activation + latch + cooldown decisions ----

    /// <summary>True when Puppeteer is configured (PuppeteerTurns set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.PuppeteerTurns > 0;

    /// <summary>Tells whether the wielder is acting right now. True when the actor pointer names the wielder
    /// (<paramref name="pointerMatch"/>), or on the backup latchMainHand + actedFlag if the pointer is unusable.</summary>
    public static bool WielderActing(bool pointerMatch, bool latchMainHand, bool actedFlag)
        => pointerMatch || (latchMainHand && actedFlag);

    /// <summary>For about three seconds after the wielder's turn signal blinks off, a hit still counts, so a
    /// quick confirm lands. 90 ticks of 33 ms, six times the half second gap seen live.</summary>
    // Counts ticks, not turns. The global turn counter bumps on the acted rising edge, which is the strike
    // itself, so a turn based hold would drop at the exact moment it exists to cover.
    internal const int ArmHoldTicks = 90;

    /// <summary>True when the tier is earned and the wielder is acting now or was within
    /// <see cref="ArmHoldTicks"/> ticks ago.</summary>
    internal static bool ArmedNow(bool liveActive, int ticksSinceLiveActive, bool tierOk)
        => tierOk && (liveActive || ticksSinceLiveActive <= ArmHoldTicks);

    /// <summary>Tells whether the actor pointer is the wielder, by identity not address (one unit can sit at several
    /// addresses). Compares NameId when <paramref name="wielderNameId"/> &gt; 0, else address equality.</summary>
    public static bool PointerNamesWielder(long actorEntry, long wielderEntry, int actorNameId, int wielderNameId)
        => actorEntry != 0 && wielderEntry != 0
           && (wielderNameId > 0 ? actorNameId == wielderNameId : actorEntry == wielderEntry);

    /// <summary>True when the struck unit is an enemy (never puppet an ally).</summary>
    public static bool ShouldLatch(bool isEnemy) => isEnemy;

    /// <summary>Tells whether the wielder may start a NEW puppet, once <paramref name="cooldownTurns"/> turns have passed.
    /// A current &lt; last stamp means a battle restart reset the counter, so treat it as off cooldown.</summary>
    public static bool OffCooldown(int currentWielderTurn, int lastPuppetWielderTurn, int cooldownTurns)
        => currentWielderTurn < lastPuppetWielderTurn
           || currentWielderTurn - lastPuppetWielderTurn >= cooldownTurns;

    // ---- the agency flag (the runtime hold target) ----
    // The flag that says who steers a unit. Combat +0x05, bit 0x08 set means human (action menu opens), clear means AI.
    // Ramza reads 0x0B, enemies 0x50, and setting it on an enemy opened the player menu.
    // The band entry sits at combat +0x1C, so combat +0x05 is band -0x17 (cf. Offsets.ACrystalHearts).
    internal const int AgencyOff = -0x17;
    internal const byte AgencyBit = 0x08;

    // The victim's job id, for the log line and re-gating. Promoted to Offsets.AJob;
    // this alias keeps Puppeteer.cs:227 and existing tests compiling. See Offsets.AJob for provenance.
    internal const int JobOff = Offsets.AJob;

    /// <summary>Flips the flag that decides who steers the enemy, the per tick hold. Guarded single bit RMW at
    /// <paramref name="bandAddr"/> + AgencyOff, only bit 0x08 (the rest is engine state). An unwritable address is a no op.</summary>
    public static void SetAgency(IGameMemory mem, long bandAddr, bool on)
    {
        long a = bandAddr + AgencyOff;
        if (!mem.Writable(a, 1)) return;
        int cur = mem.U8(a);
        int want = on ? (cur | AgencyBit) : (cur & ~AgencyBit);
        if (cur != want) mem.W8(a, (byte)want);
    }
}

/// <summary>Per battle memory of who is puppeted and when the cooldown ends. One active puppet (the latch) plus
/// the turn stamp of the last puppet (the cooldown clock). No memory access. Release on the puppet's own turn
/// is detected in Puppeteer.Hold.cs, since a wielder clock would release on the wrong unit's turn when auto
/// battle dumps turn credit onto the wielder. This state holds only the cap, PuppeteerWielderlessFallbackTurns
/// GlobalTurns after dominate (IsCapped).</summary>
internal sealed class PuppeteerState
{
    private PuppeteerEntry? _puppet;          // the currently dominated victim, or null
    private int? _lastPuppetWielderTurn;      // GlobalTurns stamp at last latch (cooldown); null = none this battle

    /// <summary>True when an enemy is currently dominated.</summary>
    public bool HasPuppet => _puppet is not null;

    /// <summary>The active puppet's fingerprint, or null.</summary>
    public (int mhp, int lvl, int br, int fa)? Fingerprint => _puppet?.Fp;

    /// <summary>The active puppet's band address, or 0.</summary>
    public long Addr => _puppet?.Addr ?? 0;

    /// <summary>The battle-turn stamp of the last puppet, or null if none yet (the cooldown clock).</summary>
    public int? LastPuppetWielderTurn => _lastPuppetWielderTurn;

    /// <summary>True when a NEW puppet may be applied. No puppet is active AND the cooldown has elapsed
    /// (or none has ever been applied this battle).</summary>
    public bool CanPuppet(int currentWielderTurn, int cooldownTurns)
        => !HasPuppet
           && (_lastPuppetWielderTurn is not int last
               || Puppeteer.OffCooldown(currentWielderTurn, last, cooldownTurns));

    /// <summary>Dominate a new victim and stamp the release-cap baseline and the cooldown clock.</summary>
    public void Puppet(long addr, (int mhp, int lvl, int br, int fa) fp, int currentWielderTurn,
                       int globalBaseline)
    {
        _puppet = new PuppeteerEntry(addr, fp, GlobalBaseline: globalBaseline);
        _lastPuppetWielderTurn = currentWielderTurn;
    }

    /// <summary>The safety cap that lets a stuck puppet go. True once GlobalTurns advanced <paramref name="capTurns"/>
    /// past the dominate baseline, a backstop if the own turn signal never fires (Puppeteer.Hold.cs).</summary>
    public bool IsCapped(int globalTurnsNow, int capTurns)
        => _puppet is { } p && globalTurnsNow - p.GlobalBaseline >= capTurns;

    /// <summary>Release the active puppet (after the agency bit is cleared). The cooldown clock stays
    /// set, so a new puppet is blocked until the cooldown elapses.</summary>
    public void Release() => _puppet = null;

    /// <summary>Battle exit. Clear the puppet AND the cooldown clock.</summary>
    public void Clear() { _puppet = null; _lastPuppetWielderTurn = null; }

    private readonly record struct PuppeteerEntry(
        long Addr, (int mhp, int lvl, int br, int fa) Fp, int GlobalBaseline);
}
