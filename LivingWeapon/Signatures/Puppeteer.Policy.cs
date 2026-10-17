namespace LivingWeapon;

/// <summary>
/// Pure decisions behind Galewind's Puppeteer signature, no memory access.
/// State, latch, agency hold, turn count, cooldown and restore live in Puppeteer.cs.
/// </summary>
internal sealed partial class Puppeteer
{
    // ---- target gate. Who can be puppeted ----
    // The job gate is off (2026-06-18): a real 828-HP boss read job 37 at combat +0x03, below the
    // old GenericJobLo (74) floor, and the job byte is unreliable on the IC build. The enemy check
    // (EnemyFingerprintCache.Contains) and the fingerprint gates in Puppeteer.cs do the filtering.
    // The band helpers below stay so a narrower gate can return in IsDominatable.

    /// <summary>Lowest generic-human job id (Squire) on the live IC job byte (roster +0x02 / combat +0x03).</summary>
    public const int GenericJobLo = 74;   // 0x4A

    /// <summary>Highest generic-human job id (95 = Onion Knight; 94 = Dark Knight, confirmed live in the party).</summary>
    public const int GenericJobHi = 95;   // 0x5F

    /// <summary>True when the job id is a generic human class.</summary>
    public static bool IsGenericHumanJob(int jobId) => jobId >= GenericJobLo && jobId <= GenericJobHi;

    /// <summary>First monster-class job id (just above the human band).</summary>
    public const int MonsterJobLo = 96;    // 0x60

    /// <summary>Top of the monster band, excludes 0x91 Construct 8 and the 0xA0+ named-character jobs.</summary>
    public const int MonsterJobHi = 144;   // 0x90

    /// <summary>True when the job id is a monster class. The band (96-144) excludes 0x91 Construct 8
    /// and the 0xA0+ named-character jobs; the IC Lucavi job list was never mapped.</summary>
    public static bool IsMonsterJob(int jobId) => jobId >= MonsterJobLo && jobId <= MonsterJobHi;

    /// <summary>The Puppeteer target gate. Every struck enemy is dominatable and the job id is ignored
    /// (a boss read 37, below the old 74 floor). To narrow it, use <c>IsGenericHumanJob(jobId) ||
    /// IsMonsterJob(jobId)</c>; <paramref name="jobId"/> is still passed for that.</summary>
    public static bool IsDominatable(int jobId) => true;

    // ---- activation + latch + cooldown decisions ----

    /// <summary>True when Puppeteer is configured (PuppeteerTurns set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.PuppeteerTurns > 0;

    /// <summary>D1. The wielder is acting when the actor pointer names the wielder's band seat
    /// (<paramref name="pointerMatch"/>, same signal as TurnTracker.TryActiveViaPointer), or by the older
    /// <paramref name="latchMainHand"/> + <paramref name="actedFlag"/> fallback when the pointer is
    /// benched, ambiguous (ResolveDeployedMainHand returns 0) or invalid.</summary>
    public static bool WielderActing(bool pointerMatch, bool latchMainHand, bool actedFlag)
        => pointerMatch || (latchMainHand && actedFlag);

    /// <summary>D1 revised (2026-07-04): identity-based pointer match, no memory access. Address equality
    /// between Band.ActorEntry and Wielder.ResolveDeployedMainHand failed live (2026-07-04 gate log:
    /// pointerMatch=False while TurnTracker matched the same actor-ptr), because the band MIRROR seat
    /// puts one unit at several addresses. So compare NameId (Offsets.ANameId via the frame
    /// back-reference, as Iai's release and Wielder's tier-1 locate do) when
    /// <paramref name="wielderNameId"/> &gt; 0, and fall back to address equality when no roster
    /// nameId was seeded.</summary>
    public static bool PointerNamesWielder(long actorEntry, long wielderEntry, int actorNameId, int wielderNameId)
        => actorEntry != 0 && wielderEntry != 0
           && (wielderNameId > 0 ? actorNameId == wielderNameId : actorEntry == wielderEntry);

    /// <summary>True when the struck unit is an enemy (never puppet an ally).</summary>
    public static bool ShouldLatch(bool isEnemy) => isEnemy;

    /// <summary>True when the wielder may apply a NEW puppet. At least <paramref name="cooldownTurns"/>
    /// of the wielder's own turns have elapsed since the last puppet. The current &lt; last guard handles
    /// a battle restart (the wielder turn counter reset below the stored stamp), treat as off cooldown.</summary>
    public static bool OffCooldown(int currentWielderTurn, int lastPuppetWielderTurn, int cooldownTurns)
        => currentWielderTurn < lastPuppetWielderTurn
           || currentWielderTurn - lastPuppetWielderTurn >= cooldownTurns;

    // ---- the agency flag (the runtime hold target) ----
    // Agency flag. Combat +0x05, bit 0x08 set = human (action menu opens), clear = AI. Observed
    // 2026-06-18 (Ramza read 0x0B, enemies 0x50; setting it on an enemy opened the player menu).
    // The band entry sits at combat +0x1C, so combat +0x05 is band -0x17 (cf. Offsets.ACrystalHearts).
    internal const int AgencyOff = -0x17;
    internal const byte AgencyBit = 0x08;

    // The victim's job id, for the log line and re-gating. Promoted to Offsets.AJob;
    // this alias keeps Puppeteer.cs:227 and existing tests compiling. See Offsets.AJob for provenance.
    internal const int JobOff = Offsets.AJob;

    /// <summary>Guarded single-bit RMW of the agency flag at <paramref name="bandAddr"/> + AgencyOff:
    /// sets or clears only bit 0x08 (the rest is engine state). A non-writable address is a no-op.
    /// This is the per-tick hold.</summary>
    public static void SetAgency(IGameMemory mem, long bandAddr, bool on)
    {
        long a = bandAddr + AgencyOff;
        if (!mem.Writable(a, 1)) return;
        int cur = mem.U8(a);
        int want = on ? (cur | AgencyBit) : (cur & ~AgencyBit);
        if (cur != want) mem.W8(a, (byte)want);
    }
}

/// <summary>Per-battle state for Puppeteer. One active puppet (the latch) plus the turn stamp of the
/// last puppet (the cooldown clock). No memory access. Release on the puppet's own turn is detected in
/// Puppeteer.Hold.cs (2026-07-07 tapes; the old wielder clock released on the wrong
/// unit's turn because auto-battle dumps turn credit onto the wielder). This state holds only the cap,
/// PuppeteerWielderlessFallbackTurns GlobalTurns after dominate (IsCapped).</summary>
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

    /// <summary>The safety cap. True once GlobalTurns has advanced <paramref name="capTurns"/>
    /// past the dominate baseline. A backstop if the turn-queue "own turn" signal never fires
    /// (Puppeteer.Hold.cs handles the normal release).</summary>
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
