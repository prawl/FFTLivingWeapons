using System;
using System.Text;

namespace LivingWeapon;

/// <summary>Classification of a live 36-byte Attack-command record (geometry in <see cref="AttackRow"/>).
/// <see cref="Foreign"/> is unrelated content such as a reused pool slot, <see cref="Unreadable"/> means the
/// address could not be read. Both mean hands off.</summary>
internal enum AttackRowShape { Vanilla, Ours, Foreign, Unreadable }

/// <summary>What the Attack row shows this tick: <see cref="Named"/> is weapon name plus tier suffix,
/// <see cref="Fist"/> is "Fists" for an unarmed human, <see cref="Vanilla"/> leaves the stock "Attack" text.</summary>
internal enum RowKind { Named, Fist, Vanilla }

/// <summary>The row decision <see cref="AttackRow.ComposeRow"/> returns. <see cref="RowName"/> is
/// null only for <see cref="RowKind.Vanilla"/>.</summary>
internal readonly record struct RowDecision(RowKind Kind, string? RowName);

/// <summary>The first four fields of the live 36-byte Attack-command record (see <see cref="AttackRow"/>).
/// <see cref="AttackRow.IsVanillaShape"/> and <see cref="AttackRow.IsOurShape"/> inspect only these.</summary>
internal readonly record struct AttackRecord(uint NameOff, uint DescOff, uint PoolOff, uint Id)
{
    internal static AttackRecord Parse(byte[] raw) => new(
        BitConverter.ToUInt32(raw, 0),
        BitConverter.ToUInt32(raw, 4),
        BitConverter.ToUInt32(raw, 8),
        BitConverter.ToUInt32(raw, 12));
}

/// <summary>
/// LW-31 stage 3, pure half (docs/TODO.md, 2026-07-06). The Attack row and hover title share one string driven by a
/// JobCommand text-catalog record at <c>LabelAddr - RecordGap</c> (enc==1 census hit, AttackCard.Census.cs): nine u32
/// <c>{nameOff, descOff, poolHeadOff, id, poolHead8Off, 0, 0, 0, ordinal}</c>, vanilla <c>{0x1FC1, 0x1FC8, 0x1FC0, 1, 0x1FC8}</c>.
/// Vanilla nameOff equals <see cref="AttackRow.RecordGap"/> and points at the "Attack" label, which stays untouched as the race-guard anchor.
/// Rename writes "&lt;rowName&gt;\0&lt;tail&gt;" into the desc footprint (LabelAddr+7, <see cref="FootprintChars"/> chars + NUL) and repoints
/// nameOff/descOff at the halves; restore resets the offsets and the flat desc. Runtime half is AttackRow.cs.
/// </summary>
internal sealed partial class AttackRow
{
    /// <summary>Record base = LabelAddr - RecordGap (2026-07-06). Equals <see cref="VanillaNameOff"/>.</summary>
    internal const int RecordGap = 0x1FC1;

    /// <summary>Vanilla nameOff: points at the label itself (record base + this == LabelAddr).</summary>
    internal const uint VanillaNameOff = 0x1FC1;

    /// <summary>Vanilla descOff: just past the label's NUL (record base + this == LabelAddr + 7, the desc footprint start).</summary>
    internal const uint VanillaDescOff = 0x1FC8;

    /// <summary>The record's poolHeadOff field; unchanged by our split shape.</summary>
    internal const uint PoolHeadOff = 0x1FC0;

    /// <summary>The record's id field; unchanged by our split shape.</summary>
    internal const uint RecordId = 1;

    /// <summary>Desc footprint capacity in chars; reuses <see cref="AttackCardText.DefaultBudgetChars"/> (census-measured 73) so the two cannot drift.</summary>
    internal const int FootprintChars = AttackCardText.DefaultBudgetChars;

    /// <summary>Desc footprint byte capacity: <see cref="FootprintChars"/> chars plus one NUL.</summary>
    internal const int FootprintBytes = FootprintChars + 1;

    /// <summary>The record's own byte size: nine u32 fields.</summary>
    internal const int RecordBytes = 36;

    /// <summary>True when all four inspected fields match the census-measured vanilla values.</summary>
    internal static bool IsVanillaShape(AttackRecord rec) =>
        rec.NameOff == VanillaNameOff && rec.DescOff == VanillaDescOff && rec.PoolOff == PoolHeadOff && rec.Id == RecordId;

