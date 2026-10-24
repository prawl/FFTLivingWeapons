using System;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// Finds the update-safe save directory (Reloaded/User/Mods/&lt;ModId&gt;) and migrates legacy saves into it.
/// Each legacy file (kills.json, legends.json, gunslinger.json) is copied out of the deploy mod dir the first time it is seen.
/// A mod-folder-replace update wipes the deploy dir, while User/Mods survives and already holds Config.json
/// (see Mod.ResolveConfigPath). Migration is copy-only, and members are fail-soft because Engine's constructor
/// builds this and Mod.StartEngine turns a constructor exception into a dead mod.
/// </summary>
internal sealed class SaveLocation
{
    private readonly string _saveDir;
    private readonly string _legacyDir;

    public SaveLocation(string modDir)
    {
        _legacyDir = modDir;
        _saveDir = ResolveSaveDir(modDir);
    }

    /// <summary>The update-safe directory saves now live in (falls back to the deploy mod dir if
    /// the Reloaded root couldn't be resolved).</summary>
    public string SaveDir => _saveDir;

    /// <summary>The path a given save file lives at now (inside <see cref="SaveDir"/>).</summary>
    public string PathFor(string fileName) => Path.Combine(_saveDir, fileName);

    /// <summary>Brings a save from the old install folder into the update-safe save folder so a mod update does not lose it. Copies <paramref name="fileName"/> and its ".bak" from the legacy deploy dir into <see cref="SaveDir"/>; idempotent and fail-soft.
    /// Always returns the <see cref="PathFor"/> path so a caller can pass it straight to a store's Load.</summary>
    public string Migrate(string fileName)
    {
        string dest = PathFor(fileName);
        string legacy = Path.Combine(_legacyDir, fileName);
        try
        {
            bool copied = AtomicCopyIfAbsent(legacy, dest);
            // An orphan .bak still migrates alone, matching KillTally.Load's [primary, .bak] fallback.
            AtomicCopyIfAbsent(legacy + ".bak", dest + ".bak");
            if (copied) ModLogger.Event(LogVerb.Save, $"Migrated {fileName} to the update-safe save folder.");
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Save, $"Failed to migrate {fileName}: {ex.Message}");
        }
        return dest;
    }

    /// <summary>Sets a save aside in an archive folder instead of deleting it. Moves <paramref name="fileName"/> and its ".bak" sibling out of <see cref="SaveDir"/> into "archive" as "&lt;stem&gt;.&lt;N&gt;&lt;ext&gt;", N one past the highest archived index, with File.Move (no overwrite, no File.Delete) so a name collision throws and is swallowed.
    /// An archived name that cannot be parsed makes it bail out with no move, and a missing source is a no-op. Never throws.</summary>
    public void Archive(string fileName)
    {
        try
        {
            string src = PathFor(fileName);
            if (!File.Exists(src)) return;

            string archiveDir = Path.Combine(_saveDir, "archive");
            Directory.CreateDirectory(archiveDir);
            int n = NextArchiveIndex(archiveDir, fileName);

            string ext = Path.GetExtension(fileName);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string dest = Path.Combine(archiveDir, $"{stem}.{n}{ext}");

            File.Move(src, dest, overwrite: false);
            ModLogger.Event(LogVerb.Save, $"Archived the previous {fileName} to {dest}.");

            string srcBak = src + ".bak";
            if (File.Exists(srcBak)) File.Move(srcBak, dest + ".bak", overwrite: false);
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Save, $"Failed to archive {fileName}: {ex.Message}");
        }
    }

    /// <summary>Picks the number for the next archived copy of a save. One past the highest "&lt;stem&gt;.N&lt;ext&gt;" index in <paramref name="archiveDir"/> for <paramref name="fileName"/>'s stem, or 1 when none exist.
    /// Throws on an unparseable index so <see cref="Archive"/> bails out instead of risking a reused index.</summary>
    private static int NextArchiveIndex(string archiveDir, string fileName)
    {
        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string prefix = stem + ".";
        int max = 0;
        foreach (string path in Directory.GetFiles(archiveDir, stem + ".*" + ext))
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(ext, StringComparison.Ordinal))
                throw new FormatException($"Archived file '{name}' does not fit the {stem}.<N>{ext} shape.");
            string indexPart = name.Substring(prefix.Length, name.Length - prefix.Length - ext.Length);
            if (!int.TryParse(indexPart, out int idx) || idx < 0)
                throw new FormatException($"Archived file '{name}' has an unparseable index.");
            if (idx > max) max = idx;
        }
        return max + 1;
    }

    /// <summary>Copies a save without ever leaving a half-written file in place. Copies src to dest when dest is absent and src exists, through a ".migrating.tmp" file so an interrupted copy
    /// cannot block migration forever; a stale tmp is overwritten next time. Returns true if it copied.</summary>
    private static bool AtomicCopyIfAbsent(string src, string dest)
    {
        if (File.Exists(dest) || !File.Exists(src)) return false;
        string tmp = dest + ".migrating.tmp";
        File.Copy(src, tmp, overwrite: true);     // tmp is disposable scratch space
        File.Move(tmp, dest, overwrite: false);    // atomic within a volume; dest is never partial
        return true;
    }

    /// <summary>Finds the update-safe folder the saves live in, with the same walk as Mod.ResolveConfigPath. modDir sits two levels under the Reloaded root (Mods/&lt;ModId&gt;), so the save dir is &lt;root&gt;/User/Mods/&lt;ModId&gt;,
    /// using Mod.ModId because Reloaded keys User/Mods on the manifest id. Any null step or exception falls back to modDir.</summary>
    private static string ResolveSaveDir(string modDir)
    {
        try
        {
            var reloadedRoot = Directory.GetParent(modDir)?.Parent?.FullName;
            if (reloadedRoot != null)
            {
                var dir = Path.Combine(reloadedRoot, "User", "Mods", Mod.ModId);
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
        catch { QuietFaults.Note("SaveLocation.ResolveSaveDir"); /* fall through to modDir */ }
        return modDir;
    }
}
