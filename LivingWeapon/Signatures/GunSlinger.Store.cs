using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// Per-unit roster-snapshot persistence for Gun Slinger, keyed on roster nameId (u16), saved as gunslinger.json
/// in the mod directory. Atomic write + .bak, but the .bak holds the current generation (see Save), unlike
/// the shared SidecarJson.SaveAtomic chain KillTally/LegendStore use. Loaded once; Save on each change.
/// </summary>
internal sealed class GunSlingerStore
{
    private readonly string _path;
    private readonly Dictionary<int, GunSlingerSnap> _snaps;

    public GunSlingerStore(string modDir)
    {
        _path = Path.Combine(modDir, "gunslinger.json");
        _snaps = Load(_path);
        // A key-0 snapshot predates GunSlinger.cs's nameId==0 scan guard and was
        // shared by colliding unseeded roster rows, so it cannot be attributed to a unit. Purge it, warn once, and
        // Save now so the warning does not repeat every launch.
        if (_snaps.Remove(0))
        {
            ModLogger.Warn(LogVerb.Save,
                "A shared gun-slinger gear snapshot (key 0, from an unseeded roster row) was found and discarded. "
                + "Its original-gear record could not be safely attributed to any one unit. The affected unit keeps "
                + "their twin weapon and Dual Wield until re-equipped, which re-snapshots their gear correctly.");
            Save();
        }
    }

    /// <summary>Get (or create) the mutable snapshot for a nameId.</summary>
    public GunSlingerSnap Get(int nameId)
    {
        if (!_snaps.TryGetValue(nameId, out var snap))
            _snaps[nameId] = snap = new GunSlingerSnap();
        return snap;
    }

    /// <summary>Atomic save. Write tmp, move it over the primary, then write the same json to .bak.
    /// The .bak holds the current generation (SidecarJson.SaveAtomic's holds the previous one), so one save
    /// leaves a valid fallback. Not folded onto the shared chain. That would change which generation
    /// the fallback sees. A failed save logs and leaves the prior file intact.</summary>
    public void Save()
    {
        try
        {
            var dto = new Dictionary<string, SnapDto>(_snaps.Count);
            foreach (var kv in _snaps)
                dto[kv.Key.ToString()] = SnapDto.From(kv.Value);
            var json = JsonConvert.SerializeObject(dto);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, true);
            File.WriteAllText(_path + ".bak", json);
        }
        catch (Exception ex) { ModLogger.Error(LogVerb.Save, "Could not save the gun-slinger gear snapshots: " + ex.Message); }
    }

    private static Dictionary<int, GunSlingerSnap> Load(string path)
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(p)) continue;
                var dto = JsonConvert.DeserializeObject<Dictionary<string, SnapDto>>(File.ReadAllText(p));
                if (dto == null) continue;
                var map = new Dictionary<int, GunSlingerSnap>(dto.Count);
                foreach (var kv in dto)
                    if (int.TryParse(kv.Key, out int id)) map[id] = kv.Value.ToSnap();
                return map;
            }
            catch { }
        }
        return new Dictionary<int, GunSlingerSnap>();
    }

    // DTO for JSON serialization
    private sealed class SnapDto
    {
        [JsonProperty("hasOff")] public bool HasOff { get; set; }
        [JsonProperty("origOff")] public int OrigOff { get; set; }
        [JsonProperty("hasSupp")] public bool HasSupp { get; set; }
        [JsonProperty("origSupp")] public int OrigSupp { get; set; }

        public static SnapDto From(GunSlingerSnap s) => new()
        {
            HasOff = s.HasOff,
            OrigOff = s.OrigOff,
            HasSupp = s.HasSupp,
            OrigSupp = s.OrigSupp
        };

        public GunSlingerSnap ToSnap() => new()
        {
            HasOff = HasOff,
            OrigOff = (ushort)OrigOff,
            HasSupp = HasSupp,
            OrigSupp = GunSlingerPolicy.MigrateLegacySupp(OrigSupp)
        };
    }
}
