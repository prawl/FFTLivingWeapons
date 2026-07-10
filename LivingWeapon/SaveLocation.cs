using System;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// LW-51: resolves the update-safe save directory (Reloaded/User/Mods/&lt;ModId&gt;) and runs a
/// one-time, strictly non-destructive migration of each legacy save file (kills.json,
/// legends.json, gunslinger.json) out of the deploy mod dir the first time it is seen there.
/// A plain mod-folder-replace update wipes the deploy dir; the save dir survives because
/// Reloaded never touches User/Mods on an update (the same directory Config.json already lives
/// in, per Mod.ResolveConfigPath).
///
/// The migration is copy-only (never delete, never overwrite): a pre-existing dest file is left
/// completely alone, and the legacy source is left in place too, so nothing is ever destroyed.
/// Every public member is fail-soft (never throws), because SaveLocation is constructed inside
/// Engine's constructor, and Mod.StartEngine's catch turns any ctor exception into a dead mod.
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

    /// <summary>
    /// One-time non-destructive migration of <paramref name="fileName"/> (and its independent
    /// ".bak" sibling) from the legacy deploy dir into <see cref="SaveDir"/>. Idempotent: once a
    /// dest file exists, later calls are a no-op for that file. Fail-soft: any exception is
    /// logged and swallowed, never thrown. Always returns <see cref="PathFor"/>'s path for
    /// <paramref name="fileName"/>, migrated or not, so a caller can pass the result straight to
    /// a store's Load without checking the outcome.
    /// </summary>
    public string Migrate(string fileName)
    {
        string dest = PathFor(fileName);
        string legacy = Path.Combine(_legacyDir, fileName);
        try
        {
            bool copied = AtomicCopyIfAbsent(legacy, dest);
            // Independent guard: an orphan .bak (legacy primary missing, e.g. a corrupt-primary
            // recovery mid-flight) still migrates on its own, matching KillTally.Load's own
            // [primary, .bak] fallback chain.
            AtomicCopyIfAbsent(legacy + ".bak", dest + ".bak");
            if (copied) ModLogger.Event(LogVerb.Save, $"Migrated {fileName} to the update-safe save folder.");
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Save, $"Failed to migrate {fileName}: {ex.Message}");
        }
        return dest;
    }

    /// <summary>
    /// LW-51 Tier-1: strictly non-destructive archive of <paramref name="fileName"/> (and its
    /// independent ".bak" sibling) out of <see cref="SaveDir"/>, into a "archive" subdirectory,
    /// as "&lt;stem&gt;.&lt;N&gt;&lt;ext&gt;" where N is one past the highest existing archived
    /// index for that stem. Uses File.Move(..., overwrite: false) ONLY: a name collision throws
    /// and is swallowed by the fail-soft catch below, never clobbering an existing archived file;
    /// no File.Delete anywhere. FAIL-CLOSED: if an existing archived name for this stem cannot be
    /// parsed back to its index, this bails out entirely (no move at all) rather than guessing a
    /// starting index that name might already secretly claim. A no-op when the primary source
    /// file does not exist (nothing to archive). Fail-soft: never throws.
    /// </summary>
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

    /// <summary>One past the highest existing "&lt;stem&gt;.N&lt;ext&gt;" index already in
    /// <paramref name="archiveDir"/> for <paramref name="fileName"/>'s stem, or 1 when none exist.
    /// Throws (so <see cref="Archive"/>'s catch fails the whole call closed) if any matching file
    /// name's index segment cannot be parsed (guessing past an unparseable existing name risks
    /// re-using an index that name secretly already claims).</summary>
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

    /// <summary>
    /// Copies src to dest only when dest is absent and src exists; otherwise a no-op. Copies
    /// through a scratch ".migrating.tmp" file first, then moves that over dest, so an
    /// interrupted copy (crash, disk full, process kill mid-write) never leaves a partial dest
    /// behind: a partial dest would satisfy the "dest absent" guard forever and permanently block
    /// migrating the still-intact legacy file. A stale leftover ".migrating.tmp" from a prior
    /// interrupted attempt is simply overwritten by the next attempt's copy (self-healing).
    /// Non-destructive: an existing dest is never opened, read, or touched. Returns true iff it
    /// copied.
    /// </summary>
    private static bool AtomicCopyIfAbsent(string src, string dest)
    {
        if (File.Exists(dest) || !File.Exists(src)) return false;
        string tmp = dest + ".migrating.tmp";
        File.Copy(src, tmp, overwrite: true);     // tmp is disposable scratch space
        File.Move(tmp, dest, overwrite: false);    // atomic within a volume; dest is never partial
        return true;
    }

    /// <summary>
    /// Mirrors Mod.ResolveConfigPath's walk but resolves a directory: modDir is assumed to be
    /// two levels under the Reloaded root (Mods/&lt;ModId&gt;), so the root is
    /// modDir's grandparent, and the save dir is &lt;root&gt;/User/Mods/&lt;ModId&gt;. Uses the
    /// manifest ModId constant (Mod.ModId), not modDir's own folder name: Reloaded keys
    /// User/Mods on the manifest id, and that is where Config.json already lives. Any null step
    /// or exception falls back to modDir unchanged (never throws).
    /// </summary>
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
        catch { /* fall through to modDir */ }
        return modDir;
    }
}
