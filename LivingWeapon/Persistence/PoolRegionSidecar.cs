using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// LW-324: pool_regions.json, PoolLocator's warm-start seed sidecar (SaveLocation.SaveDir,
/// alongside kills.json -- survives a mod-folder-replace update). Schema mirrors PoolLocator's
/// own _cached shape exactly: one (baseAddr, size) tuple per region, plus the PE build key the
/// sidecar was written under (LaunchGuard.ExpectedTimeDateStamp/ExpectedSizeOfImage, sourced
/// from there and never recomputed here) -- a game patch re-anchor changes those constants, so a
/// sidecar written by a stale build is rejected wholesale rather than seeding addresses a
/// patched process no longer shares the same layout with.
///
/// Written via the shared SidecarJson.SaveAtomic chain (KillTally.Save/LegendStore.SaveIfDirty's
/// own precedent). Read tolerant of every failure shape (missing file, malformed JSON, an
/// unrecognized schema version, a PE key mismatch) -- Load NEVER throws, always answering an
/// empty result instead, so a warm start with nothing usable degrades to exactly today's cold
/// scan (PoolLocator.WarmStart.cs's SeedFromSidecar seeds nothing and the ordinary full locate
/// runs unchanged).
/// </summary>
internal static class PoolRegionSidecar
{
    internal const int SchemaVersion = 1;

    /// <summary>Load's own answer: the persisted region list, or empty when nothing usable was
    /// found. Never distinguishes the reason to the caller -- every empty result is treated the
    /// same way (seed nothing, let the ordinary cold scan run); the reason still lands in the
    /// file-only Debug line below for anyone reading the log.</summary>
    internal readonly record struct LoadResult(IReadOnlyList<(long baseAddr, long size)> Regions);

    private static readonly LoadResult EmptyResult = new(Array.Empty<(long, long)>());

    internal static LoadResult Load(string path)
    {
        try
        {
            if (!File.Exists(path)) { LogEmpty("no sidecar file yet"); return EmptyResult; }

            var data = JsonConvert.DeserializeObject<SidecarDto>(File.ReadAllText(path));
            if (data == null || data.Version != SchemaVersion)
            { LogEmpty("missing or unrecognized schema version"); return EmptyResult; }
            if (data.PeTimeDateStamp != LaunchGuard.ExpectedTimeDateStamp || data.PeSizeOfImage != LaunchGuard.ExpectedSizeOfImage)
            { LogEmpty("PE build key does not match the running build"); return EmptyResult; }

            var regions = new List<(long baseAddr, long size)>(data.Regions.Count);
            foreach (var r in data.Regions) regions.Add((r.BaseAddr, r.Size));
            return new LoadResult(regions);
        }
        catch (Exception ex)
        {
            LogEmpty("unreadable or corrupt (" + ex.GetType().Name + ")");
            return EmptyResult;
        }
    }

    /// <summary>Persist <paramref name="regions"/> exactly as PoolLocator's own _cached holds
    /// them, stamped with the CURRENT build's PE key (LaunchGuard's constants, never
    /// recomputed). Atomic via the shared SidecarJson chain. Fail-soft, mirroring
    /// KillTally.Save/LegendStore.SaveIfDirty exactly: a failed save logs and leaves the
    /// previous file intact rather than throwing -- this runs on Engine's own tick thread
    /// (PoolLocator.Restart.cs's Publish), which must never raise.</summary>
    internal static void Save(string path, IReadOnlyList<(long baseAddr, long size)> regions)
    {
        try
        {
            var dto = new SidecarDto
            {
                Version = SchemaVersion,
                PeTimeDateStamp = LaunchGuard.ExpectedTimeDateStamp,
                PeSizeOfImage = LaunchGuard.ExpectedSizeOfImage,
                Regions = new List<RegionDto>(regions.Count),
            };
            foreach (var r in regions) dto.Regions.Add(new RegionDto { BaseAddr = r.baseAddr, Size = r.size });
            SidecarJson.SaveAtomic(path, JsonConvert.SerializeObject(dto));
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Save, "Failed to save the pool-region sidecar to disk: " + ex.Message);
        }
    }

    private static void LogEmpty(string reason) =>
        ModLogger.Debug(LogVerb.Save, $"Pool-region sidecar not usable ({reason}); warm start will seed nothing this launch.");

    private sealed class SidecarDto
    {
        [JsonProperty("version")] public int Version { get; set; }
        [JsonProperty("peTimeDateStamp")] public uint PeTimeDateStamp { get; set; }
        [JsonProperty("peSizeOfImage")] public uint PeSizeOfImage { get; set; }
        [JsonProperty("regions")] public List<RegionDto> Regions { get; set; } = new();
    }

    private sealed class RegionDto
    {
        [JsonProperty("baseAddr")] public long BaseAddr { get; set; }
        [JsonProperty("size")] public long Size { get; set; }
    }
}
