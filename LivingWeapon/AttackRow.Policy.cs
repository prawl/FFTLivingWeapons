using System;
using System.Text;

namespace LivingWeapon;

/// <summary>Classification of a live 36-byte Attack-command record (see <see cref="AttackRow"/>'s
/// class doc for the full geometry). <see cref="Vanilla"/> and <see cref="Ours"/> are the only two
/// shapes AttackRow.cs is ever willing to write into; <see cref="Foreign"/> (some other, unrelated
/// content, a freed/reused pool slot) and <see cref="Unreadable"/> (the record address itself
/// could not be read) both mean "hands off".</summary>
internal enum AttackRowShape { Vanilla, Ours, Foreign, Unreadable }

/// <summary>What the acting unit's Attack row should show this tick. <see cref="Named"/> carries
/// the renamed row text (weapon name + trimmed tier suffix); <see cref="Fist"/> is the unarmed-human
/// row ("Fists"); <see cref="Vanilla"/> means "no rename": the row and its record stay/return to the
/// stock "Attack" text.</summary>
internal enum RowKind { Named, Fist, Vanilla }

/// <summary>The row decision <see cref="AttackRow.ComposeRow"/> returns. <see cref="RowName"/> is
/// null only for <see cref="RowKind.Vanilla"/>.</summary>
internal readonly record struct RowDecision(RowKind Kind, string? RowName);

/// <summary>The first four fields of the live 36-byte Attack-command record (see
/// <see cref="AttackRow"/>'s class doc): the rest (poolHead8Off, three zero fields, a per-command
/// ordinal) are read by nothing here, <see cref="AttackRow.IsVanillaShape"/> and
/// <see cref="AttackRow.IsOurShape"/> only ever inspect these four.</summary>
internal readonly record struct AttackRecord(uint NameOff, uint DescOff, uint PoolOff, uint Id)
{
    internal static AttackRecord Parse(byte[] raw) => new(
        BitConverter.ToUInt32(raw, 0),
        BitConverter.ToUInt32(raw, 4),
        BitConverter.ToUInt32(raw, 8),
        BitConverter.ToUInt32(raw, 12));
}

/// <summary>
/// LW-31 stage 3's pure half (docs/TODO.md; live-proven 2026-07-06, owner eyewitness with a
/// screenshot on file): the Attack row and its hover-card title share ONE display string, driven by
/// a JobCommand text-catalog RECORD rather than being baked into the "Attack" label bytes
/// themselves. For an enc==1 census hit (AttackCard.Census.cs), that record sits at
/// <c>LabelAddr - RecordGap</c>: nine u32 fields
/// <c>{nameOff, descOff, poolHeadOff, id, poolHead8Off, 0, 0, 0, ordinal}</c>, RECORD-BASE-relative,
/// vanilla values <c>{0x1FC1, 0x1FC8, 0x1FC0, 1, 0x1FC8}</c>. Critically, the vanilla nameOff
/// (0x1FC1) is numerically identical to <see cref="AttackRow.RecordGap"/>: nameOff normally points
/// straight back at the "Attack" label itself (record base + 0x1FC1 == LabelAddr), which is exactly
/// why the label bytes can stay byte-for-byte untouched forever (they remain the race-guard
/// re-verify anchor) while still being the text the vanilla row renders. The rename trick never
/// touches the label: it repoints nameOff into the SAME desc footprint the old stage-2 painter
/// already had write access to (LabelAddr+7, 74-byte capacity = <see cref="FootprintChars"/> chars
/// + 1 NUL), writing the split image "&lt;rowName&gt;\0&lt;tail&gt;" there and repointing
/// nameOff/descOff to the row-name and tail halves respectively. Restore is the mirror: offsets
/// back to vanilla, then the flat vanilla desc text.
///
/// This file is the pure half (record shape math, the split-image byte layout, the sprite-based
/// human/monster gate, and the row decision matrix), mirrors the Iai.Policy.cs / Iai.cs split's
/// shape. <see cref="AttackRow"/>'s runtime half (AttackRow.cs) owns every actual memory
/// read/write.
/// </summary>
internal sealed partial class AttackRow
{
    /// <summary>Record base = LabelAddr - RecordGap (live-proven 2026-07-06). Numerically identical
    /// to <see cref="VanillaNameOff"/>, see the class doc for why that identity is the whole trick.</summary>
    internal const int RecordGap = 0x1FC1;

    /// <summary>Vanilla nameOff: points at the label itself (record base + this == LabelAddr).</summary>
    internal const uint VanillaNameOff = 0x1FC1;

    /// <summary>Vanilla descOff: points at the vanilla desc, immediately past the label's own NUL
    /// (record base + this == LabelAddr + 7 == the desc footprint's start).</summary>
    internal const uint VanillaDescOff = 0x1FC8;

    /// <summary>The record's poolHeadOff field; constant across vanilla and our own split shape
    /// (only nameOff/descOff ever move).</summary>
    internal const uint PoolHeadOff = 0x1FC0;

    /// <summary>The record's id field; constant across vanilla and our own split shape.</summary>
    internal const uint RecordId = 1;

