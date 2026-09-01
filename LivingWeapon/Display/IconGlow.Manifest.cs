using System;
using System.Collections.Generic;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// LW-381 (R14, 200-line split): IconGlow.cs is the tick-facing lifecycle (schedule the diff,
/// schedule the apply, schedule a hide-only retry); this file is the OTHER real seam that lived
/// in it -- loading and validating glow_icons/manifest.json once, on first tick, and never again.
/// Manifest load and the tick loop change for entirely different reasons (a new bake vs. a new
/// kill), so splitting them here is a real seam, not line-count evasion.
/// </summary>
internal sealed partial class IconGlow
{
    private void EnsureManifestLoaded()
    {
        if (_manifest != null || _standDown) return;
        var manifest = _store.ReadManifest();
        if (manifest == null) { StandDown("glow_icons/manifest.json is missing or malformed"); return; }
        if (manifest.SchemaVersion != 1) { StandDown($"manifest schemaVersion {manifest.SchemaVersion} is not the supported version 1"); return; }

        var kept = new List<IconGlowEntry>();
        int rejected = 0;
        foreach (var icon in manifest.Icons)
        {
            if (_weaponIds.Contains(icon.Id)) kept.Add(icon);
            else rejected++;
        }
        if (rejected > 0)
            ModLogger.Warn(LogVerb.Display, $"IconGlow's manifest listed {rejected} icon entr{(rejected == 1 ? "y" : "ies")} for weapon ids outside the current build; ignoring them (defense against a stale bake).");

        manifest.Icons = kept;
        RejectBasenameCollisions(kept);
        var byId = new Dictionary<int, List<IconGlowEntry>>();
        foreach (var e in kept)
        {
            if (!byId.TryGetValue(e.Id, out var list)) byId[e.Id] = list = new List<IconGlowEntry>();
            list.Add(e);
        }
        _entriesById = byId;
        _manifest = manifest;
        ModLogger.Debug(LogVerb.Display, $"IconGlow: manifest loaded ({kept.Count} icon entr{(kept.Count == 1 ? "y" : "ies")}) from {_modDir}\\glow_icons.");
    }

    private void StandDown(string reason)
    {
        _standDown = true;
        ModLogger.Warn(LogVerb.Display, $"IconGlow standing down: {reason}. Weapon icons will not glow this launch (data-only failure, never a crash).");
    }

    /// <summary>FIX 5's tripwire: the base-backup store flattens every entry to just its base
    /// tex FILENAME (FileIconGlowStore.BackupPath uses Path.GetFileName, ignoring the rest of
    /// BaseRel), so two DIFFERENT ids whose baked BaseRel resolves to the same filename would
    /// silently cross-wire each other's pristine-base snapshots. Zero collisions exist in the
    /// real 242-entry manifest today; this guards a future bake rename from ever reaching the
    /// store. Marks every id in a colliding group unmanaged, once, with one warn naming the
    /// filename and every id it collided under -- never a per-icon repeat.</summary>
    private void RejectBasenameCollisions(List<IconGlowEntry> kept)
    {
        var idsByFilename = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in kept)
        {
            var name = Path.GetFileName(e.BaseRel);
            if (!idsByFilename.TryGetValue(name, out var ids)) idsByFilename[name] = ids = new List<int>();
            if (!ids.Contains(e.Id)) ids.Add(e.Id);
        }

        foreach (var (filename, ids) in idsByFilename)
        {
            if (ids.Count < 2) continue;
            foreach (var id in ids) _unmanaged.Add(id);
            ModLogger.Warn(LogVerb.Display,
                $"IconGlow leaves icons {string.Join(", ", ids)} plain this launch: they share the base tex filename \"{filename}\" (a bake bug; the backup store cannot tell them apart).");
        }
    }
}
