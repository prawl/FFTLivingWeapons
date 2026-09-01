using System.Collections.Generic;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>LW-295 cycle B: the pinned bake/runtime manifest schema (schemaVersion 1). One row
/// per (weapon id, surface) -- both the 48px "small" row icon and the 100px "card" icon get their
/// own row. Parsed with Newtonsoft (WeaponMeta.cs's own precedent). Field names are verbatim
/// against tools/bake_glow_icons.py's writer; the two halves cannot drift because both implement
/// to this same shape.</summary>
internal sealed class IconGlowManifest
{
    [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonProperty("tierScales")] public Dictionary<string, double> TierScales { get; set; } = new();
    [JsonProperty("icons")] public List<IconGlowEntry> Icons { get; set; } = new();
}

/// <summary>One managed icon: the weapon id, which surface (card/small), where its deployed
/// vanilla-shape base tex lives (relative to modDir/FFTIVC), and the three glow variants keyed by
/// tier "1".."3" (tier 0 always means the plain base art -- there is no "0" variant file).</summary>
internal sealed class IconGlowEntry
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("surface")] public string Surface { get; set; } = "";
    [JsonProperty("baseRel")] public string BaseRel { get; set; } = "";
    [JsonProperty("length")] public int Length { get; set; }
    [JsonProperty("baseSha1")] public string BaseSha1 { get; set; } = "";
    [JsonProperty("variants")] public Dictionary<string, string> Variants { get; set; } = new();
    [JsonProperty("variantSha1s")] public Dictionary<string, string> VariantSha1s { get; set; } = new();
}

/// <summary>Every file/byte access IconGlow needs, as one seam -- so the whole subsystem is
/// testable with an in-memory fake and the production side is free to fail file-system-shaped
/// ways (missing folder, share violation, a locked file) without ever throwing across this
/// boundary. NO Mem/IGameMemory involvement anywhere: this is plain file I/O against the mod
/// folder (and, for <see cref="PatchPacBatch"/>, the game's own data folder), so nothing here can
/// raise an access violation.
///
/// The runtime reads and writes the DEPLOYED loose tex directly (modDir/FFTIVC/&lt;baseRel&gt;)
/// -- the same files the launch merge re-reads next boot -- plus a pristine-base snapshot store
/// (glow_icons/base_backup/) so a weapon that returns to tier 0 has something plain to restore,
/// since glow_icons/ ships only the tier 1..3 variants and the deployed base gets overwritten in
/// place. modded.pac IS back in this contract (LW-381, 2026-09-01): ledger row
/// [live-icon-repaint] proved, owner-witnessed, that an in-place same-length pac overwrite lands
/// live and an already-drawn icon refreshes on the next list redraw, so the loose-tex write below
/// is now paired with a same-session pac write -- see IconGlow.Apply.cs's class remarks.</summary>
internal interface IIconGlowStore
{
    /// <summary>Reads and parses glow_icons/manifest.json. Null on anything short of a clean
    /// parse (missing file, missing folder, malformed JSON, any I/O error) -- the caller treats
    /// null as "stand down", never distinguishes the reason.</summary>
    IconGlowManifest? ReadManifest();

    /// <summary>Reads the entry's DEPLOYED loose tex (modDir/FFTIVC/&lt;baseRel&gt;) -- whatever
    /// bytes are actually sitting there right now, which may legitimately be the plain base OR a
    /// tier variant this runtime (or an earlier one) already wrote. Null on any failure.</summary>
    byte[]? ReadDeployedTex(IconGlowEntry entry);

    /// <summary>Reads one tier's (1..3) baked glow variant for the entry. Null on any failure or
    /// an unknown tier.</summary>
    byte[]? ReadVariantTex(IconGlowEntry entry, int tier);

    /// <summary>Writes bytes to the entry's DEPLOYED loose tex (modDir/FFTIVC/&lt;baseRel&gt;)
    /// ATOMICALLY -- a torn write must never be possible, since the launch merge reads these
    /// bytes back on the next boot. Returns false on any failure instead of throwing.</summary>
    bool WriteDeployedTex(IconGlowEntry entry, byte[] bytes);

    /// <summary>Reads the entry's pristine-base snapshot from glow_icons/base_backup/, if one
    /// has ever been taken. Null when no snapshot exists yet or on any failure.</summary>
    byte[]? ReadBaseBackup(IconGlowEntry entry);

    /// <summary>Snapshots the entry's verified-pristine bytes to glow_icons/base_backup/
    /// ATOMICALLY, before anything else may overwrite the deployed tex. Returns false on any
    /// failure instead of throwing -- a failed snapshot must refuse the overwrite it would have
    /// guarded, never silently proceed without one.</summary>
    bool WriteBaseBackup(IconGlowEntry entry, byte[] bytes);

    /// <summary>LW-381 R2-2: mirrors a batch of just-written deployed texes into the running
    /// game's modded.pac in as close to ONE pass as possible (ledger [live-icon-repaint], PROVEN
    /// 2026-09-01) -- replaces a round-1 per-surface <c>PatchPac</c> that cost one full pass PER
    /// icon; a batch re-tier (e.g. tools/glow_bounce.py) is 242 managed entries, and one pass per
    /// icon over a 65 MB file is the mass case this batching exists for. Returns one result per
    /// item, keyed by (Id, Surface). Never throws; a non-Patched result is informational only and
    /// must not fail the apply.</summary>
    IReadOnlyDictionary<(int Id, string Surface), PacPatchResult> PatchPacBatch(
        IReadOnlyList<(IconGlowEntry Entry, byte[] OldBytes, byte[] NewBytes)> items);
}

