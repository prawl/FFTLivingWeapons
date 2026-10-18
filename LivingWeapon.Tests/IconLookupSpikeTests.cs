using Xunit;

namespace LivingWeapon.Tests;

/// <summary>The pure parts of the LW-427 lookup tap. The detours themselves need the game.</summary>
public class IconLookupSpikeTests
{
    [Fact]
    public void Tail_keeps_the_icon_file_stem_only()
    {
        Assert.Equal("ei_033_uitx", IconLookupSpike.Tail("UITextureParts://ui/ffto/icon/equip_item/textureparts/ei_033_uitx"));
        Assert.Equal("plain", IconLookupSpike.Tail("plain"));
    }

    [Fact]
    public void Prologues_are_the_bytes_read_from_the_exe()
    {
        Assert.True(HookLandmark.Verify(IconLookupSpike.LookupPrologue, IconLookupSpike.LookupPrologue));
        Assert.False(HookLandmark.Verify(IconLookupSpike.LoadPrologue, IconLookupSpike.LookupPrologue));
    }

    [Fact]
    public void Tracing_starts_off_and_a_missing_mod_dir_never_turns_it_on()
    {
        var spike = new IconLookupSpike(new FakeSparseMemory(), null);
        for (int i = 0; i < 100; i++) spike.Tick();
        Assert.False(spike.Tracing);
    }
}
