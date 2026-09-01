using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>Production <see cref="IIconGlowStore"/>: glow_icons/ (including its base_backup/
/// snapshot subfolder) and the deployed FFTIVC tree both live under modDir. Every method is
/// wrapped so a failure anywhere (missing folder, a locked file, a permissions error) surfaces as
/// null/false, never a throw -- IconGlow's whole contract depends on this seam never raising.
/// Both write paths go through <see cref="AtomicWrite"/>: write to a sibling temp file, then swap
/// it into place, so a crash or a share violation mid-write can never leave a torn file for the
/// next launch's merge (or a later ReadBaseBackup) to read back.
///
/// Split out of IconGlow.Store.cs (LW-381, 200-line trigger): that file now holds only the
/// contract (<see cref="IIconGlowStore"/>) and the manifest DTOs, a real seam from this file's
/// one production adapter.</summary>
internal sealed class FileIconGlowStore : IIconGlowStore
{
    private readonly string _modDir;

    // Lazily resolved once (see ResolvePac): null/null means "not tried yet", non-null _pac
    // means resolution succeeded, and _pacResolutionFailed latches a failed resolution so a
    // missing/relocated install is never re-probed on every icon, every tick.
    private ModdedPac? _pac;
    private bool _pacResolutionFailed;

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

    /// <summary>LW-381 R2-2: delegates the actual search/write/verify to <see cref="ModdedPac"/>,
    /// which this method resolves and reuses across the whole launch (its offset cache is the
    /// point of reusing one instance). The cache key is per (id, surface) -- the same pairing
    /// <see cref="DeployedPath"/> and <see cref="BackupPath"/> already key on. Everything not
    /// resolved from that cache shares ONE pass over the pac via <see cref="ModdedPac.PatchBatch"/>.</summary>
    public IReadOnlyDictionary<(int Id, string Surface), PacPatchResult> PatchPacBatch(
        IReadOnlyList<(IconGlowEntry Entry, byte[] OldBytes, byte[] NewBytes)> items)
    {
        var results = new Dictionary<(int, string), PacPatchResult>();
        if (items.Count == 0) return results;

        var pac = ResolvePac();
        if (pac == null)
        {
            foreach (var it in items) results[(it.Entry.Id, it.Entry.Surface)] = PacPatchResult.Unavailable;
            return results;
        }

        // Build each item's cache key once and keep it alongside the item, so the lookup below
        // can never drift from the key the triple was actually searched under.
        var keyed = items.Select(it => (Key: $"{it.Entry.Id}:{it.Entry.Surface}", it.Entry, it.OldBytes, it.NewBytes)).ToList();
        var byKey = pac.PatchBatch(keyed.Select(k => (k.Key, k.OldBytes, k.NewBytes)).ToList());
        foreach (var k in keyed)
            results[(k.Entry.Id, k.Entry.Surface)] = byKey.TryGetValue(k.Key, out var r) ? r : PacPatchResult.Failed;
        return results;
    }

    /// <summary>modDir is <c>&lt;game&gt;\Reloaded\Mods\&lt;modId&gt;</c>, so the game root is
    /// three parents up. THIS DERIVATION IS NOT A PROVEN PREMISE (a Reloaded-II install can live
    /// outside the game folder) -- any null parent, or a pac that does not exist at the derived
    /// path, latches <see cref="_pacResolutionFailed"/> so every later call returns
    /// <see cref="PacPatchResult.Unavailable"/> without re-probing the file system. Wrapped like
    /// every other method here: <see cref="DirectoryInfo"/>/<see cref="Path.Combine"/> can throw
    /// on a malformed path, and this class's whole contract is that nothing it does throws
    /// outward.</summary>
    private ModdedPac? ResolvePac()
    {
        if (_pac != null) return _pac;
        if (_pacResolutionFailed) return null;

        try
        {
            var modDirInfo = new DirectoryInfo(_modDir);
            var gameRoot = modDirInfo.Parent?.Parent?.Parent;
            if (gameRoot == null) { _pacResolutionFailed = true; return null; }

            var pacPath = Path.Combine(gameRoot.FullName, "data", "enhanced", "modded.pac");
            if (!File.Exists(pacPath)) { _pacResolutionFailed = true; return null; }

            _pac = new ModdedPac(pacPath);
            return _pac;
        }
        catch { _pacResolutionFailed = true; return null; }
    }

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
