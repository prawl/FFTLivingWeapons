using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>One monster job's Poach carcass drop pair (common and rare item keys and names) from poach.json.</summary>
internal readonly record struct PoachCarcass(int CommonKey, string CommonName, int RareKey, string RareName);

/// <summary>
/// Loads LivingWeapon/poach.json from the mod directory (LW-167, output of tools/extract_poach_map.py). The
/// Job sheet's Unknown10/Unknown11 columns carry each monster job id's common/rare PoachItem {key, name},
/// so the map is keyed by job id. Human jobs have no entry, which makes membership the whole monster gate.
/// A live pass refused a Black Chocobo at job 95, falsifying the old job byte - 95
/// species index.
///
/// A missing or corrupt file leaves <see cref="IsLoaded"/> false and every <see cref="TryGetJob"/> false,
/// so LivingPoach declines to poach instead of crashing or inventing a drop. One Warning is logged.
/// </summary>
internal sealed class PoachMap
{
    private readonly Dictionary<int, PoachCarcass> _jobs = new();

    /// <summary>True when poach.json parsed and carried at least one usable job entry.</summary>
    public bool IsLoaded { get; }

    public PoachMap(string modDir)
    {
        try
        {
            string path = Path.Combine(modDir, "poach.json");
            var doc = JsonConvert.DeserializeObject<PoachDoc>(File.ReadAllText(path));
            if (doc?.Jobs == null) throw new InvalidDataException("poach.json has no \"jobs\" table");

            foreach (var kv in doc.Jobs)
            {
                if (!int.TryParse(kv.Key, out int jobId)) continue;
                var e = kv.Value;
                if (e?.Common == null || e.Rare == null) continue;
                _jobs[jobId] = new PoachCarcass(e.Common.Key, e.Common.Name ?? "", e.Rare.Key, e.Rare.Name ?? "");
            }
            if (_jobs.Count == 0) throw new InvalidDataException("poach.json parsed but every job entry was malformed");

            IsLoaded = true;
        }
        catch (Exception)
        {
            IsLoaded = false;
            _jobs.Clear();
            ModLogger.Warn(LogVerb.Signature, "Living Poach is disarmed: the poach map file is missing or unreadable.");
        }
    }

    /// <summary>Looks up a monster job's carcass pair. False when the map never loaded or the job has no entry.</summary>
    public bool TryGetJob(int jobId, out PoachCarcass carcass) => _jobs.TryGetValue(jobId, out carcass);

    private sealed class PoachDoc
    {
        [JsonProperty("jobs")] public Dictionary<string, PoachJobDto>? Jobs { get; set; }
    }

    private sealed class PoachJobDto
    {
        [JsonProperty("common")] public PoachEntryDto? Common { get; set; }
        [JsonProperty("rare")] public PoachEntryDto? Rare { get; set; }
    }

    private sealed class PoachEntryDto
    {
        [JsonProperty("key")] public int Key { get; set; }
        [JsonProperty("name")] public string? Name { get; set; }
    }
}
