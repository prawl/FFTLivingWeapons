using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-147: OffsetRemapMem forwards every IGameMemory member it overrides to the inner fake at
/// the remapped address -- but W16 was missing from that override list, so a W16 call fell
/// through to IGameMemory's interface-default W16 (a no-op, see GameMemory.cs) instead of
/// reaching the inner fake. No production call site hits this today (WpScratchPainter writes via
/// WriteBytes, not W16), so the gap was silent rather than a live bug -- this pins the adapter's
/// full-forwarding contract so a future W16 call site doesn't vanish the same way.
/// </summary>
public class OffsetRemapMemTests
{
    [Fact]
    public void W16_forwards_through_the_remap_to_the_inner_fake()
    {
        var inner = new FakeSparseMemory();
        // Typed as the IGameMemory interface: this is exactly how production code (and every
        // real call site) holds the adapter, so a missing override falls through to
        // IGameMemory's no-op default (GameMemory.cs) precisely as it would in production.
        IGameMemory wrapped = new OffsetRemapMem(inner, mirrorWeaponAddr: 0x2000,
                                                  mirrorOffHandAddr: 0x3000, wpScratchAddr: 0x4000);

        wrapped.W16(Offsets.MirrorWeapon, 0xBEEF);

        Assert.Equal(0xBEEF, inner.U16(0x2000));         // landed at the REMAPPED address...
        Assert.Equal(0, inner.U16(Offsets.MirrorWeapon)); // ...not the original game address
    }

    [Fact]
    public void W16_on_an_address_outside_the_three_remapped_slots_passes_through_unchanged()
    {
        var inner = new FakeSparseMemory();
        IGameMemory wrapped = new OffsetRemapMem(inner, mirrorWeaponAddr: 0x2000,
                                                  mirrorOffHandAddr: 0x3000, wpScratchAddr: 0x4000);

        wrapped.W16(0x9999, 0x1234);

        Assert.Equal(0x1234, inner.U16(0x9999));
    }
}
