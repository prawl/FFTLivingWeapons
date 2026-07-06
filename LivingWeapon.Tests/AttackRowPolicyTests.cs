using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// AttackRow.Policy.cs is LW-31 stage 3's pure half (docs/TODO.md): the Attack row's RENAME
/// mechanism, live-proven 2026-07-06 (owner eyewitness, screenshot on file). Everything here is
/// pure record/byte/decision logic, no memory access, mirroring the Iai.Policy.cs /
/// Iai.cs split's shape (this class doc names AttackRow.cs as the stateful runtime half that owns
/// every actual read/write). Covers: the 36-byte record shape classification, the split-image byte
/// layout, the offset-pointer encoding, the sprite-based human/monster gate, and the row decision
/// matrix that ties them together with the weapon meta lookup.
/// </summary>
public class AttackRowPolicyTests
{
    // ---- AttackRecord.Parse + shape classification ----

    private static byte[] Record(uint nameOff, uint descOff, uint poolOff, uint id, uint ordinal = 7)
    {
        var buf = new byte[36];
        BitConverter.GetBytes(nameOff).CopyTo(buf, 0);
        BitConverter.GetBytes(descOff).CopyTo(buf, 4);
        BitConverter.GetBytes(poolOff).CopyTo(buf, 8);
        BitConverter.GetBytes(id).CopyTo(buf, 12);
        // +16.. stays zero except the ordinal at +32 (mirrors the live layout's 9th u32); neither
        // IsVanillaShape nor IsOurShape reads past the first four fields.
        BitConverter.GetBytes(ordinal).CopyTo(buf, 32);
        return buf;
    }

    [Fact]
    public void Parse_reads_the_first_four_u32_fields_in_order()
    {
        byte[] raw = Record(0x1FC1, 0x1FC8, 0x1FC0, 1);
        var rec = AttackRecord.Parse(raw);

        Assert.Equal(0x1FC1u, rec.NameOff);
        Assert.Equal(0x1FC8u, rec.DescOff);
        Assert.Equal(0x1FC0u, rec.PoolOff);
        Assert.Equal(1u, rec.Id);
    }

    [Fact]
    public void IsVanillaShape_matches_the_census_proven_vanilla_values()
    {
        var rec = AttackRecord.Parse(Record(0x1FC1, 0x1FC8, 0x1FC0, 1));
        Assert.True(AttackRow.IsVanillaShape(rec));
        Assert.False(AttackRow.IsOurShape(rec));
    }

    [Theory]
    [InlineData(0x1FC0u)]   // nameOff wrong
    [InlineData(0x1FC1u)]   // descOff would collide with nameOff's own vanilla value
    public void IsVanillaShape_rejects_any_field_mismatch(uint wrongDescOff)
    {
        var rec = AttackRecord.Parse(Record(0x1FC1, wrongDescOff, 0x1FC0, 1));
        Assert.False(AttackRow.IsVanillaShape(rec));
    }

    [Fact]
    public void IsOurShape_matches_a_freshly_painted_split_record()
    {
        // "Zwill Straightblade+3" is 21 chars -> descOff = 0x1FC8 + 22.
        var rec = AttackRecord.Parse(Record(0x1FC8, 0x1FC8 + 22, 0x1FC0, 1));
        Assert.True(AttackRow.IsOurShape(rec));
        Assert.False(AttackRow.IsVanillaShape(rec));
    }

    [Fact]
    public void IsOurShape_accepts_the_minimum_one_char_row_name()
    {
        var rec = AttackRecord.Parse(Record(0x1FC8, 0x1FC8 + 2, 0x1FC0, 1));
        Assert.True(AttackRow.IsOurShape(rec));
    }

    [Fact]
    public void IsOurShape_boundary_is_inclusive_at_the_full_73_char_footprint()
    {
        // The longest possible row name leaves room for nothing but its own NUL: descOff at the
        // very top of the (0x1FC8, 0x1FC8+73] range.
        var atBoundary = AttackRecord.Parse(Record(0x1FC8, 0x1FC8 + 73, 0x1FC0, 1));
        var overBoundary = AttackRecord.Parse(Record(0x1FC8, 0x1FC8 + 74, 0x1FC0, 1));

        Assert.True(AttackRow.IsOurShape(atBoundary));
        Assert.False(AttackRow.IsOurShape(overBoundary));
    }

