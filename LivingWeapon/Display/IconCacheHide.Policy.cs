using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// Pure classification and formatting rules for <see cref="IconCacheHide"/>.
/// They use no <see cref="IGameMemory"/>, so they are plain unit tests. Offsets come from LIVE_LEDGER row
/// <c>[icon-cache-hide-reload]</c>: a parts entry is 0x70 bytes (name ptr +0, interned id u32 +8,
/// state u16 +0x58 == 0x0101 once loaded, several entries can share a name); a texture record has
/// name ptr +0, .tex byte size u64 +0x28, path-hash u32 +0x30. The byte-buffer scanners live in
/// IconCacheHide.Scan.cs.
/// </summary>
internal static partial class IconCacheHidePolicy
{
    internal const int PartsHeadSize = 0x60;          // targeted-read size for a parts candidate
    internal const int PartsIdOffset = 0x08;
    internal const int PartsStateOffset = 0x58;
    internal const ushort PartsExpectedState = 0x0101;
    // Bound from probe evidence (lw381_parts_cache_probe.py), not the ledger row.
    internal const uint PartsMaxId = 0x0FFFFFFF;

    internal const int TexHeadSize = 0x40;            // targeted-read size for a texture candidate
    internal const int TexSizeOffset = 0x28;
    internal const int TexHashOffset = 0x30;

    /// <summary>Pass 1's two constant search prefixes.</summary>
    internal static readonly byte[][] Prefixes =
    {
        Encoding.ASCII.GetBytes("UITextureParts://ui/ffto/icon/"),
        Encoding.ASCII.GetBytes("ui/ffto/icon/"),
    };

    // Longest prefix, and the forward-read cap ScanChunkForStrings uses; together they bound how
    // far the pass-1 extended span reaches into the reader's trail slack.
    internal static readonly int PrefixMax = Prefixes.Max(p => p.Length);
    internal const int CStringMax = 128;

    /// <summary>Mirrors the zero-clear literal in ChunkReader.cs's <c>ReadInRegion</c>
    /// (<c>Array.Clear(_buf, readLen, Math.Min(512, ...))</c>). See
    /// <c>IconCacheHideTests.ChunkReaderZeroClearLength_CoversPrefixMaxPlusCStringMax</c>, which
    /// fails if that literal shrinks and stale bytes could match at a region's tail.</summary>
    internal const int ChunkReaderZeroClearLength = ChunkReader.ZeroClearLength;

    /// <summary>Zeroed id/hash field written by a hide.</summary>
    internal static readonly byte[] ZeroU32 = new byte[4];

    /// <summary>The four strings a re-tiered icon's records can point at, id zero-padded to 3
    /// digits.</summary>
    internal static IReadOnlyList<Needle> Needles(int id)
    {
        string n = id.ToString("D3", CultureInfo.InvariantCulture);
        return new[]
        {
            new Needle($"UITextureParts://ui/ffto/icon/equip_item/textureparts/ei_{n}_uitx", "card", NeedleKind.PartsUri),
            new Needle($"ui/ffto/icon/equip_item/texture/ei_{n}_uitx.tex", "card", NeedleKind.TexPath),
            new Needle($"UITextureParts://ui/ffto/icon/equip_item_s/textureparts/ei_s_{n}_uitx", "small", NeedleKind.PartsUri),
            new Needle($"ui/ffto/icon/equip_item_s/texture/ei_s_{n}_uitx.tex", "small", NeedleKind.TexPath),
        };
    }

    /// <summary>Every needle text for every distinct id in <paramref name="targets"/>, mapped
    /// back to (id, surface, kind), built once per <see cref="IconCacheHide.Hide"/> call.</summary>
    internal static Dictionary<string, (int Id, string Surface, NeedleKind Kind)> BuildNeedleLookup(
        IReadOnlyList<IconCacheTarget> targets)
    {
        var map = new Dictionary<string, (int, string, NeedleKind)>();
        foreach (var id in targets.Select(t => t.Id).Distinct())
            foreach (var n in Needles(id))
                map[n.Text] = (id, n.Surface, n.Kind);
        return map;
    }

    /// <summary>Exact match only, no suffix/prefix test <paramref name="cstr"/> is trimmed at its
    /// first NUL and must equal a pending needle. No NUL in the window, or no hit, rejects.</summary>
    internal static (int Id, string Surface, NeedleKind Kind)? IsExpectedString(byte[] cstr,
        IReadOnlyDictionary<string, (int Id, string Surface, NeedleKind Kind)> lookup)
    {
        int nul = Array.IndexOf(cstr, (byte)0);
        if (nul < 0) return null;
        string s = Encoding.ASCII.GetString(cstr, 0, nul);
        return lookup.TryGetValue(s, out var match) ? match : ((int, string, NeedleKind)?)null;
    }

