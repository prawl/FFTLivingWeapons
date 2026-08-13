using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>One monster job's Poach carcass drop pair: the common and rare inventory item keys +
/// display names poach.json ships for that job id.</summary>
internal readonly record struct PoachCarcass(int CommonKey, string CommonName, int RareKey, string RareName);

/// <summary>
/// Loads LivingWeapon/poach.json (LW-167; tools/extract_poach_map.py's committed output --
/// the Job sheet's own Unknown10/Unknown11 columns carry each MONSTER JOB ID's PoachItem
/// common/rare {key, name} directly, keyed by job id) from the mod directory, mirroring
/// MetaLoader/GunSlingerStore's modDir-rooted load pattern.
///
/// LW-167 (2026-08-12): the map used to be keyed by a derived "species index" (victim job byte -
/// 95); a live pass falsified that arithmetic (a Black Chocobo at job 95 was refused) and it is
/// gone. Map-membership BY JOB ID is now the entire monster gate -- human jobs simply have no
/// entry.
///
/// DISARM ON FAILURE, NOT CRASH-OR-LIMP: a missing or corrupt file leaves <see cref="IsLoaded"/>
/// false and every <see cref="TryGetJob"/> lookup false -- LivingPoach checks this before ever
/// consulting the map, so a broken deploy quietly refuses to poach against garbage data rather
/// than crash or fabricate a drop. Exactly ONE Warning is logged (the map loads once at
/// construction and never reloads mid-session, so there is no risk of this spamming the tick loop).
/// </summary>
internal sealed class PoachMap
{
    private readonly Dictionary<int, PoachCarcass> _jobs = new();

    /// <summary>True when poach.json parsed cleanly and carried at least one usable job entry.</summary>
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

    /// <summary>Look up one monster job's carcass pair by its job id. False (and a default record)
    /// when the map never loaded, or this job has no entry (including every human job).</summary>
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
