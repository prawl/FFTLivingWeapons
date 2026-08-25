using System.Collections.Generic;
using System.Diagnostics;
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
/// tier "1".."3" (tier 0 always means the base tex itself -- there is no "0" variant file).</summary>
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
/// ways (missing folder, share violation, a relaunch's fresh pac) without ever throwing across
/// this boundary. NO Mem/IGameMemory involvement anywhere: this is plain file I/O against the mod
/// folder and the game's own modded.pac, so nothing here can raise an access violation.</summary>
internal interface IIconGlowStore
{
    /// <summary>Reads and parses glow_icons/manifest.json. Null on anything short of a clean
    /// parse (missing file, missing folder, malformed JSON, any I/O error) -- the caller treats
    /// null as "stand down", never distinguishes the reason.</summary>
    IconGlowManifest? ReadManifest();

    /// <summary>Reads the entry's DEPLOYED loose base tex (modDir/FFTIVC/&lt;baseRel&gt;) -- the
    /// bytes actually shipped in this build, which is also the needle BuildIndex searches
    /// modded.pac for. Null on any failure.</summary>
    byte[]? ReadBaseTex(IconGlowEntry entry);

    /// <summary>Reads one tier's (1..3) baked glow variant for the entry. Null on any failure or
    /// an unknown tier.</summary>
    byte[]? ReadVariantTex(IconGlowEntry entry, int tier);

    /// <summary>Reads the WHOLE running game's modded.pac (data/enhanced/modded.pac, resolved off
    /// the running process's own module directory -- there is no other runtime game-root
    /// resolver). Null on any failure (unreadable, share violation, path unresolved).</summary>
    byte[]? ReadPac();

    /// <summary>Writes bytes at an absolute offset into modded.pac, opened with
    /// FileShare.ReadWrite (the game holds it open). Returns false on any failure instead of
    /// throwing -- a splice failure degrades that one icon, it never crashes the mod.</summary>
    bool WriteAt(long offset, byte[] bytes);
}

/// <summary>Production <see cref="IIconGlowStore"/>: glow_icons/ and the deployed FFTIVC tree
/// live under modDir; the pac path has no existing runtime resolver to reuse (probes hardcode the
/// Steam path, Mod.cs:67 resolves only modDir) so it is derived here from
/// Process.GetCurrentProcess().MainModule's own directory -- the DLL runs in-process inside
/// fft_enhanced.exe, so that IS the game's install directory. Every method is wrapped so a
/// failure anywhere (missing MainModule, missing folder, a locked file) surfaces as null/false,
/// never a throw -- IconGlow's whole contract depends on this seam never raising.</summary>
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

    public byte[]? ReadBaseTex(IconGlowEntry entry)
    {
        try
        {
            var rel = entry.BaseRel.Replace('/', Path.DirectorySeparatorChar);
            return File.ReadAllBytes(Path.Combine(_modDir, "FFTIVC", rel));
        }
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

    public byte[]? ReadPac()
    {
        try
        {
            var path = ResolvePacPath();
            return path is null ? null : File.ReadAllBytes(path);
        }
        catch { return null; }
    }

    public bool WriteAt(long offset, byte[] bytes)
    {
        try
        {
            var path = ResolvePacPath();
            if (path is null) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch { return false; }
    }

    /// <summary>&lt;game install dir&gt;/data/enhanced/modded.pac (P6/P8's own path). Null if
    /// MainModule is unavailable for any reason -- never throws.</summary>
    private static string? ResolvePacPath()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return null;
            var dir = Path.GetDirectoryName(exe);
            return dir is null ? null : Path.Combine(dir, "data", "enhanced", "modded.pac");
        }
        catch { return null; }
    }
}
