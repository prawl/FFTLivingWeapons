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
}