    [Fact]
    public void IsOurShape_rejects_descOff_equal_to_nameOff()
    {
        // descOff must be STRICTLY greater than nameOff (0x1FC8): a row name can never be zero
        // chars (BuildImage guards this too), so descOff==nameOff is not a legal split state.
        var rec = AttackRecord.Parse(Record(0x1FC8, 0x1FC8, 0x1FC0, 1));
        Assert.False(AttackRow.IsOurShape(rec));
    }

    [Theory]
    [InlineData(0x1FC0u, 0x1FC9u, 1u)]     // wrong nameOff
    [InlineData(0x1FC8u, 0x1FC9u, 0u)]     // wrong id
    public void IsOurShape_rejects_wrong_nameOff_or_id(uint nameOff, uint descOff, uint id)
    {
        var rec = AttackRecord.Parse(Record(nameOff, descOff, 0x1FC0, id));
        Assert.False(AttackRow.IsOurShape(rec));
    }

    [Fact]
    public void A_foreign_record_matches_neither_shape()
    {
        var rec = AttackRecord.Parse(Record(0x9999, 0x1234, 0x1FC0, 1));
        Assert.False(AttackRow.IsVanillaShape(rec));
        Assert.False(AttackRow.IsOurShape(rec));
    }

    // ---- BuildImage ----

    [Fact]
    public void BuildImage_lays_out_name_NUL_tail_NUL_padded_to_74_bytes()
    {
        byte[] image = AttackRow.BuildImage("Fist", "Attacks with bare fists.");

        Assert.Equal(74, image.Length);
        Assert.Equal((byte)'F', image[0]);
        Assert.Equal(0, image[4]);   // the separating NUL right after "Fist"
        Assert.Equal((byte)'A', image[5]);
        int tailNulAt = 5 + "Attacks with bare fists.".Length;
        Assert.Equal(0, image[tailNulAt]);
        for (int i = tailNulAt; i < image.Length; i++) Assert.Equal(0, image[i]);   // NUL-padded to the end
    }

    [Fact]
    public void BuildImage_throws_when_the_combined_length_overflows_the_73_char_footprint()
    {
        string name = new string('X', 40);
        string tail = new string('Y', 33);   // 40 + 1 + 33 = 74 > 73
        Assert.Throws<ArgumentException>(() => AttackRow.BuildImage(name, tail));
    }

    [Fact]
    public void BuildImage_accepts_the_boundary_combined_length_of_exactly_73()
    {
        string name = new string('X', 40);
        string tail = new string('Y', 32);   // 40 + 1 + 32 = 73, exactly at the limit
        byte[] image = AttackRow.BuildImage(name, tail);
        Assert.Equal(74, image.Length);
    }

    // ---- OffsetBytes / VanillaOffsetBytes ----

