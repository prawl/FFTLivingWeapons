using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// Per-unit roster-snapshot persistence for Gun Slinger. Keys on roster nameId (u16).
/// Saved as gunslinger.json in the mod directory; atomic write + .bak, mirrors KillTally.
/// Loaded once at construction; Save() is called each time a snapshot changes.
/// </summary>
internal sealed class GunSlingerStore
{
    private readonly string _path;
    private readonly Dictionary<int, GunSlingerSnap> _snaps;

    public GunSlingerStore(string modDir)
    {
        _path = Path.Combine(modDir, "gunslinger.json");
        _snaps = Load(_path);
    }

    /// <summary>Get (or create) the mutable snapshot for a nameId.</summary>
    public GunSlingerSnap Get(int nameId)
    {
        if (!_snaps.TryGetValue(nameId, out var snap))
            _snaps[nameId] = snap = new GunSlingerSnap();
        return snap;
    }

    /// <summary>Atomic save: write tmp; back up prior primary to .bak; move tmp to primary;
    /// then copy the new primary to .bak so the .bak is always the last-known-good copy.
    /// Mirrors KillTally but adds the final bak-copy so a single-save cycle leaves .bak
    /// valid for the fallback Load path. A failed save logs and leaves the prior file intact.</summary>
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
            // Keep .bak in sync so the fallback load path always has a good copy.
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

    // DTO for JSON serialization (avoid exposing GunSlingerSnap's property names directly)
    private sealed class SnapDto
    {
        [JsonProperty("hasOff")]   public bool HasOff   { get; set; }
        [JsonProperty("origOff")]  public int  OrigOff  { get; set; }
        [JsonProperty("hasSupp")]  public bool HasSupp  { get; set; }
        [JsonProperty("origSupp")] public int  OrigSupp { get; set; }

        public static SnapDto From(GunSlingerSnap s) => new()
        {
            HasOff = s.HasOff, OrigOff = s.OrigOff, HasSupp = s.HasSupp, OrigSupp = s.OrigSupp
        };

        public GunSlingerSnap ToSnap() => new()
        {
            HasOff = HasOff, OrigOff = (ushort)OrigOff, HasSupp = HasSupp, OrigSupp = (byte)OrigSupp
        };
    }
}
