using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S2: the category getter detour's pure decision and its arm landmark.</summary>
public class CategoryGetterHookTests
{
    private static readonly int[] Donors = { 37, 67 };

    [Theory]
    [InlineData(261L, 37)]
    [InlineData(262L, 67)]
    [InlineData(0x4105L, 37)]     // flag bits masked
    [InlineData(0x7FFF0105L, 37)] // anything above bit 9 masked
    [InlineData(260L, -1)]
    [InlineData(263L, -1)]        // past the donor table: passthrough
    [InlineData(37L, -1)]
    [InlineData(0L, -1)]
    public void Resolve_answers_as_the_donor_inside_the_table_and_passes_through_outside(long rcx, int expected)
        => Assert.Equal(expected, CategoryGetterHook.Resolve(rcx, 261, Donors));

    [Fact]
    public void ShouldArm_requires_the_getters_prologue()
    {
        // sub rsp,28h; movzx r11d,cx; mov eax,3FFh -- the 1.5.2 entry, read on disk 2026-08-27
        Assert.True(CategoryGetterHook.ShouldArm(true, new byte[] { 0x48, 0x83, 0xEC, 0x28, 0x44, 0x0F, 0xB7, 0xD9, 0xB8, 0xFF, 0x03, 0x00, 0x00, 0x66 }));
        Assert.False(CategoryGetterHook.ShouldArm(true, new byte[] { 0x48, 0x83, 0xEC, 0x28, 0x44, 0x0F, 0xB7, 0xD9, 0xB8, 0xFF, 0x01, 0x00, 0x00 }));
        Assert.False(CategoryGetterHook.ShouldArm(true, new byte[] { 0x48, 0x83 }));
        Assert.False(CategoryGetterHook.ShouldArm(false, CategoryGetterHook.ExpectedPrologue));
        Assert.Equal(Offsets.FnCategoryGetter, new CategoryGetterHook(new FakeCodePatcher(), 261, Donors).TargetAddr);
    }

    [Fact]
    public void Install_survives_a_detour_firing_during_activate()
    {
        var f = new FakeCodePatcher();
        var hook = new CategoryGetterHook(f, 261, Donors);
        f.Seed(hook.TargetAddr, CategoryGetterHook.ExpectedPrologue);
        var fh = FiringHooks.Make(out var hooks);

        Assert.Null(hook.Install(hooks));

        Assert.True(fh.Created[0].Fired);
        Assert.True(hook.IsActive);
    }

    [Fact]
    public void Install_after_a_failed_activate_leaves_no_stale_hook_and_retries_with_a_new_one()
    {
        var f = new FakeCodePatcher();
        var hook = new CategoryGetterHook(f, 261, Donors);
        f.Seed(hook.TargetAddr, CategoryGetterHook.ExpectedPrologue);
        var fh = FiringHooks.Make(out var hooks);
        fh.ThrowOnActivation = 1;
        fh.EnabledWhenThrowing = true;

        Assert.NotNull(hook.Install(hooks));
        Assert.False(hook.IsActive);
        Assert.False(fh.Created[0].IsHookEnabled);

        fh.ThrowOnActivation = 0;
        Assert.Null(hook.Install(hooks));
        Assert.Equal(2, fh.Created.Count);
        Assert.True(fh.Created[1].Activated);
        Assert.True(hook.IsActive);
    }
}
