using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S4: the patch-site table, pinned against the research rig's marker v2 (one
/// extended id, every line owner-observed 2026-08-26/27) and generalised to N ids.</summary>
public class ExtendedSitesTests
{
    [Fact]
    public void One_extended_id_reproduces_the_rig_marker_byte_for_byte()
    {
        var boot = ExtendedSites.BootPatches(1).ToDictionary(p => p.Addr, p => p);
        Assert.Equal(19, boot.Count);
        // the rig's six built-ins
        Assert.Equal((0x01, 0x02), (boot[0x140284800].Old, boot[0x140284800].New));
        foreach (long a in new[] { 0x140284724L, 0x1402847C9L, 0x140288CDAL, 0x140289074L })
            Assert.Equal((0x05, 0x06), (boot[a].Old, boot[a].New));
        Assert.Equal((0x06, 0x07), (boot[0x140284C0A].Old, boot[0x140284C0A].New));
        // the thirteen marker lines
        Assert.Equal((0x05, 0x06), (boot[0x140397121].Old, boot[0x140397121].New));
        Assert.Equal((0x05, 0x06), (boot[0x140287570].Old, boot[0x140287570].New));
        Assert.Equal((0x05, 0x06), (boot[0x140285E2D].Old, boot[0x140285E2D].New));
        Assert.Equal((0x06, 0x07), (boot[0x140286187].Old, boot[0x140286187].New));
        Assert.Equal((0x05, 0x06), (boot[0x1402862F7].Old, boot[0x1402862F7].New));
        Assert.Equal((0x06, 0x07), (boot[0x140285EE7].Old, boot[0x140285EE7].New));
        Assert.Equal((0x05, 0x06), (boot[0x14030CED3].Old, boot[0x14030CED3].New));
        Assert.Equal((0x04, 0x05), (boot[0x140101071].Old, boot[0x140101071].New));
        Assert.Equal((0x05, 0x06), (boot[0x1403602F4].Old, boot[0x1403602F4].New));
        Assert.Equal((0x06, 0x07), (boot[0x140226FDF].Old, boot[0x140226FDF].New));
        Assert.Equal((0x05, 0x06), (boot[0x14028024F].Old, boot[0x14028024F].New));
        Assert.Equal((0x04, 0x05), (boot[0x1401ED95B].Old, boot[0x1401ED95B].New));
        Assert.Equal((0x04, 0x05), (boot[0x1401ED982].Old, boot[0x1401ED982].New));
        Assert.All(boot.Values, p => Assert.False(string.IsNullOrWhiteSpace(p.Label)));

        var post = ExtendedSites.PostLoadPatches(1).ToDictionary(p => p.Addr, p => p);
        Assert.Equal(2, post.Count);
        Assert.Equal((0x06, 0x07), (post[0x14F2EA40F].Old, post[0x14F2EA40F].New));
        Assert.Equal((0x5E, 0x5F), (post[0x14F45D315].Old, post[0x14F45D315].New));
    }

    [Fact]
    public void Seven_extended_ids_widen_every_cap_by_seven_except_the_two_special_encodings()
    {
        var boot = ExtendedSites.BootPatches(7).ToDictionary(p => p.Addr, p => p);
        Assert.Equal(0x0C, boot[0x140285E2D].New);   // 0x105 + 7 = 0x10C
        Assert.Equal(0x0D, boot[0x140284C0A].New);   // lea disp 6 + 7
        Assert.Equal(0x0B, boot[0x140101071].New);   // 0x104 + 7
        Assert.Equal(0x02, boot[0x140284800].New);   // high-byte widening is fixed (ids up to 515)
        var post = ExtendedSites.PostLoadPatches(7).ToDictionary(p => p.Addr, p => p);
        Assert.Equal(0x0D, post[0x14F2EA40F].New);
        Assert.Equal(0x58 ^ 13, post[0x14F45D315].New);   // r15 = 0x58 ^ imm must equal 6 + 7
    }

    [Fact]
    public void Donor_thunks_cover_the_nine_per_category_accessors_with_only_the_sprite_pair_on_the_art_donor()
    {
        Assert.Equal(9, ExtendedSites.DonorThunks.Length);
        Assert.Single(ExtendedSites.DonorThunks, t => t.UsesArtDonor);
        Assert.Equal(Offsets.ThunkSpritePair, ExtendedSites.DonorThunks.Single(t => t.UsesArtDonor).Addr);
        Assert.DoesNotContain(ExtendedSites.DonorThunks, t => t.Addr == Offsets.ThunkWeaponStat);   // the row stub, not a donor
        Assert.Equal(9, ExtendedSites.DonorThunks.Select(t => t.Addr).Distinct().Count());
    }
}
