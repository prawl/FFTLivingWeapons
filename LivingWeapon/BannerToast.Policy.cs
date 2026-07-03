using System;

namespace LivingWeapon;

/// <summary>
/// Pure decisions behind the tier-up toast wording + tier-cross detection -- no memory access.
/// The stateful driver (prime, queue, poll, edge-detect) lives in BannerToast.cs.
/// </summary>
internal sealed partial class BannerToast
{
    /// <summary>English ordinal suffix for a positive kill count (1st, 2nd, 3rd, 4th, ..., the
    /// 11th/12th/13th exception, 21st/22nd/23rd, ...). A zero or negative count (never expected
    /// from a live kill tally, but the tally is player-inspectable JSON) falls back to "th".</summary>
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

    /// <summary>The locked toast wording. Tier 3 with a signature weapon announces the unlock;
    /// every other crossing announces the growth suffix -- Tuning.Suffix (the card-painting
    /// array: "  "/"+ "/"+2"/"+3") trimmed of its padding, so tier 1 reads "grown to +!",
    /// matching the card's own "+" suffix (user-locked wording, 2026-07-02).</summary>
    public static string Payload(string name, int tier, int kills, string? sigLabel)
    {
        int t = Math.Clamp(tier, 1, 3);
        string suffix = OrdinalSuffix(kills);
        if (t == 3 && !string.IsNullOrEmpty(sigLabel))
            return $"{name} has gained its {kills}{suffix} kill and has unlocked {sigLabel}!";
        return $"{name} has gained its {kills}{suffix} kill and has grown to {Tuning.Suffix[t].Trim()}!";
    }

    /// <summary>A crossing fires ONE toast at the HIGHEST new tier -- a tally jump (dev seeding,
    /// fast-forwarded battles) that skips a tier must not queue one toast per skipped tier.
    /// Returns 0 (no toast) when newTier has not advanced past prevTier.</summary>
    public static int CrossedTier(int prevTier, int newTier)
        => newTier > prevTier ? newTier : 0;

    /// <summary>Weapon Chronicle kill-count milestones, DESCENDING so the scan below finds the
    /// HIGHEST crossed milestone first. 1 is first blood.</summary>
    private static readonly int[] Milestones = { 1000, 500, 250, 100, 1 };

    /// <summary>A crossing fires ONE toast at the HIGHEST milestone crossed -- mirrors
    /// CrossedTier's jump semantics: a tally jump that skips a milestone announces only the
    /// highest, never one toast per skipped milestone. First blood (1) only fires from a true
    /// zero start -- the construction prime baselines already-loaded tallies, and the dev-seed
    /// floor (Tuning.DevKillSeed, 3) sits above 1, so a seeded weapon never announces first
    /// blood. Returns 0 (no toast) when no milestone was crossed.</summary>
    public static int CrossedMilestone(int prevKills, int newKills)
    {
        foreach (int m in Milestones)
            if (prevKills < m && newKills >= m) return m;
        return 0;
    }

    /// <summary>The locked Weapon Chronicle milestone wording (cite: weapon-chronicle-design,
    /// 2026-07-02). The default arm is unreachable against the fixed Milestones table above --
    /// kept as a safe fallback rather than an exception.</summary>
    public static string MilestonePayload(string name, int milestone) => milestone switch
    {
        1 => $"{name} draws first blood!",
        100 => $"{name} claims its 100th soul!",
        250 => $"{name} has felled 250 foes!",
        500 => $"500 souls rest upon {name}'s edge!",
        1000 => $"{name}, slayer of a thousand!",
        _ => $"{name} has felled {milestone} foes!",
    };

    /// <summary>The locked wording for a first-blood kill that ALSO crosses a tier in the same
    /// DetectCrossings pass -- the two events merge into one toast rather than queuing twice
    /// (only possible for milestone 1; higher milestones can never coincide with a fresh weapon's
    /// first kill). Mirrors Payload's tier-3-signature-unlock arm.</summary>
    public static string FirstBloodTierPayload(string name, int tier, string? sigLabel)
    {
        int t = Math.Clamp(tier, 1, 3);
        if (t == 3 && !string.IsNullOrEmpty(sigLabel))
            return $"{name} draws first blood and has unlocked {sigLabel}!";
        return $"{name} draws first blood and has grown to {Tuning.Suffix[t].Trim()}!";
    }
}
