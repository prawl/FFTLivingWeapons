using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// One weapon's Reliquary deed record: the last victim, per-archetype kill counts (Unknown is
/// counted but never earns a Mark), and the Marks earned in order. Legends and PendingAnnounce are
/// Phase 2 slots kept in the schema now to avoid a later migration (decision 10). LastPainted is
/// the previous composed card line, kept so a relaunch can still recognize a buffer painted
/// before the last rotation (decision 12).</summary>
internal sealed class WeaponLegend
{
    public ushort LastVictimNameId;
    public byte LastVictimJob;
    /// <summary>(int)VictimClass.Archetype of the last victim, or -1 when no deed is recorded yet.</summary>
    public int LastVictimCls = -1;
    public readonly int[] Counts = new int[LegendStore.ArchetypeSlots];
    public List<int> Marks = new();
    public List<string> Legends = new();            // Phase 2 slot (decision 10)
    public List<string> PendingAnnounce = new();     // Phase 2 slot
    public string? LastPainted;
}

/// <summary>
/// Reliquary Phase 1 deed ledger (docs/RELIQUARY_AC.md), stored in legends.json beside kills.json.
/// Saves follow KillTally's prior-copy-to-.bak order (KillTally.cs:64-76). Unlike KillTally, a
/// corrupt load also logs a warning and a flight record, as the AC requires.
/// </summary>
internal sealed class LegendStore
{
    /// <summary>Width of <see cref="WeaponLegend.Counts"/>: one slot per VictimClass.Archetype, Unknown included.</summary>
    internal const int ArchetypeSlots = 5;

    private readonly string _path;
    private readonly Dictionary<int, WeaponLegend> _legends;
    private bool _dirty;

    /// <summary>Where the load came from: "primary", "backup", or "fresh". Feeds the launch header's [save] legends line.</summary>
    public string LoadedFrom { get; }

    /// <summary>Weapons with at least one recorded deed.</summary>
    public int WeaponCount => _legends.Count;

    /// <summary>Total Marks earned across all weapons.</summary>
    public int TotalMarks
    {
        get
        {
            int total = 0;
            foreach (var w in _legends.Values) total += w.Marks.Count;
            return total;
        }
    }

    private LegendStore(string path, Dictionary<int, WeaponLegend> legends, string loadedFrom)
    {
        _path = path;
        _legends = legends;
        LoadedFrom = loadedFrom;
    }

    /// <summary>Loads legends.json from modDir, then .bak, then an empty store; never throws. A corrupt
    /// file logs a warning and a Flight.Record tap.</summary>
    public static LegendStore Load(string modDir)
    {
        string path = Path.Combine(modDir, "legends.json");
        foreach (var p in new[] { path, path + ".bak" })
        {
            if (!File.Exists(p)) continue;
            try
            {
                var dto = JsonConvert.DeserializeObject<Dictionary<string, WeaponLegendDto>>(File.ReadAllText(p));
                if (dto == null) continue;
                var map = new Dictionary<int, WeaponLegend>(dto.Count);
                foreach (var kv in dto)
                    if (int.TryParse(kv.Key, out int id)) map[id] = kv.Value.ToLegend();
                return new LegendStore(path, map, p.EndsWith(".bak") ? "backup" : "primary");
            }
            catch (Exception ex)
            {
                string which = p.EndsWith(".bak") ? "backup" : "primary";
                ModLogger.Warn(LogVerb.Save, $"The legends file's {which} copy at {p} is corrupt; falling back: {ex.Message}");
                Flight.Record("legend-store", $"corrupt-load which={which} path={p}: {ex.Message}");
            }
        }
        return new LegendStore(path, new Dictionary<int, WeaponLegend>(), "fresh");
    }

    /// <summary>True if this weapon has ever had a deed recorded.</summary>
    public bool Has(int weaponId) => _legends.ContainsKey(weaponId);

    /// <summary>lets <see cref="TallyMigration.MoveLegends"/> move deeds when an item id
    /// changes. Marks the store dirty and returns how many old ids moved.</summary>
    public int Migrate(IReadOnlyDictionary<int, int> plan)
    {
        int moved = TallyMigration.MoveLegends(plan, _legends);
        if (moved > 0) _dirty = true;
        return moved;
    }

    /// <summary>Read view for the composer (CardLine/StoryLines). Returns an empty WeaponLegend, never null, for a weapon with no deeds.</summary>
    public WeaponLegend Get(int weaponId) =>
        _legends.TryGetValue(weaponId, out var w) ? w : new WeaponLegend();

