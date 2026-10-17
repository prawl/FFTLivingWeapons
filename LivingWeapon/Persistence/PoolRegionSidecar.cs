using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// Pool_regions.json, PoolLocator's warm-start seed (SaveLocation.SaveDir, next to
/// kills.json, so it survives a mod-folder replace). One (baseAddr, size) tuple per region as in
/// PoolLocator's _cached, plus the PE build key from LaunchGuard.ExpectedTimeDateStamp/
/// ExpectedSizeOfImage. A sidecar written under a different key is rejected whole. Written via
/// SidecarJson.SaveAtomic like KillTally.Save and LegendStore.SaveIfDirty. Load never throws; any
/// failure returns empty, which falls back to the cold scan (PoolLocator.WarmStart.cs SeedFromSidecar).
/// </summary>
internal static class PoolRegionSidecar
{
    internal const int SchemaVersion = 1;

    /// <summary>The persisted region list, or empty when nothing usable was found. The reason is
    /// only in the Debug log line; callers treat every empty result alike.</summary>
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

    /// <summary>Persists <paramref name="regions"/> as PoolLocator's _cached holds them, stamped
    /// with the current PE key. Fail-soft like KillTally.Save. A failure logs and keeps the previous
    /// file, since this runs on Engine's tick thread (PoolLocator.Restart.cs Publish) and must not throw.</summary>
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
        // The mod can arm on an unknown build, so this key no longer tells builds
        // apart; the per-region reverify is the guard.
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
