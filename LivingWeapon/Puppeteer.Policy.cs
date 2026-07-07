namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Galewind's "Puppeteer" signature -- no memory access.
/// The stateful latch, agency-bit hold, CT-based turn count, cooldown, and restore live in Puppeteer.cs.
/// </summary>
internal sealed partial class Puppeteer
{
    // ---- target gate: who can be puppeted ----
    // ⚠ ALLOW-EVERYONE (2026-06-18, user request "I want to control everyone"): the job gate is OFF.
    // It earned its retirement live -- a real 828-HP enemy (a boss) read job 37 at combat +0x03, BELOW
    // the old GenericJobLo (74) floor, so the floor was refusing to puppet a genuine enemy. The job byte
    // is NOT a reliable unit filter on the IC build (it reads story/special ids well outside the
    // human/monster bands). The REAL filter is upstream in Puppeteer.cs's verdict chain: the "not-enemy"
    // verdict (EnemyFingerprintCache.Contains) restricts to enemies, and the fingerprint gates
    // (maxHp 1..1999, lvl 1..99, brave/faith 1..100, took damage > 0) already prove it's a fielded
    // combatant -- so any struck enemy is a valid puppet regardless of its job id. (ShouldLatch below
    // remains the pure hook for that enemy test.) The band helpers below (IsGenericHumanJob /
    // IsMonsterJob) are kept intact so a narrower gate can be reinstated in one line (see IsDominatable).

    /// <summary>Lowest generic-human job id (Squire) on the live IC job byte (roster +0x02 / combat +0x03).</summary>
    public const int GenericJobLo = 74;   // 0x4A

    /// <summary>Highest generic-human job id (95 = Onion Knight; 94 = Dark Knight, confirmed live in the party).</summary>
    public const int GenericJobHi = 95;   // 0x5F

    /// <summary>True when the job id is a generic human class.</summary>
    public static bool IsGenericHumanJob(int jobId) => jobId >= GenericJobLo && jobId <= GenericJobHi;

    /// <summary>First monster-class job id (just above the human band).</summary>
    public const int MonsterJobLo = 96;    // 0x60

    /// <summary>Top of the monster band -- excludes 0x91 Construct 8 and the 0xA0+ named-character jobs.</summary>
    public const int MonsterJobHi = 144;   // 0x90

    /// <summary>True when the job id is a monster class. ⚠ Currently includes boss-monsters / Lucavi (see
    /// the class TODO) until the IC Lucavi job list is mapped.</summary>
    public static bool IsMonsterJob(int jobId) => jobId >= MonsterJobLo && jobId <= MonsterJobHi;

    /// <summary>The Puppeteer target gate. ⚠ ALLOW-EVERYONE: every struck enemy is dominatable -- the
    /// job id is not consulted (it reads unreliable story/special ids on the IC build; a real boss read
    /// 37, below the old 74 floor). The enemy + fingerprint gates in Puppeteer.cs are the real filter.
    /// To reinstate a narrower gate, swap the body to e.g. <c>IsGenericHumanJob(jobId) ||
    /// IsMonsterJob(jobId)</c> (the <paramref name="jobId"/> is still passed in for that purpose).</summary>
    public static bool IsDominatable(int jobId) => true;

    // ---- activation + latch + cooldown decisions ----

    /// <summary>True when Puppeteer is configured (PuppeteerTurns set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.PuppeteerTurns > 0;

    /// <summary>D1: the wielder-acting gate is the OR of two independent signals. <paramref
    /// name="pointerMatch"/> is the engine's own actor pointer naming the wielder's OWN band seat
    /// (Band.ActorEntry == the deployed main-hand wielder's entry, Wielder.ResolveDeployedMainHand) --
    /// it does NOT require <paramref name="actedFlag"/>: the pointer itself IS the engine's own
    /// "whose turn is it" signal (the same precedent TurnTracker.TryActiveViaPointer and Iai's release
    /// detection both rely on). <paramref name="latchMainHand"/> + <paramref name="actedFlag"/> is
    /// today's mechanism, preserved verbatim as the fallback for a benched pointer, a two-wielder
    /// ambiguity (ResolveDeployedMainHand returns 0), or an invalid pointer. Strictly widens the old
    /// latch-only gate -- no shared latch consumer (Signatures.IsActingMainHand) is narrowed.</summary>
    public static bool WielderActing(bool pointerMatch, bool latchMainHand, bool actedFlag)
        => pointerMatch || (latchMainHand && actedFlag);

    /// <summary>D1 REVISED (2026-07-04): identity-based pointer match, no memory access. Plain address
    /// equality between Band.ActorEntry and Wielder.ResolveDeployedMainHand's entry was LIVE-
    /// FALSIFIED (2026-07-04 gate log: pointerMatch=False while TurnTracker attributed the same
    /// actor-ptr in the same window) -- the revolving band MIRROR seat means one unit legitimately
    /// exists at multiple band addresses, so the two resolvers can each return a DIFFERENT copy of
    /// the SAME unit and never compare equal by address. NameId identity via the frame back-reference
    /// (Offsets.ANameId, mirroring the roster nameId Offsets.RNameId -- the same mirror-safe bridge
    /// Iai's release and Wielder's tier-1 locate already use) is authoritative whenever the wielder's
    /// OWN roster nameId resolved (<paramref name="wielderNameId"/> &gt; 0): compare <paramref
    /// name="actorNameId"/> against it instead of the two addresses. Address equality remains ONLY as
    /// the fallback for the nameId-unavailable case (an unseeded roster nameId, wielderNameId &lt;= 0)
    /// -- the pre-2026-07-04 behavior, unchanged for every caller that never seeded one.</summary>
    public static bool PointerNamesWielder(long actorEntry, long wielderEntry, int actorNameId, int wielderNameId)
        => actorEntry != 0 && wielderEntry != 0
           && (wielderNameId > 0 ? actorNameId == wielderNameId : actorEntry == wielderEntry);