    /// <summary>True for our split shape: nameOff at the footprint start, descOff strictly past it and within
    /// <see cref="FootprintChars"/> chars (the boundary is legal). poolOff and id stay vanilla.</summary>
    internal static bool IsOurShape(AttackRecord rec) =>
        rec.Id == RecordId && rec.PoolOff == PoolHeadOff && rec.NameOff == VanillaDescOff
        && rec.DescOff > VanillaDescOff && rec.DescOff <= VanillaDescOff + FootprintChars;

    /// <summary>Builds the split-image payload for the desc footprint: ASCII(rowName) NUL ASCII(tail) NUL, zero-padded to 74 bytes.
    /// Throws when rowName+NUL+tail exceeds <see cref="FootprintChars"/>; callers size <paramref name="tail"/> against
    /// <paramref name="rowName"/> first (AttackCardTail.ComposeTail).</summary>
    internal static byte[] BuildImage(string rowName, string tail)
    {
        if (rowName.Length + 1 + tail.Length > FootprintChars)
            throw new ArgumentException(
                $"AttackRow.BuildImage: '{rowName}' (NUL) '{tail}' is {rowName.Length + 1 + tail.Length} chars, over the {FootprintChars}-char footprint.");

        var image = new byte[FootprintBytes];   // zero-initialized: every pad byte is already NUL
        byte[] nameBytes = Encoding.ASCII.GetBytes(rowName);
        byte[] tailBytes = Encoding.ASCII.GetBytes(tail);
        Array.Copy(nameBytes, image, nameBytes.Length);
        Array.Copy(tailBytes, 0, image, nameBytes.Length + 1, tailBytes.Length);
        return image;
    }

    /// <summary>The {nameOff, descOff} pair (little-endian u32 x2) for a split image whose row name is
    /// <paramref name="rowNameChars"/> long: nameOff is <see cref="VanillaDescOff"/>, descOff sits just past the row name's NUL.</summary>
    internal static byte[] OffsetBytes(int rowNameChars)
    {
        var buf = new byte[8];
        BitConverter.GetBytes(VanillaDescOff).CopyTo(buf, 0);
        BitConverter.GetBytes(VanillaDescOff + (uint)(rowNameChars + 1)).CopyTo(buf, 4);
        return buf;
    }

    /// <summary>The vanilla {nameOff, descOff} pair, for Restore.</summary>
    internal static readonly byte[] VanillaOffsetBytes = BuildVanillaOffsetBytes();

    private static byte[] BuildVanillaOffsetBytes()
    {
        var buf = new byte[8];
        BitConverter.GetBytes(VanillaNameOff).CopyTo(buf, 0);
        BitConverter.GetBytes(VanillaDescOff).CopyTo(buf, 4);
        return buf;
    }

    /// <summary>True for a roster SpriteSet byte (Offsets.RSprite) that renders as an ordinary human: below 0x80, 0x80/0x81
    /// (docs/research/SPRITE_SWAP.md), and the guest bodies 0xA2/0xA3/0xA5. False for 0x82 (monster generic, seen 2026-07-06
    /// on a real party monster) and any unrecognized byte, so an unknown sprite never defaults to "Fists".</summary>
    internal static bool HumanSprite(byte s) =>
        s < 0x80 || s == 0x80 || s == 0x81 || s == 0xA2 || s == 0xA3 || s == 0xA5;

    /// <summary>The row decision for one tick. A non-sentinel weapon id found in <paramref name="metaName"/> wins over any sprite
    /// gate and renames the row to the weapon name plus trimmed tier suffix (<see cref="Tuning.Suffix"/> by <see cref="Tuning.TierFor"/>).
    /// An empty-hand sentinel (0, 0xFF, 0xFFFF) on a human sprite gives "Fists" (2026-07-06, plural). Anything else stays
    /// <see cref="RowKind.Vanilla"/>.</summary>
    internal static RowDecision ComposeRow(int rawMainHand, string? metaName, int kills, byte spriteByte)
    {
        bool sentinel = rawMainHand == 0 || rawMainHand == 0xFF || rawMainHand == 0xFFFF;
        if (!sentinel && metaName != null)
        {
            int tier = Tuning.TierFor(kills);
            return new RowDecision(RowKind.Named, metaName + Tuning.Suffix[tier].TrimEnd());
        }
        if (sentinel && HumanSprite(spriteByte))
            return new RowDecision(RowKind.Fist, "Fists");
        return new RowDecision(RowKind.Vanilla, null);
    }
}
