using System;
using System.Collections.Generic;
using System.IO;
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
/// folder, so nothing here can raise an access violation.
///
/// LW-336: modded.pac is gone from this contract. The runtime now reads and writes the DEPLOYED
/// loose tex directly (modDir/FFTIVC/&lt;baseRel&gt;) -- the same files the launch merge
/// re-reads next boot -- plus a pristine-base snapshot store (glow_icons/base_backup/) so a
/// weapon that returns to tier 0 has something plain to restore, since glow_icons/ ships only
/// the tier 1..3 variants and the deployed base gets overwritten in place.</summary>
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
}

/// <summary>Production <see cref="IIconGlowStore"/>: glow_icons/ (including its base_backup/
/// snapshot subfolder) and the deployed FFTIVC tree both live under modDir. Every method is
/// wrapped so a failure anywhere (missing folder, a locked file, a permissions error) surfaces as
/// null/false, never a throw -- IconGlow's whole contract depends on this seam never raising.
/// Both write paths go through <see cref="AtomicWrite"/>: write to a sibling temp file, then swap
/// it into place, so a crash or a share violation mid-write can never leave a torn file for the
/// next launch's merge (or a later ReadBaseBackup) to read back.</summary>
internal sealed class FileIconGlowStore : IIconGlowStore
{
    private readonly string _modDir;

    public FileIconGlowStore(string modDir) => _modDir = modDir;

    public IconGlowManifest? ReadManifest()
    {
        try
        {
            var path = Path.Combine(_modDir, "glow_icons", "manifest.json");
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<IconGlowManifest>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public byte[]? ReadDeployedTex(IconGlowEntry entry)
    {
        try { return File.ReadAllBytes(DeployedPath(entry)); }
        catch { return null; }
    }

    public byte[]? ReadVariantTex(IconGlowEntry entry, int tier)
    {
        try
        {
            if (!entry.Variants.TryGetValue(tier.ToString(), out var file)) return null;
            return File.ReadAllBytes(Path.Combine(_modDir, "glow_icons", file));
        }
        catch { return null; }
    }

    public bool WriteDeployedTex(IconGlowEntry entry, byte[] bytes) => AtomicWrite(DeployedPath(entry), bytes);

    public byte[]? ReadBaseBackup(IconGlowEntry entry)
    {
        try { return File.ReadAllBytes(BackupPath(entry)); }
        catch { return null; }
    }

    public bool WriteBaseBackup(IconGlowEntry entry, byte[] bytes) => AtomicWrite(BackupPath(entry), bytes);

    private string DeployedPath(IconGlowEntry entry) =>
        Path.Combine(_modDir, "FFTIVC", entry.BaseRel.Replace('/', Path.DirectorySeparatorChar));

    private string BackupPath(IconGlowEntry entry) =>
        Path.Combine(_modDir, "glow_icons", "base_backup", Path.GetFileName(entry.BaseRel));

    /// <summary>Write-to-temp-then-swap: never leaves <paramref name="path"/> partially
    /// written. File.Replace is preferred (single atomic filesystem op when the destination
    /// exists); a delete+move fallback covers the cases File.Replace itself can refuse (no
    /// existing destination, cross-volume). The temp file is always cleaned up, success or
    /// failure. Never throws.</summary>
    private static bool AtomicWrite(string path, byte[] bytes)
    {
        string? tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            tmp = null;
            return true;
        }
        catch { return false; }
        finally
        {
            if (tmp != null) { try { File.Delete(tmp); } catch { } }
        }
    }
}
