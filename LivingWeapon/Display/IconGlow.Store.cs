using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>The icon-glow bake/runtime manifest (schemaVersion 1). One row per (weapon id,
/// surface), for both the 48px "small" and 100px "card" icons. Field names match
/// tools/bake_glow_icons.py's writer.</summary>
internal sealed class IconGlowManifest
{
    [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonProperty("tierScales")] public Dictionary<string, double> TierScales { get; set; } = new();
    [JsonProperty("icons")] public List<IconGlowEntry> Icons { get; set; } = new();
}

/// <summary>One managed icon. Tier "1".."3" variants only; tier 0 is the plain base, with no "0" file.
/// <c>BaseRel</c> is relative to modDir/FFTIVC.</summary>
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

/// <summary>Why a loose icon file write failed. Win32Code is the low 16 bits of the exception HResult,
/// so 32 is a sharing violation and 5 is access denied.</summary>
internal sealed record IconGlowWriteError(string ExceptionType, int Win32Code, string Message)
{
    public string Detail => $"{ExceptionType}, Win32 error {Win32Code}, {Flatten(Message)}";

    private static string Flatten(string s) => Regex.Replace(s, @"\s*[\r\n]+\s*", " ").Trim();
}

/// <summary>File and byte access for IconGlow, as a seam so tests can fake it. Plain file I/O, no
/// Mem/IGameMemory. Implementations return null/false on failure instead of throwing.
/// Reads and writes the deployed loose tex (modDir/FFTIVC/&lt;baseRel&gt;), with a pristine snapshot
/// in glow_icons/base_backup/ for tier 0 restores, since glow_icons/ ships only tier 1..3.
/// Since 2026-09-01 the loose-tex write is paired with a same-session modded.pac write
/// (ledger [live-icon-repaint]); see IconGlow.Apply.cs.</summary>
internal interface IIconGlowStore
{
    /// <summary>Reads and parses glow_icons/manifest.json. Null on any failure, which the caller treats
    /// as "stand down".</summary>
    IconGlowManifest? ReadManifest();

    /// <summary>Reads the entry's deployed loose tex (modDir/FFTIVC/&lt;baseRel&gt;). It may hold the
    /// plain base or a tier variant written earlier. Null on failure.</summary>
    byte[]? ReadDeployedTex(IconGlowEntry entry);

    /// <summary>Reads one baked glow variant (tier 1..3). Null on failure or unknown tier.</summary>
    byte[]? ReadVariantTex(IconGlowEntry entry, int tier);

    /// <summary>Atomically writes the entry's deployed loose tex (modDir/FFTIVC/&lt;baseRel&gt;), since the
    /// launch merge reads it next boot. False on failure.</summary>
    bool WriteDeployedTex(IconGlowEntry entry, byte[] bytes);

    /// <summary>Reads the pristine snapshot from glow_icons/base_backup/. Null if none or on failure.</summary>
    byte[]? ReadBaseBackup(IconGlowEntry entry);

    /// <summary>Atomically snapshots verified-pristine bytes to glow_icons/base_backup/ before the deployed
    /// tex is overwritten. False on failure, and the caller must then refuse the overwrite.</summary>
    bool WriteBaseBackup(IconGlowEntry entry, byte[] bytes);

    /// <summary>Set by a failed WriteDeployedTex or WriteBaseBackup, reset to null at the start of each.</summary>
    IconGlowWriteError? LastWriteError { get; }

    /// <summary>Ledger [live-icon-repaint], 2026-09-01. Mirrors a batch of written texes into
    /// the running game's modded.pac in one pass; a per-icon pass over a 65 MB file is too slow when
    /// tools/glow_bounce.py re-tiers all 242 entries. One result per (Id, Surface). Never throws,
    /// and a non-Patched result is informational only.</summary>
    IReadOnlyDictionary<(int Id, string Surface), PacPatchResult> PatchPacBatch(
        IReadOnlyList<(IconGlowEntry Entry, byte[] OldBytes, byte[] NewBytes)> items);
}

