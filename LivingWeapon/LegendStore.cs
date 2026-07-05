using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// One weapon's Reliquary deed record: the most recent victim, per-archetype kill counts
/// (Unknown counted too, but never earns a Mark), the archetype indices earned as Marks (in
/// earn order), and two Phase 2 storage-only slots (legends, pendingAnnounce -- decision 10:
/// the compose branch is descoped to Phase 2, but the schema carries the slot now so Phase 2
/// needs no breaking migration). LastPainted is decision 12's anchor-rotation state: the
/// PREVIOUS distinct composed card line, persisted so a relaunch's three-way anchor can still
/// recognize a buffer painted before the last rotation.</summary>
internal sealed class WeaponLegend
{
    public ushort LastVictimNameId;
    public byte LastVictimJob;
    /// <summary>(int)VictimClass.Archetype of the last victim, or -1 when no deed is recorded yet.</summary>
    public int LastVictimCls = -1;
    public readonly int[] Counts = new int[LegendStore.ArchetypeSlots];
    public List<int> Marks = new();
    public List<string> Legends = new();            // Phase 2 storage slot only (decision 10)
    public List<string> PendingAnnounce = new();     // Phase 2 plumbing only
    public string? LastPainted;
}

/// <summary>
/// Reliquary Phase 1 deed ledger (docs/RELIQUARY_AC.md): legends.json, sibling of kills.json.
/// Persistence mirrors KillTally's exact prior-copy-to-.bak save ordering (KillTally.cs:64-76)
/// -- legends are permanent facts, so the prior-generation fallback is the right trade -- PLUS a
/// corrupt-load warning + flight record KillTally's own silent catch lacks (the AC requires
/// evidence of a corrupt load, not just a quiet fallback).
/// </summary>
internal sealed class LegendStore
{
    /// <summary>Fixed width of <see cref="WeaponLegend.Counts"/> -- one slot per VictimClass.Archetype
    /// value (including Unknown).</summary>
    internal const int ArchetypeSlots = 5;

    private readonly string _path;
    private readonly Dictionary<int, WeaponLegend> _legends;
    private bool _dirty;

    private LegendStore(string path, Dictionary<int, WeaponLegend> legends)
    {
        _path = path;
        _legends = legends;
    }

    /// <summary>Load legends.json at modDir, falling back to .bak, then an empty store (fresh
    /// install) -- never throws. Mirrors KillTally.Load's [path, .bak, empty] chain, but a
    /// corrupt file logs one ModLogger.LogWarning line + one Flight.Record tap (KillTally's own
    /// catch is silent -- do not copy that here).</summary>
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
                return new LegendStore(path, map);
            }
            catch (Exception ex)
            {
                string which = p.EndsWith(".bak") ? "backup" : "primary";
                ModLogger.LogWarning($"legend-store: corrupt {which} at {p} -- falling back -- {ex.Message}");
                Flight.Record("legend-store", $"corrupt-load which={which} path={p} -- {ex.Message}");
            }
        }
        return new LegendStore(path, new Dictionary<int, WeaponLegend>());
    }

    /// <summary>True if this weapon has ever had a deed recorded.</summary>
    public bool Has(int weaponId) => _legends.ContainsKey(weaponId);

    /// <summary>Read view for the composer (CardLine/StoryLines): the weapon's current deed
    /// state, or a fresh (empty) WeaponLegend for a weapon with no deeds yet -- never null, so
    /// callers don't need a separate Has() guard just to read defaults.</summary>
    public WeaponLegend Get(int weaponId) =>
        _legends.TryGetValue(weaponId, out var w) ? w : new WeaponLegend();

    /// <summary>Record one kill's deed: classifies the victim (VictimClass.Classify), updates
    /// lastVictim to this (the MOST RECENT) kill, and increments that archetype's count. Returns
    /// every archetype that just crossed Tuning.MarkThresholds[0] for the FIRST time (a mark
    /// already earned is never re-returned; Unknown never earns a Mark, per Classify's contract).
    /// Marks the store dirty.</summary>
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

    /// <summary>Decision 12 (the anchor-rotation rule): persist the PREVIOUS distinct composed
    /// card line for a weapon ("lastPainted" in the schema) -- called by StoryLines ONLY on a
    /// compose-change edge, never at paint time. Marks the store dirty.</summary>
    public void RotatePainted(int weaponId, string? previous)
    {
        if (!_legends.TryGetValue(weaponId, out var w))
            _legends[weaponId] = w = new WeaponLegend();
        w.LastPainted = previous;
        _dirty = true;
    }

    /// <summary>Persist iff something changed since the last save -- mirrors KillTally.Save's
    /// exact ordering (tmp -> prior-copy-to-.bak -> move). A failed save logs and leaves the
    /// previous primary+.bak intact; dirty stays set so the next save edge retries. Never throws
    /// (this runs on Engine's tick thread).</summary>
    public void SaveIfDirty()
    {
        if (!_dirty) return;
        try
        {
            var dto = new Dictionary<string, WeaponLegendDto>(_legends.Count);
            foreach (var kv in _legends) dto[kv.Key.ToString()] = WeaponLegendDto.From(kv.Value);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(dto));
            if (File.Exists(_path)) File.Copy(_path, _path + ".bak", true);
            File.Move(tmp, _path, true);
            _dirty = false;
        }
        catch (Exception ex) { ModLogger.LogError("legend-store: failed to save deeds to disk -- " + ex.Message); }
    }
}

// ---- JSON DTOs (mirrors GunSlingerStore's SnapDto idiom: keep the wire schema decoupled from
// the mutable runtime type's field names) ----

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
            NameId = w.LastVictimNameId, Job = w.LastVictimJob, Cls = w.LastVictimCls,
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