    /// <summary>The desc footprint's text capacity in chars, the same census-proven 73 as
    /// <see cref="AttackCardText.DefaultBudgetChars"/> (reused, not re-declared, so the two facts
    /// can never drift apart).</summary>
    internal const int FootprintChars = AttackCardText.DefaultBudgetChars;

    /// <summary>The desc footprint's full byte capacity: <see cref="FootprintChars"/> text chars
    /// plus one NUL. Both the vanilla flat desc and every split image are exactly this many bytes.</summary>
    internal const int FootprintBytes = FootprintChars + 1;

    /// <summary>The record's own byte size: nine u32 fields.</summary>
    internal const int RecordBytes = 36;

    /// <summary>True when every one of the four inspected fields matches the census-proven vanilla
    /// values exactly.</summary>
    internal static bool IsVanillaShape(AttackRecord rec) =>
        rec.NameOff == VanillaNameOff && rec.DescOff == VanillaDescOff && rec.PoolOff == PoolHeadOff && rec.Id == RecordId;

    /// <summary>True when the record shows OUR OWN split shape: nameOff repointed to the footprint
    /// start (a row name can never be zero chars, so descOff is always STRICTLY past it), descOff
    /// somewhere inside the footprint's own <see cref="FootprintChars"/>-char capacity (inclusive of
    /// the boundary: a row name that consumes the entire footprint bar its own NUL is legal).
    /// poolOff/id are unchanged from vanilla: only the two offset fields ever move.</summary>
    internal static bool IsOurShape(AttackRecord rec) =>
        rec.Id == RecordId && rec.PoolOff == PoolHeadOff && rec.NameOff == VanillaDescOff
        && rec.DescOff > VanillaDescOff && rec.DescOff <= VanillaDescOff + FootprintChars;

    /// <summary>Builds the 74-byte split-image payload written into the desc footprint:
    /// ASCII(rowName) + NUL + ASCII(tail) + NUL, zero-padded to the full 74 bytes (never a stale-byte
    /// palimpsest, every byte of a fresh write is accounted for). Throws when the combined
    /// rowName+NUL+tail length would exceed the <see cref="FootprintChars"/>-char footprint; callers
    /// are expected to have already sized <paramref name="tail"/>'s budget against
    /// <paramref name="rowName"/>'s own length (AttackCardTail.ComposeTail's budget parameter).</summary>
    internal static byte[] BuildImage(string rowName, string tail)
    {
        if (rowName.Length + 1 + tail.Length > FootprintChars)
            throw new ArgumentException(
                $"AttackRow.BuildImage: '{rowName}' (NUL) '{tail}' is {rowName.Length + 1 + tail.Length} chars, over the {FootprintChars}-char footprint.");

        var image = new byte[FootprintBytes];   // zero-initialized: every pad byte is already NUL
        byte[] nameBytes = Encoding.ASCII.GetBytes(rowName);
        byte[] tailBytes = Encoding.ASCII.GetBytes(tail);
        Array.Copy(nameBytes, image, nameBytes.Length);
        // image[nameBytes.Length] stays 0 (the separating NUL).
        Array.Copy(tailBytes, 0, image, nameBytes.Length + 1, tailBytes.Length);
        // image[nameBytes.Length + 1 + tailBytes.Length ..] stays 0 (the tail's own NUL + padding).
        return image;
    }

    /// <summary>The 8-byte {nameOff, descOff} pair (little-endian u32 x2) that repoints the record
    /// at a fresh split image whose row name is <paramref name="rowNameChars"/> chars long: nameOff
    /// always <see cref="VanillaDescOff"/> (the footprint start), descOff just past the row name's
    /// own NUL.</summary>
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

    /// <summary>True for a roster SpriteSet byte (Offsets.RSprite) that renders as an ordinary
    /// human: every story body and both generics render under 0x80 or ARE 0x80/0x81
    /// (docs/research/SPRITE_SWAP.md), plus the three known guest bodies 0xA2/0xA3/0xA5. FALSE for
    /// 0x82 (the monster generic, live-proven 2026-07-06 against a real party monster) and for
    /// anything else this table does not positively recognize, fails CLOSED, because an unknown
    /// sprite earning "Fists" by default would be a silent mis-classification, never a safe guess.</summary>
    internal static bool HumanSprite(byte s) =>
        s < 0x80 || s == 0x80 || s == 0x81 || s == 0xA2 || s == 0xA3 || s == 0xA5;

    /// <summary>The row decision for one tick: a real (non-sentinel) weapon id found in
    /// <paramref name="metaName"/>'s meta lookup always wins (armed beats every sprite gate;
    /// monsters cannot equip, so an "armed monster" is definitionally a resolve artifact, not a
    /// real case to protect against) and renames the row to the weapon's name plus its trimmed tier
    /// suffix (<see cref="Tuning.Suffix"/>, keyed off <see cref="Tuning.TierFor"/>). Otherwise, an
    /// empty-hand sentinel (0, 0xFF, 0xFFFF) on a HUMAN sprite earns "Fists" (owner decision
    /// 2026-07-06, plural). Everything else (an untracked/unknown weapon id, or an empty hand on
    /// a non-human (monster or unrecognized) sprite) is <see cref="RowKind.Vanilla"/>: no rename,
    /// never a guess.</summary>
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
