using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Locks in the Plague latch grace window. Live bug (2026-06-10): the poison bit is set by the
/// engine during attack resolution, which can land a tick before the acted window is observed
/// open (actor resolution lag) or after it closes (animation tail) -- the strict
/// edge-during-window latch then never fires, and a chocobo cleansed a "permanent" poison
/// unopposed (log showed four ACTIVE windows and zero latches). The fix: record WHEN each
/// slot's poison edge happened and WHEN the window was last open, and latch when the two
/// overlap within PlagueGraceMs in either order. Third-party poison stays excluded: an edge
/// with no Venombolt window within the grace never latches.
/// </summary>
public class PlagueGraceTests
{
    private static readonly (int mhp, int lvl, int br, int fa) Fp = (120, 25, 70, 60);
    private static readonly (int mhp, int lvl, int br, int fa) OtherFp = (90, 12, 55, 45);
    private const long Addr = 0x1000;

    // ---- PlagueBaseline edge timestamps ----

    [Fact]
    public void Records_the_edge_time_on_a_false_to_true_transition()
    {
        var b = new PlagueBaseline();
        b.Update(Addr, Fp, poisoned: false, nowMs: 1000);
        b.Update(Addr, Fp, poisoned: true, nowMs: 1033);
        Assert.Equal(1033, b.LastEdgeMs(Addr, Fp));
    }

    [Fact]
    public void A_unit_first_seen_already_poisoned_never_yields_an_edge()
    {
        var b = new PlagueBaseline();
        b.Update(Addr, Fp, poisoned: true, nowMs: 1000);
        b.Update(Addr, Fp, poisoned: true, nowMs: 2000);
        Assert.True(b.LastEdgeMs(Addr, Fp) < 0);   // sentinel: no edge ever observed
    }

    [Fact]
    public void The_edge_time_survives_subsequent_poisoned_ticks()
    {
        var b = new PlagueBaseline();
        b.Update(Addr, Fp, poisoned: false, nowMs: 1000);
        b.Update(Addr, Fp, poisoned: true, nowMs: 1033);
        b.Update(Addr, Fp, poisoned: true, nowMs: 1066);
        b.Update(Addr, Fp, poisoned: true, nowMs: 1099);
        Assert.Equal(1033, b.LastEdgeMs(Addr, Fp));
    }

    [Fact]
    public void A_cure_then_repoison_records_a_fresh_edge()
    {
        var b = new PlagueBaseline();
        b.Update(Addr, Fp, poisoned: false, nowMs: 1000);
        b.Update(Addr, Fp, poisoned: true, nowMs: 1033);   // first edge
        b.Update(Addr, Fp, poisoned: false, nowMs: 5000);  // cured
        b.Update(Addr, Fp, poisoned: true, nowMs: 7000);   // re-poisoned
        Assert.Equal(7000, b.LastEdgeMs(Addr, Fp));
    }

    [Fact]
    public void A_fingerprint_change_resets_the_slot_and_forgets_the_edge()
    {
        var b = new PlagueBaseline();
        b.Update(Addr, Fp, poisoned: false, nowMs: 1000);
        b.Update(Addr, Fp, poisoned: true, nowMs: 1033);
        b.Update(Addr, OtherFp, poisoned: true, nowMs: 2000);   // unit replaced, arrives poisoned
        Assert.True(b.LastEdgeMs(Addr, OtherFp) < 0);            // no edge for the new unit
        Assert.True(b.LastEdgeMs(Addr, Fp) < 0);                 // old unit's edge gone with it
    }

    [Fact]
    public void Asking_with_a_mismatched_fingerprint_returns_the_sentinel()
    {
        var b = new PlagueBaseline();
        b.Update(Addr, Fp, poisoned: false, nowMs: 1000);
        b.Update(Addr, Fp, poisoned: true, nowMs: 1033);
        Assert.True(b.LastEdgeMs(Addr, OtherFp) < 0);
    }

    // ---- ShouldLatchNow: the two-sided grace overlap ----

    [Theory]
    // edge then window, inside grace -> latch
    [InlineData(1000, 2500, 2600, true)]
    // window then edge, inside grace -> latch
    [InlineData(2500, 1000, 2600, true)]
    // edge fresh, window stale -> no latch (third-party poison, wielder acted long ago)
    [InlineData(9000, 1000, 9100, false)]
    // window fresh, edge stale -> no latch (pre-existing poison)
    [InlineData(1000, 9000, 9100, false)]
    // both stale -> no latch
    [InlineData(1000, 1500, 9900, false)]
    // simultaneous (same tick) -> latch
    [InlineData(5000, 5000, 5000, true)]
    public void Latches_only_when_edge_and_window_overlap_within_grace(
        long edgeMs, long windowMs, long now, bool expect)
    {
        Assert.Equal(expect, Plague.ShouldLatchNow(
            isEnemy: true, held: false, lastEdgeMs: edgeMs, lastActiveMs: windowMs,
            now: now, graceMs: Tuning.PlagueGraceMs));
    }

    [Fact]
    public void Never_latches_an_ally_or_an_already_held_slot()
    {
        Assert.False(Plague.ShouldLatchNow(false, false, 1000, 1000, 1100, Tuning.PlagueGraceMs));
        Assert.False(Plague.ShouldLatchNow(true, true, 1000, 1000, 1100, Tuning.PlagueGraceMs));
    }

    [Fact]
    public void Sentinel_timestamps_never_satisfy_the_grace()
    {
        long sentinel = long.MinValue / 2;
        Assert.False(Plague.ShouldLatchNow(true, false, sentinel, 1000, 1100, Tuning.PlagueGraceMs));
        Assert.False(Plague.ShouldLatchNow(true, false, 1000, sentinel, 1100, Tuning.PlagueGraceMs));
    }

    [Fact]
    public void Grace_is_two_seconds()
        => Assert.Equal(2000, Tuning.PlagueGraceMs);
}
