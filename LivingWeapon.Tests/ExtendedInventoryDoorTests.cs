using System;
using System.IO;
using LivingWeapon;
using Reloaded.Hooks.Definitions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-393: the extended inventory's door reading. Waiting before the boot arm, Open once armed, a failed
/// check when an install step refuses, and an absence when nothing is shipped or the loader gave no hooks.
/// </summary>
public class ExtendedInventoryDoorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lw_ext_door_" + Guid.NewGuid().ToString("N"));
    public ExtendedInventoryDoorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    internal static ExtendedInventoryData.LoadResult Moonblade() => new()
    {
        FolderPresent = true,
        Items = new[]
        {
            new ExtendedItemDef
            {
                Id = 261, Name = "Moonblade", Category = "Sword", CloneDonor = 37, ArtDonor = 37, SeedCount = 1,
                CatalogRecord = new byte[] { 0x04, 0x16, 0x00, 0x80, 0x25, 0x03, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00 },
                WeaponRow = new byte[] { 0x01, 0x8E, 0x01, 0xFF, 0x0F, 0x00, 0x00, 0x00 },
            },
        },
    };

    private ExtendedInventory Build(FakeCodePatcher f, ExtendedInventoryData.LoadResult data,
        Func<IReloadedHooks?, string?>? hooks = null)
        => new(f, new FakeNearAllocator(), data, ExtendedBagSidecar.Load(Path.Combine(_dir, ExtendedBagSidecar.FileName)), hooks);

    [Fact]
    public void Door_waits_before_the_boot_arm_ran()
    {
        var inv = Build(ExtendedInventoryTests.VanillaImage(), Moonblade(), _ => null);

        Assert.Equal(DoorState.Waiting, inv.Door.State);
        Assert.Equal("the loader has not handed over its hooks yet", inv.Door.Reason);
        Assert.False(inv.Door.FailedCheck);
    }

    [Fact]
    public void Door_opens_when_the_arm_succeeds()
    {
        var inv = Build(ExtendedInventoryTests.VanillaImage(), Moonblade(), _ => null);

        inv.BootArm(null);

        Assert.True(inv.Armed, inv.Refusal);
        Assert.Equal(DoorState.Open, inv.Door.State);
    }

    [Fact]
    public void An_install_refusal_closes_the_door_as_a_failed_check_with_a_player_reason()
    {
        var f = ExtendedInventoryTests.VanillaImage();
        f.Seed(ExtendedSites.BootPatches(1)[0].Addr, 0x07);   // a moved first boot site refuses before any write
        var inv = Build(f, Moonblade(), _ => null);

        inv.BootArm(null);

        Assert.False(inv.Armed);
        Assert.Equal(DoorState.Closed, inv.Door.State);
        Assert.True(inv.Door.FailedCheck);
        // The raw diagnostic (addresses, bytes) stays on Refusal for the log; the door carries plain words for the box.
        Assert.NotNull(inv.Refusal);
        Assert.Equal("the game code the new items plug into is not where this mod expects it", inv.Door.Reason);
        Assert.DoesNotContain("0x", inv.Door.Reason);
        Assert.False(inv.Door.Plain);
    }

    [Fact]
    public void Nothing_shipped_closes_the_door_as_an_absence()
    {
        var inv = Build(ExtendedInventoryTests.VanillaImage(), new ExtendedInventoryData.LoadResult(), _ => null);

        inv.BootArm(null);

        Assert.Equal(DoorState.Closed, inv.Door.State);
        Assert.False(inv.Door.FailedCheck);
        Assert.Equal("no new items are shipped in this build", inv.Door.Reason);
    }

    [Fact]
    public void A_missing_loader_closes_the_door_as_an_absence_and_keeps_the_warning()
    {
        using var cap = LogCapture.Start();
        var inv = Build(ExtendedInventoryTests.VanillaImage(), Moonblade());   // the default install step refuses null hooks

        inv.BootArm(null);

        Assert.False(inv.Armed);
        Assert.Equal(DoorState.Closed, inv.Door.State);
        Assert.False(inv.Door.FailedCheck);
        Assert.Equal("the loader gave no hooks", inv.Door.Reason);
        Assert.Contains(cap.File, l => l.Contains("[WARN]") && l.Contains("game-hooks helper mod"));
    }

    [Fact]
    public void A_missing_loader_is_seen_before_the_install_touches_the_game()
    {
        var f = ExtendedInventoryTests.VanillaImage();
        var inv = Build(f, Moonblade());

        inv.BootArm(null);

        Assert.Empty(f.Writes);
        Assert.False(inv.Door.FailedCheck);
        Assert.Equal(ExtendedInventory.NoHooksRefusal, inv.Refusal);
    }

    [Fact]
    public void Tables_that_fail_validation_stay_a_failed_check()
    {
        var inv = Build(ExtendedInventoryTests.VanillaImage(),
            new ExtendedInventoryData.LoadResult { FolderPresent = true, Errors = new[] { "id 261: no row" } });

        inv.BootArm(null);

        Assert.Equal(DoorState.Closed, inv.Door.State);
        Assert.True(inv.Door.FailedCheck);
    }
}
