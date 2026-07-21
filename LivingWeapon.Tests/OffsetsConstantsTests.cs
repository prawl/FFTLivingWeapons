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
}
