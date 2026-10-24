using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// The per-weapon kill tally and its persistence. Saves go tmp -> backup -> move, and a load falls
/// back to the .bak when the primary is missing or corrupt.
/// <see cref="Kills"/> is the one dictionary every subsystem shares by reference, so it is mutated
/// and never replaced after construction.
/// </summary>
internal sealed class KillTally
{
    private readonly string _path;

    /// <summary>The shared id -> kill-count map. Mutate, never replace.</summary>
    public Dictionary<int, int> Kills { get; }

    /// <summary>Where the load came from ("primary", "backup" or "fresh"). Feeds the launch
    /// header's [save] kill-tally line.</summary>
    public string LoadedFrom { get; }

    private KillTally(string path, Dictionary<int, int> kills, string loadedFrom)
    {
        _path = path;
        Kills = kills;
        LoadedFrom = loadedFrom;
    }

    /// <summary>Sum of all counts (the log's "total kills" figure).</summary>
    public int Total
    {
        get
        {
            int s = 0;
            foreach (var v in Kills.Values) s += v;
            return s;
        }
    }

    /// <summary>Loads a weapon's saved kill counts from <paramref name="path"/>, falling back to its .bak, and never crashes on a damaged file. A missing or corrupt pair yields an empty tally, and a backup or fresh result logs a Warning.
    /// A file that is not a JSON object falls through to the .bak (KillsSchema.TryParse), and a well-formed object is sanitized entry by entry so one bad row does not fail the load.</summary>
    public static KillTally Load(string path) => Load(path, File.ReadAllText);

    internal static KillTally Load(string path, Func<string, string> readText)
    {
        var unreadable = new List<string>();
        foreach (var p in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(p)) continue;
                if (!KillsSchema.TryParse(readText(p), out var m, out int dropped)) continue;
                if (dropped > 0)
                    ModLogger.Warn(LogVerb.Save, $"{dropped} malformed kill-tally entries were dropped.");
                bool fromBackup = p.EndsWith(".bak");
                if (fromBackup)
                    ModLogger.Warn(LogVerb.Save, "The kill tally's primary file was missing or corrupt; it was restored from the backup.");
                return new KillTally(path, m, fromBackup ? "backup" : "primary");
            }
            catch (Exception ex)
            {
                unreadable.Add(p);
                ModLogger.Warn(LogVerb.Save, $"The kill tally file {Path.GetFileName(p)} could not be read ({ex.GetType().Name}, {ex.Message}).");
            }
        }
        if (unreadable.Count > 0) WarnUnreadable(unreadable);
        else ModLogger.Warn(LogVerb.Save, "No kill tally was found on disk; starting fresh.");
        return new KillTally(path, new Dictionary<int, int>(), "fresh");
    }

    // If both files exist but cannot be read, the next saves would overwrite the player's real kills, so keep a copy of each first.
    private static void WarnUnreadable(List<string> files)
    {
        var kept = new List<string>();
        foreach (var f in files)
        {
            try
            {
                string dest = f + ".unreadable";
                for (int n = 2; File.Exists(dest); n++) dest = f + ".unreadable." + n;
                File.Copy(f, dest);
                kept.Add(Path.GetFileName(dest));
            }
            catch { QuietFaults.Note("KillTally.KeepUnreadableCopy"); }
        }
        string tail = kept.Count > 0
            ? $"A copy of the old file was kept as {string.Join(" and ", kept)}; with the game closed, rename it back to kills.json to restore your kills."
            : "No copy could be made, so close other programs and restart the game before your next battle, or the old tally will be overwritten.";
        ModLogger.Warn(LogVerb.Save, "Your kill tally file could not be read, maybe another program has it locked, so this session starts with no kills counted. " + tail);
    }

    /// <summary>Persist via the shared <see cref="SidecarJson.SaveAtomic"/> chain. A failed
    /// save logs and leaves the previous file intact.</summary>
    public void Save()
    {
        try
        {
            var outMap = new Dictionary<string, int>(Kills.Count);
            foreach (var kv in Kills) outMap[kv.Key.ToString()] = kv.Value;
            SidecarJson.SaveAtomic(_path, JsonConvert.SerializeObject(outMap));
        }
        catch (Exception ex) { ModLogger.Error(LogVerb.Save, "Failed to save the kill tally to disk: " + ex.Message); }
    }
}