    [Fact]
    public void OffsetBytes_encodes_nameOff_then_descOff_little_endian()
    {
        byte[] bytes = AttackRow.OffsetBytes(rowNameChars: 21);   // "Zwill Straightblade+3"

        Assert.Equal(8, bytes.Length);
        Assert.Equal(0x1FC8u, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(0x1FC8u + 22u, BitConverter.ToUInt32(bytes, 4));
    }

    [Fact]
    public void VanillaOffsetBytes_is_the_census_proven_pair()
    {
        Assert.Equal(8, AttackRow.VanillaOffsetBytes.Length);
        Assert.Equal(0x1FC1u, BitConverter.ToUInt32(AttackRow.VanillaOffsetBytes, 0));
        Assert.Equal(0x1FC8u, BitConverter.ToUInt32(AttackRow.VanillaOffsetBytes, 4));
    }

    // ---- HumanSprite ----

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x02)]   // story body (Ramza/Delita chapter)
    [InlineData(0x16)]   // story body (Mustadio)
    [InlineData(0x1E)]   // story body (Agrias)
    [InlineData(0x7F)]
    [InlineData(0x80)]   // generic male
    [InlineData(0x81)]   // generic female
    [InlineData(0xA2)]   // guest body (Balthier)
    [InlineData(0xA3)]   // guest body (Luso)
    [InlineData(0xA5)]   // guest body (Argath Deathknight)
    public void HumanSprite_true_for_every_known_human_body(int sprite)
    {
        Assert.True(AttackRow.HumanSprite((byte)sprite));
    }

    [Theory]
    [InlineData(0x82)]   // monster: the one generic that must fail closed
    [InlineData(0x83)]
    [InlineData(0x90)]   // unknown, fail closed, never guess human
    [InlineData(0xA4)]
    [InlineData(0xFF)]
    public void HumanSprite_false_for_monster_and_any_unknown_value(int sprite)
    {
        Assert.False(AttackRow.HumanSprite((byte)sprite));
    }

    // ---- ComposeRow decision matrix ----

    private const int WindrunnerId = 501;

    [Theory]
    [InlineData(0, "Windrunner")]
    [InlineData(4, "Windrunner")]
    [InlineData(5, "Windrunner+")]
    [InlineData(24, "Windrunner+")]
    [InlineData(25, "Windrunner+2")]
    [InlineData(49, "Windrunner+2")]
    [InlineData(50, "Windrunner+3")]
    public void ComposeRow_named_weapon_carries_the_trimmed_tier_suffix(int kills, string expectedRowName)
    {
        // Tuning.ProdThresholds {5,25,50}: the test build compiles without LWDEV, so KillThresholds
        // is the production curve (mirrors AttackCardTests' own documented convention).
        var decision = AttackRow.ComposeRow(WindrunnerId, "Windrunner", kills, spriteByte: 0x80);

        Assert.Equal(RowKind.Named, decision.Kind);
        Assert.Equal(expectedRowName, decision.RowName);
    }

    [Fact]
    public void ComposeRow_unknown_weapon_id_is_vanilla()
    {
        var decision = AttackRow.ComposeRow(9999, metaName: null, kills: 3, spriteByte: 0x80);
        Assert.Equal(RowKind.Vanilla, decision.Kind);
        Assert.Null(decision.RowName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xFF)]
    [InlineData(0xFFFF)]
    public void ComposeRow_unarmed_human_sentinel_is_fist(int sentinel)
    {
        var decision = AttackRow.ComposeRow(sentinel, metaName: null, kills: 0, spriteByte: 0x80);
        Assert.Equal(RowKind.Fist, decision.Kind);
        Assert.Equal("Fists", decision.RowName);
    }

    [Theory]
    [InlineData(0x80)]
    [InlineData(0x81)]
    [InlineData(0x02)]   // story body
    public void ComposeRow_unarmed_plus_human_sprite_is_fist(int sprite)
    {
        var decision = AttackRow.ComposeRow(0xFFFF, metaName: null, kills: 0, spriteByte: (byte)sprite);
        Assert.Equal(RowKind.Fist, decision.Kind);
    }

    [Fact]
    public void ComposeRow_unarmed_monster_is_vanilla()
    {
        var decision = AttackRow.ComposeRow(0xFFFF, metaName: null, kills: 0, spriteByte: 0x82);
        Assert.Equal(RowKind.Vanilla, decision.Kind);
        Assert.Null(decision.RowName);
    }

    [Fact]
    public void ComposeRow_unarmed_unknown_sprite_fails_closed_to_vanilla()
    {
        var decision = AttackRow.ComposeRow(0xFF, metaName: null, kills: 0, spriteByte: 0x90);
        Assert.Equal(RowKind.Vanilla, decision.Kind);
    }

    [Fact]
    public void ComposeRow_armed_monster_impossible_case_still_composes_named()
    {
        // Monsters cannot equip in practice; if this ever happens anyway (a garbage RRHand read),
        // an armed resolve always wins over the sprite gate: the gate only ever applies to the
        // EMPTY-hand branch.
        var decision = AttackRow.ComposeRow(WindrunnerId, "Windrunner", kills: 0, spriteByte: 0x82);
        Assert.Equal(RowKind.Named, decision.Kind);
        Assert.Equal("Windrunner", decision.RowName);
    }
}
