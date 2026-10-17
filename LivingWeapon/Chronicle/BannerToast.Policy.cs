using System;

namespace LivingWeapon;

/// <summary>
/// Pure toast wording and tier-cross detection. The stateful driver is in BannerToast.cs.
/// </summary>
internal sealed partial class BannerToast
{
    /// <summary>English ordinal suffix, with the 11th-13th exception. Zero or negative gives "th"
    /// since the tally is player-editable JSON.</summary>
    public static string OrdinalSuffix(int n)
    {
        if (n <= 0) return "th";
        int teensCheck = n % 100;
        if (teensCheck is >= 11 and <= 13) return "th";
        return (n % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
    }

    /// <summary>Toast wording. Tier 3 with a signature weapon announces the unlock; every other
    /// crossing announces the grown name (weapon name plus the trimmed Tuning.Suffix, so tier 1
    /// reads "grown to Ravager+"). The grown-to arm has no trailing "!" because "+!" read as
    /// punctuation soup (wording locked 2026-07-04, superseding 2026-07-02).</summary>
    public static string Payload(string name, int tier, int kills, string? sigLabel)
    {
        int t = Math.Clamp(tier, 1, 3);
        string suffix = OrdinalSuffix(kills);
        if (t == 3 && !string.IsNullOrEmpty(sigLabel))
            return $"{name} has gained its {kills}{suffix} kill and has unlocked {sigLabel}!";
        return $"{name} has gained its {kills}{suffix} kill and has grown to {name}{Tuning.Suffix[t].Trim()}";
    }

    /// <summary>Returns the highest new tier, so a tally jump that skips tiers (dev seeding,
    /// fast-forwarded battles) queues one toast. Returns 0 when newTier has not advanced.</summary>
    public static int CrossedTier(int prevTier, int newTier)
        => newTier > prevTier ? newTier : 0;

    /// <summary>Weapon Chronicle kill milestones, descending so the scan finds the highest
    /// crossed one first. 1 is first blood.</summary>
    private static readonly int[] Milestones = { 1000, 500, 250, 100, 1 };

    /// <summary>Returns the highest milestone crossed, so a jump announces only that one. First
    /// blood only fires from a true zero start. The construction prime baselines loaded tallies and
    /// the dev-seed floor (Tuning.DevKillSeed, 3) sits above 1. Returns 0 when none was crossed.</summary>
    public static int CrossedMilestone(int prevKills, int newKills)
    {
        foreach (int m in Milestones)
            if (prevKills < m && newKills >= m) return m;
        return 0;
    }

    /// <summary>Weapon Chronicle milestone wording (weapon-chronicle-design, 2026-07-02). The
    /// default arm is unreachable and exists as a fallback.</summary>
    public static string MilestonePayload(string name, int milestone) => milestone switch
    {
        1 => $"{name} draws first blood!",
        100 => $"{name} claims its 100th soul!",
        250 => $"{name} has felled 250 foes!",
        500 => $"500 souls rest upon {name}'s edge!",
        1000 => $"{name}, slayer of a thousand!",
        _ => $"{name} has felled {milestone} foes!",
    };

    /// <summary>Wording for a first-blood kill that also crosses a tier in one DetectCrossings
    /// pass, merged into one toast.</summary>
    public static string FirstBloodTierPayload(string name, int tier, string? sigLabel)
    {
        int t = Math.Clamp(tier, 1, 3);
        if (t == 3 && !string.IsNullOrEmpty(sigLabel))
            return $"{name} draws first blood and has unlocked {sigLabel}!";
        return $"{name} draws first blood and has grown to {name}{Tuning.Suffix[t].Trim()}";
    }

    /// <summary>Reliquary Phase 1 (docs/RELIQUARY_AC.md) Mark toast wording, enqueued by
    /// Reliquary.cs at key 1000 + (int)archetype.
    ///
    /// The tuple's `tier` slot doubles as the queue-dedupe key. Tiers own 1..3, Weapon Chronicle
    /// milestones own the negated milestone values (-1, -100, -250, -500, -1000), Reliquary Marks own
    /// 1000..1003 (Unknown, index 4, never earns a Mark), and Phase 2 Legends reserve 2000+.
    /// BannerToastTests.Event_key_space_is_pairwise_distinct guards against collisions.</summary>
    public static string MarkPayload(string name, string title) => $"{name} has earned its Mark: {title}!";
}
