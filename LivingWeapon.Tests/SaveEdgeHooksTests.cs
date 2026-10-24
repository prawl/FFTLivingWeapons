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

    /// <summary>LW-351 stage 3 P4-F3: the replay callback and the tracker publication used to share
    /// one try, so a throwing replay skipped <see cref="SaveEdgeTracker.OnApplied"/> too -- the very
    /// edge Engine's tick fallback keys on. A throw here must cost only the replay, not the edge.</summary>
    [Fact]
    public void A_throwing_replay_still_publishes_the_load_edge()
    {
        var f = new FakeCodePatcher();
        f.Seed(Offsets.SaveStructPtr, BitConverter.GetBytes(StructAddr));
        f.Seed(StructAddr + Offsets.SaveHeaderKeyOff, SaveEdgeTrackerTests.Header(2482));
        var tracker = new SaveEdgeTracker();
        var hooks = new SaveEdgeHooks(f, tracker, new List<int>(),
            replayOnLoad: _ => throw new InvalidOperationException("boom"));

        hooks.AfterApply(second: false);

        Assert.True(tracker.TryTakePendingLoad(out var key));
        Assert.Equal(SaveEdgeTracker.KeyFromHeader(SaveEdgeTrackerTests.Header(2482)), key);
    }

    /// <summary>LW-452: a flee or a Retry puts the pre battle snapshot back without entering either load
    /// routine, so the rewind hook hands the snapshot header here and it takes the same load edge.</summary>
    [Fact]
    public void AfterRewind_publishes_a_load_edge_for_the_snapshot_key_and_logs_the_rewind_line()
    {
        var header = SaveEdgeTrackerTests.Header(3141);
        string? replayed = null;
        var tracker = new SaveEdgeTracker();
        var hooks = new SaveEdgeHooks(new FakeCodePatcher(), tracker, new List<int>(), replayOnLoad: k => replayed = k);
        using var cap = LogCapture.Start();

        hooks.AfterRewind(header);

        string key = SaveEdgeTracker.KeyFromHeader(header);
        Assert.Equal(key, replayed);
        Assert.True(tracker.TryTakePendingLoad(out var taken));
        Assert.Equal(key, taken);
        Assert.Single(cap.File, l => l.Contains("rewound to its pre battle save") && l.Contains("put back with it"));
    }

    [Fact]
    public void AfterRewind_still_publishes_the_edge_when_the_replay_throws()
    {
        var header = SaveEdgeTrackerTests.Header(2718);
        var tracker = new SaveEdgeTracker();
        var hooks = new SaveEdgeHooks(new FakeCodePatcher(), tracker, new List<int>(),
            replayOnLoad: _ => throw new InvalidOperationException("boom"));

        hooks.AfterRewind(header);

        Assert.True(tracker.TryTakePendingLoad(out var key));
        Assert.Equal(SaveEdgeTracker.KeyFromHeader(header), key);
    }

    [Fact]
    public void SerializeCore_calls_the_on_serialized_hook_after_the_header_is_read()
    {
        var f = new FakeCodePatcher();
        f.Seed(Offsets.SaveStructPtr, BitConverter.GetBytes(StructAddr));
        f.Seed(StructAddr + Offsets.SaveHeaderKeyOff, SaveEdgeTrackerTests.Header(2024));
        var tracker = new SaveEdgeTracker();
        int calls = 0;
        bool pendingWhenCalled = false;
        var hooks = new SaveEdgeHooks(f, tracker, new List<int>(), onSerialized: () => { calls++; pendingWhenCalled = tracker.TryTakePendingSave(out _, out _); });

        hooks.SerializeCore(() => (nint)1);

        Assert.Equal(1, calls);
        Assert.True(pendingWhenCalled);

        var unreadable = new SaveEdgeHooks(new FakeCodePatcher(), new SaveEdgeTracker(), new List<int>(), onSerialized: () => calls++);
        unreadable.SerializeCore(() => (nint)1);
        Assert.Equal(1, calls);   // no header, no call
    }

    [Fact]
    public void AfterApply_calls_on_applied_after_the_replay()
    {
        var f = new FakeCodePatcher();
        f.Seed(Offsets.SaveStructPtr, BitConverter.GetBytes(StructAddr));
        f.Seed(StructAddr + Offsets.SaveHeaderKeyOff, SaveEdgeTrackerTests.Header(2025));
        var order = new List<string>();
        var hooks = new SaveEdgeHooks(f, new SaveEdgeTracker(), new List<int>(), replayOnLoad: _ => order.Add("replay"), onApplied: () => order.Add("applied"));
        hooks.AfterApply(second: false);
        Assert.Equal(new[] { "replay", "applied" }, order);

        var throwing = new SaveEdgeHooks(f, new SaveEdgeTracker(), new List<int>(), replayOnLoad: _ => throw new InvalidOperationException("boom"), onApplied: () => order.Add("again"));
        throwing.AfterApply(second: false);
        Assert.Equal("again", order[^1]);   // a failed replay still captures, the edge is published regardless
    }

    [Fact]
    public void AfterRewind_calls_on_rewound_only_after_a_successful_replay()
    {
        int rewound = 0;
        var found = new SaveEdgeHooks(new FakeCodePatcher(), new SaveEdgeTracker(), new List<int>(), replayOnLoad: _ => { },
            onRewound: () => rewound++);
        found.AfterRewind(SaveEdgeTrackerTests.Header(77));
        Assert.Equal(1, rewound);

        var missing = new SaveEdgeHooks(new FakeCodePatcher(), new SaveEdgeTracker(), new List<int>(), replayOnLoad: _ => { },
            hasRecord: _ => false, onRewound: () => rewound++);
        missing.AfterRewind(SaveEdgeTrackerTests.Header(78));
        Assert.Equal(1, rewound);   // the missing record branch never calls it

        var throwing = new SaveEdgeHooks(new FakeCodePatcher(), new SaveEdgeTracker(), new List<int>(),
            replayOnLoad: _ => throw new InvalidOperationException("boom"), onRewound: () => rewound++);
        throwing.AfterRewind(SaveEdgeTrackerTests.Header(79));
        Assert.Equal(1, rewound);   // a failed replay is no restore to capture
    }

    [Fact]
    public void AfterRewind_with_no_record_for_the_key_replays_nothing_and_publishes_no_edge()
    {
        var header = SaveEdgeTrackerTests.Header(1618);
        int replays = 0;
        var tracker = new SaveEdgeTracker();
        var hooks = new SaveEdgeHooks(new FakeCodePatcher(), tracker, new List<int>(), replayOnLoad: _ => replays++,
            hasRecord: _ => false);
        using var cap = LogCapture.Start();

        hooks.AfterRewind(header);

        Assert.Equal(0, replays);
        Assert.False(tracker.TryTakePendingLoad(out _));
        Assert.Single(cap.File, l => l.Contains("holds no bag counts for that save"));
    }

    private static FakeCodePatcher Prologued()
    {
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        f.Seed(Offsets.FnSaveSerialize, SaveEdgeHooks.SerializePrologue);
        f.Seed(Offsets.FnSaveApply, SaveEdgeHooks.ApplyPrologue);
        f.Seed(Offsets.FnSaveApplyB, SaveEdgeHooks.ApplyBPrologue);
        return f;
    }

    [Fact]
    public void Install_survives_detours_firing_during_activate()
    {
        var hook = new SaveEdgeHooks(Prologued(), new SaveEdgeTracker(), new List<int>());
        var fh = FiringHooks.Make(out var hooks);

        Assert.Null(hook.Install(hooks));

        Assert.Equal(3, fh.Created.Count);
        Assert.All(fh.Created, h => Assert.True(h.Fired));
        Assert.True(hook.IsActive);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Install_failing_at_an_activation_stores_only_the_hooks_that_went_live(int k)
    {
        var hook = new SaveEdgeHooks(Prologued(), new SaveEdgeTracker(), new List<int>());
        var fh = FiringHooks.Make(out var hooks);
        fh.ThrowOnActivation = k;
        fh.EnabledWhenThrowing = true;

        Assert.NotNull(hook.Install(hooks));
        Assert.False(hook.IsActive);
        Assert.Equal(k - 1, hook.StoredHooks);
        Assert.Equal(3, fh.Created.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i < k - 1, fh.Created[i].Activated);
            Assert.False(fh.Created[i].IsHookEnabled);
            Assert.Equal(i <= k - 1 ? 1 : 0, fh.Created[i].DisableCalls);
        }

        fh.ThrowOnActivation = 0;
        string? second = hook.Install(hooks);
        if (k > 1)
        {
            Assert.Equal("save-edge hooks: an earlier install failed partway, restart the game to retry", second);
            Assert.Equal(3, fh.Created.Count);
            Assert.False(hook.IsActive);
        }
        else
        {
            Assert.Null(second);
            Assert.Equal(6, fh.Created.Count);
            Assert.True(hook.IsActive);
        }
    }

    [Fact]
    public void Install_after_a_release_switches_the_same_hooks_back_on()
    {
        var hook = new SaveEdgeHooks(Prologued(), new SaveEdgeTracker(), new List<int>());
        var fh = FiringHooks.Make(out var hooks);
        Assert.Null(hook.Install(hooks));
        hook.Release();
        Assert.False(hook.IsActive);

        Assert.Null(hook.Install(hooks));

        Assert.True(hook.IsActive);
        Assert.Equal(3, fh.Created.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Install_failing_at_a_create_activates_nothing_and_a_retry_builds_all_three(int c)
    {
        var hook = new SaveEdgeHooks(Prologued(), new SaveEdgeTracker(), new List<int>());
        var fh = FiringHooks.Make(out var hooks);
        fh.Fire = false;
        fh.ThrowOnCreate = c;

        string? first = hook.Install(hooks);

        Assert.Contains("save-edge hooks: install failed (create refused)", first);
        Assert.False(hook.IsActive);
        Assert.Equal(0, hook.StoredHooks);
        Assert.Equal(c - 1, fh.Created.Count);
        Assert.All(fh.Created, h => Assert.False(h.Activated));

        fh.ThrowOnCreate = 0;
        Assert.Null(hook.Install(hooks));
        Assert.True(hook.IsActive);
        Assert.Equal(c - 1 + 3, fh.Created.Count);
        Assert.All(fh.Created.Skip(c - 1), h => Assert.True(h.Activated));
    }
}
