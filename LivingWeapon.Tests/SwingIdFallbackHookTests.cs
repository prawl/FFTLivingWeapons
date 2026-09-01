using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-365: the swing-id fallback hook (SwingIdFallbackHook.cs), ThunkClone's stateful
/// twin for a mid-instruction jump instead of a five-byte accessor thunk -- the site is a 7-byte
/// movzx, so the install writes a 5-byte E9 jmp plus two 0x90 pad bytes over it, not a straight
/// overwrite.</summary>
public class SwingIdFallbackHookTests
{
    private static readonly byte[] SeededSite = SwingIdFallbackHook.ExpectedSite;

    [Fact]
    public void Installs_over_a_seeded_site_and_the_page_holds_the_pinned_stub()
    {
        var fake = new FakeCodePatcher();
        var alloc = new FakeNearAllocator();
        long site = Offsets.FnSwingPrepIdCopy;
        fake.Seed(site, SeededSite);

        var hook = new SwingIdFallbackHook(site);
        Assert.Null(hook.Install(fake, alloc, lo: 261, count: 7));
        Assert.True(hook.Installed);

        var now = fake.Read(site, 7);
        Assert.Equal(0xE9, now[0]);
        int rel32 = BitConverter.ToInt32(now, 1);
        Assert.Equal(hook.StubAddr, site + 5 + rel32);
        Assert.Equal(new byte[] { 0x90, 0x90 }, new[] { now[5], now[6] });

        var expectedStub = ThunkStub.EmitSwingIdFallbackStub(Offsets.SwingIdWord, Offsets.BattleUnitsBase + Offsets.CWeapon, 261, 267, site + 7);
        var gotStub = fake.Read(hook.StubAddr, expectedStub.Length);
        Assert.Equal(expectedStub, gotStub);
    }

    [Fact]
    public void Refuses_when_the_site_is_unreadable_and_writes_nothing()
    {
        var fake = new FakeCodePatcher();   // unseeded; ZeroFillUnseeded defaults to false, so TryRead fails
        var alloc = new FakeNearAllocator();
        long site = Offsets.FnSwingPrepIdCopy;

        var hook = new SwingIdFallbackHook(site);
        var why = hook.Install(fake, alloc, lo: 261, count: 7);
        Assert.NotNull(why);
        Assert.Contains("is unreadable", why);
        Assert.False(hook.Installed);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void Refuses_when_the_site_does_not_carry_the_expected_movzx_and_writes_nothing()
    {
        var fake = new FakeCodePatcher();
        var alloc = new FakeNearAllocator();
        long site = Offsets.FnSwingPrepIdCopy;
        fake.Seed(site, 0x0F, 0xB7, 0x05, 0xAA, 0xBB, 0xCC, 0x00);   // a plausible but wrong movzx

        var hook = new SwingIdFallbackHook(site);
        var why = hook.Install(fake, alloc, lo: 261, count: 7);
        Assert.NotNull(why);
        Assert.Contains("expected movzx", why);
        Assert.False(hook.Installed);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void Refuses_when_the_allocator_returns_zero_and_writes_nothing()
    {
        var fake = new FakeCodePatcher();
        var alloc = new FakeNearAllocator { RefuseAfter = 0 };
        long site = Offsets.FnSwingPrepIdCopy;
        fake.Seed(site, SeededSite);

        var hook = new SwingIdFallbackHook(site);
        var why = hook.Install(fake, alloc, lo: 261, count: 7);
        Assert.NotNull(why);
        Assert.Contains("no executable page", why);
        Assert.False(hook.Installed);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void Restore_puts_the_seven_bytes_back_and_a_second_install_or_restore_is_a_no_op()
    {
        var fake = new FakeCodePatcher();
        var alloc = new FakeNearAllocator();
        long site = Offsets.FnSwingPrepIdCopy;
        fake.Seed(site, SeededSite);

        var hook = new SwingIdFallbackHook(site);
        Assert.Null(hook.Install(fake, alloc, lo: 261, count: 7));
        long firstStubAddr = hook.StubAddr;

        Assert.Null(hook.Install(fake, alloc, lo: 261, count: 7));   // second install: no-op success
        Assert.Equal(firstStubAddr, hook.StubAddr);
        Assert.Equal(1, alloc.Requests.Count);   // no second allocation

        Assert.True(hook.Restore(fake));
        Assert.False(hook.Installed);
        Assert.Equal(SeededSite, fake.Read(site, 7));

        Assert.True(hook.Restore(fake));   // second restore: no-op true
    }

    [Fact]
    public void ShouldArm_is_false_for_a_short_or_unreadable_buffer()
    {
        Assert.False(SwingIdFallbackHook.ShouldArm(false, SeededSite));
        Assert.False(SwingIdFallbackHook.ShouldArm(true, null));
        Assert.False(SwingIdFallbackHook.ShouldArm(true, new byte[] { 0x0F, 0xB7, 0x05 }));
        Assert.True(SwingIdFallbackHook.ShouldArm(true, SeededSite));
    }

    [Fact]
    public void Refuses_with_no_extended_ids_instead_of_throwing()
    {
        var fake = new FakeCodePatcher();
        var alloc = new FakeNearAllocator();
        long site = Offsets.FnSwingPrepIdCopy;
        fake.Seed(site, SeededSite);

        var hook = new SwingIdFallbackHook(site);
        var why = hook.Install(fake, alloc, lo: 261, count: 0);
        Assert.Equal("swing-id fallback: no extended ids", why);
        Assert.False(hook.Installed);
        Assert.Empty(fake.Writes);
    }
}
