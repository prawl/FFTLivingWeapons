using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Direct tests for the WP-scratch paint: mirror-weapon keying plus the ownership guard
/// (write only when the scratch byte holds the natural or already-boosted value -- the
/// behavior contract that keeps another unit's number from being stamped over).
/// DisplayTests pins the same contract end-to-end through Tick.
/// </summary>
public class WpScratchPainterTests
{
    private const long StaticsBase = 0xA0_0000_0000L;

    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 10, new WeaponMeta { Name = "SwordA", Flavor = "Bright edge of dawn", Wp = 12, Cat = "Sword", Formula = 1 } },
    };

    /// <summary>Statics layout mirrors the Display fixtures: MirrorWeapon u16 at +0,
    /// MirrorOffHand u16 at +2, WpScratch u8 at +4.</summary>
    private static (FakeHeap heap, WpScratchPainter painter) Build(
        int mirrorId, byte scratch, Func<int, int> killsFor, bool writable = true)
    {
        var statics = new byte[16];
        statics[0] = (byte)(mirrorId & 0xFF);
        statics[1] = (byte)(mirrorId >> 8);
        statics[4] = scratch;

        var heap = new FakeHeap((StaticsBase, statics, writable));
        var mem = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  StaticsBase + 0,
            mirrorOffHandAddr: StaticsBase + 2,
            wpScratchAddr:     StaticsBase + 4);
        return (heap, new WpScratchPainter(mem, BuildMeta(), killsFor));
    }

    [Fact]
    public void Writes_boosted_wp_when_scratch_holds_natural()
    {
        // 50 kills = tier 3 under prod thresholds -> boosted = round(12 * 1.30) = 16.
        var (heap, painter) = Build(mirrorId: 10, scratch: 12, killsFor: _ => 50);

        painter.Paint();

        int expected = (int)Math.Round(12 * (1.0 + Tuning.Factor[3]));
        Assert.Equal((byte)expected, heap.U8(StaticsBase + 4));
    }

    [Fact]
    public void Skips_write_when_scratch_already_boosted()
    {
        int boosted = (int)Math.Round(12 * (1.0 + Tuning.Factor[3]));
        var (heap, painter) = Build(mirrorId: 10, scratch: (byte)boosted, killsFor: _ => 50);
        int writesBefore = heap.Writes;

        painter.Paint();

        Assert.Equal(writesBefore, heap.Writes);
        Assert.Equal((byte)boosted, heap.U8(StaticsBase + 4));
    }

    [Fact]
    public void Unrelated_scratch_value_is_not_owned_and_stays_untouched()
    {
        var (heap, painter) = Build(mirrorId: 10, scratch: 99, killsFor: _ => 50);

        painter.Paint();

        Assert.Equal(99, heap.U8(StaticsBase + 4));
    }

    [Fact]
    public void Tier_zero_natural_equals_boosted_no_write()
    {
        // 0 kills -> tier 0 -> boosted == natural: a write would be pointless churn.
        var (heap, painter) = Build(mirrorId: 10, scratch: 12, killsFor: _ => 0);
        int writesBefore = heap.Writes;

        painter.Paint();

        Assert.Equal(writesBefore, heap.Writes);
        Assert.Equal(12, heap.U8(StaticsBase + 4));
    }

    [Fact]
    public void Unknown_mirror_id_is_ignored()
    {
        var (heap, painter) = Build(mirrorId: 42, scratch: 12, killsFor: _ => 50);
        int writesBefore = heap.Writes;

        painter.Paint();

        Assert.Equal(writesBefore, heap.Writes);
    }

    [Fact]
    public void Unwritable_scratch_is_left_alone()
    {
        var (heap, painter) = Build(mirrorId: 10, scratch: 12, killsFor: _ => 50, writable: false);

        painter.Paint();

        Assert.Equal(12, heap.U8(StaticsBase + 4));
    }

    [Fact]
    public void Unreadable_scratch_does_not_throw()
    {
        // No heap region at all: every guarded read misses; Paint must be a safe no-op.
        var heap = new FakeHeap(Array.Empty<(long, byte[])>());
        var mem = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  StaticsBase + 0,
            mirrorOffHandAddr: StaticsBase + 2,
            wpScratchAddr:     StaticsBase + 4);
        var painter = new WpScratchPainter(mem, BuildMeta(), _ => 50);

        var ex = Record.Exception(() => painter.Paint());

        Assert.Null(ex);
    }
}
