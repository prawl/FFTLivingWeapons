using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Cheap tripwire against a well-meaning future bump: RosterSlots is a proven CEILING (50 rows,
/// roster_span_probe.py 2026-07-21), not a floor. Slots 50+ are a stale guest bank holding
/// duplicate unit identities (a cloned Beowulf row); scanning them would make fingerprint-keyed
/// resolves ambiguous. Do not raise this constant without a fresh live probe proving the new span
/// is real, contiguous, AND free of duplicate identities.
/// </summary>
public class OffsetsConstantsTests
{
    [Fact]
    public void RosterSlots_is_exactly_50_never_more()
    {
        Assert.Equal(50, Offsets.RosterSlots);
    }

    /// <summary>LW-42 tripwire: the 1.5 slot0 in-battle marker is exactly 0x10 (live battle edges
    /// 2026-07-21; pre-1.5 it was 0xFF and that value is retired). PairArmed and InLiveBattle's
    /// mode-1/5 excuse anchor on this value, so changing it silently revives or kills those
    /// paths; any bump needs fresh live edge evidence, not a guess.</summary>
    [Fact]
    public void Slot0InBattleMarker_is_the_15_marker_0x10()
    {
        Assert.Equal(0x10u, Offsets.Slot0InBattleMarker);
    }
}
