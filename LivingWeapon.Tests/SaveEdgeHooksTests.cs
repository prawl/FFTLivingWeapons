using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-371 (T9): <see cref="SaveEdgeHooks.SerializeCore"/>, the test seam
/// <see cref="SaveEdgeHooks"/>'s native DetourSerialize is now a thin wrapper over (the
/// InventoryResetHook.Process idiom: the original call outside every try). Drives the whole
/// serialize-detour body directly with a fake original standing in for the native trampoline, the
/// same idiom BagReplayOrderingTests already uses for AfterApply.
/// </summary>
public class SaveEdgeHooksTests
{
    private sealed class ThrowingCodePatcher : ICodePatcher
    {
        public bool TryRead(long address, int count, out byte[] bytes) => throw new InvalidOperationException("boom");
        public bool TryWrite(long address, byte[] bytes) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Serialize_core_projects_before_forwarding()
    {
        var f = TemplateRelocationTests.SeedVanilla();
        var rel = new TemplateRelocation();
        Assert.Null(rel.Install(f, new FakeNearAllocator()));

        // Seed the inventory chart's PAGE region with two owned ids so Project has something real
        // to copy down before the fake "original" runs.
        var region = new byte[TemplateRelocation.RegionBytes];
        region[0] = 0x0A; region[1] = 0x00; region[2] = 0x0B; region[3] = 0x00;
        region[4] = (byte)(TemplateSeat.EndMarker & 0xFF); region[5] = (byte)(TemplateSeat.EndMarker >> 8);
        for (int i = 6; i < region.Length; i += 2) { region[i] = 0xFF; region[i + 1] = 0xFF; }
        f.Seed(rel.PageAddr + 0x000, region);

        var hooks = new SaveEdgeHooks(f, new SaveEdgeTracker(), new List<int>(), templates: rel);
        byte[]? seenAtCallTime = null;

        nint ret = hooks.SerializeCore(() =>
        {
            // The fake original records the old block's bytes AT CALL TIME: they must already be
            // the projection (LW-371's whole point -- the game's own struct copy, right after,
            // must see what Project just wrote, not the pre-arc contents).
            seenAtCallTime = f.Read(Offsets.InventoryOrderTemplate, 6);
            return (nint)0x1234;
        });

        Assert.Equal((nint)0x1234, ret);
        Assert.Equal(new byte[] { 0x0A, 0x00, 0x0B, 0x00, 0xFF, 0x00 }, seenAtCallTime);
    }

    [Fact]
    public void Serialize_core_still_forwards_when_the_projection_throws()
    {
        var seedF = TemplateRelocationTests.SeedVanilla();
        var rel = new TemplateRelocation();
        Assert.Null(rel.Install(seedF, new FakeNearAllocator()));   // Installed, so Project actually enters its loop

        var throwing = new ThrowingCodePatcher();
        var hooks = new SaveEdgeHooks(throwing, new SaveEdgeTracker(), new List<int>(), templates: rel);
        bool called = false;

        nint ret = hooks.SerializeCore(() => { called = true; return (nint)0x77; });

        Assert.True(called);         // the original call is OUTSIDE every try (never skipped)
        Assert.Equal((nint)0x77, ret);
    }

    [Fact]
    public void Serialize_core_is_a_noop_projection_when_no_templates_are_given()
    {
        var f = new FakeCodePatcher();
        var hooks = new SaveEdgeHooks(f, new SaveEdgeTracker(), new List<int>());   // templates: null, the default
        bool called = false;

        nint ret = hooks.SerializeCore(() => { called = true; return (nint)0x99; });

        Assert.True(called);
        Assert.Equal((nint)0x99, ret);
        Assert.Empty(f.Writes);
    }

    private const long StructAddr = 0x1F0000000L;

    /// <summary>T9e (v1.3, verifier 2 SHOULD-FIX): AfterApply must adopt the old blocks onto the
    /// page (TemplateSync.Adopt) BEFORE the bag/template replay runs, so the seat that follows
    /// always lands on a fresh page (SaveEdgeHooks.Detours.cs's own ordering comment says so; this
    /// pins it against a fake replay callback instead of trusting the comment). A second case
    /// covers the BagReplayOrderingTests.NewWorld shape -- a hooks object built WITHOUT templates
    /// (every manually-built Hooks object in that file) -- and proves it never touches any page.</summary>
    [Fact]
    public void AfterApply_adopts_the_old_blocks_onto_the_page_BEFORE_the_bag_replay()
    {
        var f = TemplateRelocationTests.SeedVanilla();
        var rel = new TemplateRelocation();
        Assert.Null(rel.Install(f, new FakeNearAllocator()));

        // The page starts holding something recognizably STALE (not the fresh chart below), so a
        // replay callback that fires before Adopt runs -- or a page Adopt never touches -- reads
        // this instead and the test catches it.
        var stale = new byte[6] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };
        f.Seed(rel.PageAddr + 0x000, stale);

        // The game's load routine has just restored the inventory old block from the save struct
        // with a FRESH chart; Adopt's job is to copy that onto the page before anything else runs.
        var fresh = TemplateRelocationTests.ChartSpan(TemplateRelocation.Charts[0], 0x6000, 2);
        f.Seed(Offsets.InventoryOrderTemplate, fresh);
        f.Seed(Offsets.PickerOrderTemplate, TemplateRelocationTests.ChartSpan(TemplateRelocation.Charts[1], 0x7000, 2));
        f.Seed(Offsets.PickerAllItemsTemplate, TemplateRelocationTests.ChartSpan(TemplateRelocation.Charts[2], 0x8000, 2));
        f.Seed(Offsets.SaveStructPtr, BitConverter.GetBytes(StructAddr));
        f.Seed(StructAddr + Offsets.SaveHeaderKeyOff, SaveEdgeTrackerTests.Header(1234));

        byte[]? seenAtCallTime = null;
        var hooks = new SaveEdgeHooks(f, new SaveEdgeTracker(), new List<int>(),
            replayOnLoad: _ => seenAtCallTime = f.Read(rel.PageAddr + 0x000, 6), templates: rel);

        hooks.AfterApply(second: false);

        Assert.NotNull(seenAtCallTime);
        Assert.Equal(new byte[] { 0x00, 0x60, 0x01, 0x60, 0xFF, 0x00 }, seenAtCallTime);   // the fresh chart, not the stale page

        // Second case: a hooks object built WITHOUT templates (the shape every
        // BagReplayOrderingTests.World's manually-built Hooks takes) must never touch any page,
        // even one a DIFFERENT, unrelated relocation object thinks is valid.
        var f2 = TemplateRelocationTests.SeedVanilla();
        var rel2 = new TemplateRelocation();
        Assert.Null(rel2.Install(f2, new FakeNearAllocator()));
        var stale2 = new byte[2] { 0xBB, 0xBB };
        f2.Seed(rel2.PageAddr + 0x000, stale2);
        f2.Seed(Offsets.InventoryOrderTemplate, TemplateRelocationTests.ChartSpan(TemplateRelocation.Charts[0], 0x6000, 2));
        f2.Seed(Offsets.SaveStructPtr, BitConverter.GetBytes(StructAddr));
        f2.Seed(StructAddr + Offsets.SaveHeaderKeyOff, SaveEdgeTrackerTests.Header(5678));
        var hooks2 = new SaveEdgeHooks(f2, new SaveEdgeTracker(), new List<int>(), replayOnLoad: _ => { });   // templates: omitted -> null

        hooks2.AfterApply(second: false);

        Assert.Equal(stale2, f2.Read(rel2.PageAddr + 0x000, 2));   // untouched
    }
}