    /// <summary>Returns the interned id (nonzero) on a pass, 0 on a fail.</summary>
    internal static uint ClassifyPartsEntry(byte[] head)
    {
        if (head.Length < PartsHeadSize) return 0;
        uint id = BitConverter.ToUInt32(head, PartsIdOffset);
        ushort state = BitConverter.ToUInt16(head, PartsStateOffset);
        if (id == 0 || id > PartsMaxId || state != PartsExpectedState) return 0;
        return id;
    }

    /// <summary>Returns the path hash (nonzero) on a pass, 0 on a fail. The size must equal the
    /// manifest's byte length, not merely be nonzero, so a same-named decoy or another surface's
    /// texture is rejected.</summary>
    internal static uint ClassifyTexRecord(byte[] head, int expectedLength)
    {
        if (head.Length < TexHeadSize) return 0;
        long size = BitConverter.ToInt64(head, TexSizeOffset);
        uint hash = BitConverter.ToUInt32(head, TexHashOffset);
        if (size != expectedLength || hash == 0) return 0;
        return hash;
    }

    /// <summary>The corrupted name pointer a hide writes. The string's address plus one, so the
    /// record no longer resolves to a live string.</summary>
    internal static byte[] HideNameBytes(long stringAddr) => BitConverter.GetBytes(stringAddr + 1);

    /// <summary>Coupling rule (see <see cref="IconCacheHide"/>'s class remarks): parts entries count
    /// as hidden only after a texture record was hidden. Otherwise the surface reports
    /// <see cref="HideOutcome.TextureMissing"/>, or <see cref="HideOutcome.Refused"/> if a texture
    /// write was attempted and failed. Once <paramref name="texHidden"/> is nonzero a refused write
    /// elsewhere on the surface no longer downgrades it; Refused means no texture record could be
    /// written.</summary>
    internal static SurfaceOutcome DecideOutcome(int partsCandidates, int texCandidates,
        int texHidden, int partsHidden, bool anyTexRefused)
    {
        if (partsCandidates == 0 && texCandidates == 0)
            return new SurfaceOutcome(HideOutcome.NothingResident, 0, 0, 0, 0);
        if (texHidden == 0)
            return new SurfaceOutcome(anyTexRefused ? HideOutcome.Refused : HideOutcome.TextureMissing,
                partsCandidates, 0, texCandidates, 0);
        return new SurfaceOutcome(HideOutcome.Hidden, partsCandidates, partsHidden, texCandidates, texHidden);
    }

    /// <summary>Log-line shapes. <paramref name="refusedAddr"/>/<paramref name="refusedReason"/>
    /// matter only for <see cref="HideOutcome.Refused"/> (the first texture refusal). When the
    /// texture hid but no parts entry did, the line must not promise a redraw. The parts entries
    /// still point at the old texture handle, so the icon keeps its art until they are recreated.</summary>
    internal static string SummaryLine(int id, string surface, SurfaceOutcome o,
        long refusedAddr = 0, string? refusedReason = null) => o.Outcome switch
        {
            HideOutcome.Hidden when o.PartsHidden > 0 =>
                $"IconCacheHide: icon {id} {surface}: {o.PartsHidden} of {o.PartsCandidates} parts entries and {o.TexHidden} of {o.TexCandidates} texture records hidden; the next menu open redraws it.",
            HideOutcome.Hidden =>
                $"IconCacheHide: icon {id} {surface}: {o.PartsHidden} of {o.PartsCandidates} parts entries and {o.TexHidden} of {o.TexCandidates} texture records hidden; the texture is fresh but its menu records were not, so the icon may keep its current art until they are recreated.",
            HideOutcome.NothingResident =>
                $"IconCacheHide: icon {id} {surface}: nothing resident to hide (the icon has not been drawn this session).",
            HideOutcome.TextureMissing =>
                $"IconCacheHide: icon {id} {surface}: no texture record passed to hide; its parts entries were left alone so the icon keeps drawing its current art instead of going blank.",
            HideOutcome.Postponed =>
                $"IconCacheHide: icon {id} {surface}: postponed (a battle is in progress); retried the next time it is safe.",
            HideOutcome.Refused =>
                $"IconCacheHide: icon {id} {surface}: the write at 0x{refusedAddr:X} was refused ({refusedReason}); the icon keeps its current art this session.",
            _ => $"IconCacheHide: icon {id} {surface}: unrecognized outcome.",
        };

    /// <summary>A refused parts write never changes the outcome once a texture record hid, so it
    /// gets a Debug line once per (id, surface) per launch, with its reason (several refused
    /// holders report only the first).</summary>
    internal static string PartsRefusedLine(int id, string surface, long addr, string reason) =>
        $"IconCacheHide: icon {id} {surface}: a menu record write at 0x{addr:X} was refused ({reason}); the texture record for this surface was hidden.";
}
