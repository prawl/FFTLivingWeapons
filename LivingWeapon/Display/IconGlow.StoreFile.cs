using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>Production <see cref="IIconGlowStore"/>. The loose files live under modDir, in glow_icons/, its base_backup/
/// subfolder, and the deployed FFTIVC tree, and are written through <see cref="AtomicWrite"/> so a crash cannot leave a torn
/// file. The pac is patched in place beside the exe through <see cref="ModdedPac"/>. Failure comes back as null, false or a
/// per item result. Split from IconGlow.Store.cs, which keeps the interface and DTOs.</summary>
internal sealed class FileIconGlowStore : IIconGlowStore
{
    private readonly string _modDir;

    // Resolved lazily; _pacResolutionFailed latches a failure so a missing install is not re-probed every tick.
    private ModdedPac? _pac;
    private bool _pacResolutionFailed;
    private readonly Func<string?> _exePath;
    private readonly Func<string, bool> _fileExists;

    public FileIconGlowStore(string modDir) : this(modDir, PacLocator.LiveExePath, File.Exists) { }

    internal FileIconGlowStore(string modDir, Func<string?> exePath, Func<string, bool> fileExists)
    {
        _modDir = modDir;
        _exePath = exePath;
        _fileExists = fileExists;
    }

    public IconGlowWriteError? LastWriteError { get; private set; }

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

    /// <summary>Patches the pac through one <see cref="ModdedPac"/> per launch, so its offset cache
    /// survives between passes. Items are keyed by (id, surface), unlike the loose paths, which key on BaseRel.</summary>
    public IReadOnlyDictionary<(int Id, string Surface), PacPatchResult> PatchPacBatch(
        IReadOnlyList<(IconGlowEntry Entry, byte[] OldBytes, byte[] NewBytes, byte[]? PriorTarget)> items)
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
        var keyed = items.Select(it => (Key: $"{it.Entry.Id}:{it.Entry.Surface}", it.Entry, it.OldBytes, it.NewBytes, it.PriorTarget)).ToList();
        var byKey = pac.PatchBatch(keyed.Select(k => (k.Key, k.OldBytes, k.NewBytes, k.PriorTarget)).ToList());
        foreach (var k in keyed)
            results[(k.Entry.Id, k.Entry.Surface)] = byKey.TryGetValue(k.Key, out var r) ? r : PacPatchResult.Failed;
        return results;
    }

    public IconGlowWriteError? LastPacError(int id, string surface) => _pac?.LastFailure($"{id}:{surface}");

    /// <summary>Finds the pac beside the running exe, else three folders above modDir. A miss is remembered
    /// for the launch, so later batches come back Unavailable at once. A pac found but not opened is retried each batch.</summary>
    private ModdedPac? ResolvePac()
    {
        if (_pac != null) return _pac;
        if (_pacResolutionFailed) return null;

        try
        {
            string? exe;
            try { exe = _exePath(); }
            catch { exe = null; }

            var candidates = PacLocator.Candidates(exe, _modDir);
            if (PacLocator.FirstExisting(candidates, _fileExists) is not { } hit)
            {
                _pacResolutionFailed = true;
                ModLogger.Debug(LogVerb.Display, $"IconGlow found no modded.pac at {PacLocator.Describe(candidates)}.");
                return null;
            }

            ModLogger.EventWithTrace(LogVerb.Display,
                $"IconGlow found modded.pac {hit.Label}.",
                $"IconGlow modded.pac path {hit.Path}");
            _pac = new ModdedPac(hit.Path);
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
    private bool AtomicWrite(string path, byte[] bytes)
    {
        LastWriteError = null;
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
        catch (Exception ex)
        {
            LastWriteError = new IconGlowWriteError(ex.GetType().Name, ex.HResult & 0xFFFF, ex.Message);
            return false;
        }
        finally
        {
            if (tmp != null) { try { File.Delete(tmp); } catch { } }
        }
    }
}