    /// <summary>True when the struck unit is an enemy (never puppet an ally).</summary>
    public static bool ShouldLatch(bool isEnemy) => isEnemy;

    /// <summary>True when the wielder may apply a NEW puppet: at least <paramref name="cooldownTurns"/>
    /// of the wielder's own turns have elapsed since the last puppet. The current &lt; last guard handles
    /// a battle restart (the wielder turn counter reset below the stored stamp) -- treat as off cooldown.</summary>
    public static bool OffCooldown(int currentWielderTurn, int lastPuppetWielderTurn, int cooldownTurns)
        => currentWielderTurn < lastPuppetWielderTurn
           || currentWielderTurn - lastPuppetWielderTurn >= cooldownTurns;

    // ---- the agency flag (the runtime hold target) ----
    // Combat base +0x05, bit 0x08: SET = human (the action menu opens), CLEAR = AI. PROVEN live
    // 2026-06-18 (Ramza read 0x0B with the bit set; enemies read 0x50 clear; setting it on an enemy
    // opened the player menu for it). Band-relative: the band entry sits at combat base +0x1C, so
    // combat +0x05 == band -0x17 (cf. Offsets.ACrystalHearts -0x15 == combat +0x07).
    internal const int AgencyOff = -0x17;
    internal const byte AgencyBit = 0x08;

    // The victim's job id (for the log line + the re-gating hook): combat base +0x03 (PSX "Current
    // Job"), band-relative == band -0x19. CONFIRMED live 2026-06-18 (Ramza 83 Thief, party Dark Knight
    // 94, enemies Goblin 99 / Bonesnatch 110 / Wisenkin 124-127) -- but the byte is not a reliable unit
    // FILTER (bosses read story/special ids like 37), so IsDominatable ignores it (see above).
    internal const int JobOff = -0x19;

    /// <summary>Guarded single-bit RMW of the agency flag at <paramref name="bandAddr"/> + AgencyOff:
    /// OR-set (on) or AND-clear (off) ONLY bit 0x08, leaving the rest of the byte intact (its other bits
    /// are engine state). VirtualQuery-guarded -- a non-writable address is a no-op. The per-tick hold
    /// primitive (mirrors CharmLock.Force).</summary>
    public static void SetAgency(IGameMemory mem, long bandAddr, bool on)
    {
        long a = bandAddr + AgencyOff;
        if (!mem.Writable(a, 1)) return;
        int cur = mem.U8(a);
        int want = on ? (cur | AgencyBit) : (cur & ~AgencyBit);
        if (cur != want) mem.W8(a, (byte)want);
    }
}

/// <summary>Per-battle state for Puppeteer: exactly ONE active puppet at a time (the latch) plus the
/// battle-turn stamp of the last puppet (the cooldown clock). Mirrors CharmLock's single-lock shape,
/// not Maim's dictionary -- only one enemy is dominated at a time. No memory access.
///
/// RELEASE: the puppet is held until it takes its OWN turn, detected LIVE in Puppeteer.Hold.cs by
/// the turn queue naming the puppet at an acted turn boundary (the LW-5 round-2/3 tape, 2026-07-07:
/// the shipped wielder-clock released on the wrong unit's turn, early or late, because LW-7 dumps
/// turn credit onto the wielder). This state only holds the CAP: release no later than
/// PuppeteerWielderlessFallbackTurns GlobalTurns after dominate (IsCapped), the backstop in case the
/// queue signal never fires live.
/// </summary>
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

    /// <summary>True when a NEW puppet may be applied: no puppet is active AND the cooldown has elapsed
    /// (or none has ever been applied this battle).</summary>
    public bool CanPuppet(int currentWielderTurn, int cooldownTurns)
        => !HasPuppet
           && (_lastPuppetWielderTurn is not int last
               || Puppeteer.OffCooldown(currentWielderTurn, last, cooldownTurns));

    /// <summary>Dominate a new victim. Stamps the GlobalTurns baseline (for the release CAP) and the
    /// cooldown clock (also GlobalTurns).</summary>
    public void Puppet(long addr, (int mhp, int lvl, int br, int fa) fp, int currentWielderTurn,
                       int globalBaseline)
    {
        _puppet = new PuppeteerEntry(addr, fp, GlobalBaseline: globalBaseline);
        _lastPuppetWielderTurn = currentWielderTurn;
    }

    /// <summary>The LW-5 safety CAP: true once GlobalTurns has advanced <paramref name="capTurns"/>
    /// past the dominate baseline. The sole time-based release, a backstop for the case where the
    /// live turn-queue "own turn" signal never fires (Puppeteer.Hold.cs owns the normal release).
    /// Bounds a puppet to at most capTurns global turns, never to battle exit.</summary>
    public bool IsCapped(int globalTurnsNow, int capTurns)
        => _puppet is { } p && globalTurnsNow - p.GlobalBaseline >= capTurns;

    /// <summary>Release the active puppet (after the agency bit is cleared). The cooldown clock stays
    /// set, so a new puppet is blocked until the cooldown elapses.</summary>
    public void Release() => _puppet = null;

    /// <summary>Battle exit: clear the puppet AND the cooldown clock.</summary>
    public void Clear() { _puppet = null; _lastPuppetWielderTurn = null; }

    private readonly record struct PuppeteerEntry(
        long Addr, (int mhp, int lvl, int br, int fa) Fp, int GlobalBaseline);
}