    /// <summary>Records one kill: classifies the victim, sets lastVictim, and bumps that archetype's
    /// count. Returns archetypes that first crossed Tuning.MarkThresholds[0] on this kill.</summary>
    public List<VictimClass.Archetype> RecordDeed(int weaponId, VictimSnapshot victim)
    {
        if (!_legends.TryGetValue(weaponId, out var w))
            _legends[weaponId] = w = new WeaponLegend();

        var cls = VictimClass.Classify(victim.Job, victim.Undead);
        w.LastVictimNameId = victim.NameId;
        w.LastVictimJob = victim.Job;
        w.LastVictimCls = (int)cls;

        int idx = (int)cls;
        w.Counts[idx]++;

        var earned = new List<VictimClass.Archetype>();
        if (cls != VictimClass.Archetype.Unknown
            && w.Counts[idx] >= Tuning.MarkThresholds[0]
            && !w.Marks.Contains(idx))
        {
            w.Marks.Add(idx);
            earned.Add(cls);
        }

        _dirty = true;
        return earned;
    }

    /// <summary>Decision 12: stores the previous composed card line ("lastPainted"). StoryLines calls
    /// it on a compose-change edge, not at paint time.</summary>
    public void RotatePainted(int weaponId, string? previous)
    {
        if (!_legends.TryGetValue(weaponId, out var w))
            _legends[weaponId] = w = new WeaponLegend();
        w.LastPainted = previous;
        _dirty = true;
    }

    /// <summary>Saves via <see cref="SidecarJson.SaveAtomic"/> when dirty. A failed save logs,
    /// keeps the old primary and .bak, and stays dirty for a retry. Never throws, since it runs on
    /// Engine's tick thread.</summary>
    public void SaveIfDirty()
    {
        if (!_dirty) return;
        try
        {
            var dto = new Dictionary<string, WeaponLegendDto>(_legends.Count);
            foreach (var kv in _legends) dto[kv.Key.ToString()] = WeaponLegendDto.From(kv.Value);
            SidecarJson.SaveAtomic(_path, JsonConvert.SerializeObject(dto));
            _dirty = false;
        }
        catch (Exception ex) { ModLogger.Error(LogVerb.Save, "Failed to save weapon legends to disk: " + ex.Message); }
    }
}

// JSON DTOs, like GunSlingerStore's SnapDto, keep the wire schema apart from the runtime field names.

internal sealed class LastVictimDto
{
    [JsonProperty("nameId")] public int NameId { get; set; }
    [JsonProperty("job")] public int Job { get; set; }
    [JsonProperty("cls")] public int Cls { get; set; }
}

internal sealed class WeaponLegendDto
{
    [JsonProperty("lastVictim")] public LastVictimDto? LastVictim { get; set; }
    [JsonProperty("counts")] public int[]? Counts { get; set; }
    [JsonProperty("marks")] public List<int>? Marks { get; set; }
    [JsonProperty("legends")] public List<string>? Legends { get; set; }
    [JsonProperty("pendingAnnounce")] public List<string>? PendingAnnounce { get; set; }
    [JsonProperty("lastPainted")] public string? LastPainted { get; set; }

    public static WeaponLegendDto From(WeaponLegend w) => new()
    {
        LastVictim = w.LastVictimCls < 0 ? null : new LastVictimDto
        {
            NameId = w.LastVictimNameId,
            Job = w.LastVictimJob,
            Cls = w.LastVictimCls,
        },
        Counts = w.Counts,
        Marks = w.Marks,
        Legends = w.Legends,
        PendingAnnounce = w.PendingAnnounce,
        LastPainted = w.LastPainted,
    };

    public WeaponLegend ToLegend()
    {
        var w = new WeaponLegend();
        if (LastVictim != null)
        {
            w.LastVictimNameId = (ushort)LastVictim.NameId;
            w.LastVictimJob = (byte)LastVictim.Job;
            w.LastVictimCls = LastVictim.Cls;
        }
        if (Counts != null)
        {
            int n = Math.Min(Counts.Length, LegendStore.ArchetypeSlots);
            Array.Copy(Counts, w.Counts, n);
        }
        if (Marks != null) w.Marks = new List<int>(Marks);
        if (Legends != null) w.Legends = new List<string>(Legends);
        if (PendingAnnounce != null) w.PendingAnnounce = new List<string>(PendingAnnounce);
        w.LastPainted = LastPainted;
        return w;
    }
}
