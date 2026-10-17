using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>Production <see cref="IIconGlowStore"/> over modDir (glow_icons/, its base_backup/
/// subfolder, and the deployed FFTIVC tree). Every method returns null/false on failure and never
/// throws. Writes go through <see cref="AtomicWrite"/> so a crash cannot leave a torn file.
/// Split from IconGlow.Store.cs, which keeps the interface and DTOs.</summary>
internal sealed class FileIconGlowStore : IIconGlowStore
{
    private readonly string _modDir;

    // Resolved lazily; _pacResolutionFailed latches a failure so a missing install is not re-probed every tick.
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

    /// <summary>Delegates to <see cref="ModdedPac"/>, resolved once per launch so its
    /// offset cache is reused. The cache key is (id, surface), matching <see cref="DeployedPath"/>
    /// and <see cref="BackupPath"/>. Uncached items share one pass via <see cref="ModdedPac.PatchBatch"/>.</summary>
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

        // Key built once per item so the lookup below uses the key the item was searched under.
        var keyed = items.Select(it => (Key: $"{it.Entry.Id}:{it.Entry.Surface}", it.Entry, it.OldBytes, it.NewBytes)).ToList();
        var byKey = pac.PatchBatch(keyed.Select(k => (k.Key, k.OldBytes, k.NewBytes)).ToList());
        foreach (var k in keyed)
            results[(k.Entry.Id, k.Entry.Surface)] = byKey.TryGetValue(k.Key, out var r) ? r : PacPatchResult.Failed;
        return results;
    }

    /// <summary>modDir is <c>&lt;game&gt;\Reloaded\Mods\&lt;modId&gt;</c>, so the game root is three
    /// parents up. That is unproven, since a Reloaded-II install can live outside the game folder. A
    /// null parent or a missing pac latches <see cref="_pacResolutionFailed"/> and later calls return
    /// <see cref="PacPatchResult.Unavailable"/>. Wrapped because path APIs can throw.</summary>
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

    /// <summary>Writes to a temp file, then swaps it in. File.Replace when the destination exists,
    /// else File.Move. The temp file is always cleaned up. Never throws.</summary>
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
