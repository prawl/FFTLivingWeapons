using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// The per-weapon kill tally and its persistence (atomic write + .bak). The tally is the
/// count the whole runtime keys on; losing it loses the player's progress, hence the
/// paranoia: saves go tmp -> backup -> move, and a load falls back to the .bak when the
/// primary is missing or corrupt.
///
/// <see cref="Kills"/> is the ONE mutable dictionary every subsystem shares by reference
/// (KillTracker credits into it, growth/signatures/display read it) -- the instance must
/// never be replaced after construction, only mutated.
/// </summary>
internal sealed class KillTally
{
    private readonly string _path;

    /// <summary>The shared id -> kill-count map. Mutate, never replace.</summary>
    public Dictionary<int, int> Kills { get; }

    /// <summary>Where the load actually came from: "primary", "backup", or "fresh" (no readable
    /// file). Feeds the launch header's [save] kill-tally line (logging facelift stage 3).</summary>
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

    /// <summary>Load the tally at <paramref name="path"/>, falling back to its .bak; a missing
    /// or corrupt pair yields an empty tally (a fresh install), never a crash. A backup or fresh
    /// outcome logs a Warning (the facelift closed KillTally's old silent-catch consistency gap
    /// against LegendStore.Load, which always reported its corrupt-load fallbacks).</summary>
    public static KillTally Load(string path)
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(p)) continue;
                var d = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(p));
                if (d == null) continue;
                var m = new Dictionary<int, int>(d.Count);
                foreach (var kv in d) if (int.TryParse(kv.Key, out int id)) m[id] = kv.Value;
                bool fromBackup = p.EndsWith(".bak");
                if (fromBackup)
                    ModLogger.Warn(LogVerb.Save, "The kill tally's primary file was missing or corrupt; it was restored from the backup.");
                return new KillTally(path, m, fromBackup ? "backup" : "primary");
            }
            catch { }
        }
        ModLogger.Warn(LogVerb.Save, "No kill tally was found on disk; starting fresh.");
        return new KillTally(path, new Dictionary<int, int>(), "fresh");
    }

    /// <summary>Persist atomically: write a .tmp, back the current file up to .bak, move the
    /// .tmp over the primary. A failed save logs and leaves the previous file intact.</summary>
    public void Save()
    {
        try
        {
            var outMap = new Dictionary<string, int>(Kills.Count);
            foreach (var kv in Kills) outMap[kv.Key.ToString()] = kv.Value;
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(outMap));
            if (File.Exists(_path)) File.Copy(_path, _path + ".bak", true);
            File.Move(tmp, _path, true);
        }
        catch (Exception ex) { ModLogger.Error(LogVerb.Save, "Failed to save the kill tally to disk: " + ex.Message); }
    }
}
