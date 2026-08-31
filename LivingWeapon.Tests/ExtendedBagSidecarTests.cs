using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-348 / LW-353: the per-save bag-count sidecar (schema 2) and its schema-1 migration.</summary>
public class ExtendedBagSidecarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lw_bag_" + Guid.NewGuid().ToString("N"));
    private string PathOf => Path.Combine(_dir, ExtendedBagSidecar.FileName);

    public ExtendedBagSidecarTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void A_missing_file_is_a_first_run_with_no_saves_and_no_legacy()
    {
        var s = ExtendedBagSidecar.Load(PathOf);
        Assert.Equal(0, s.SaveCount);
        Assert.Equal("none (first run)", s.LoadedFrom);
        Assert.False(s.TryGetSave("pt1-abc", out _));
        Assert.Null(s.TakeLegacy());
    }

    [Fact]
    public void Record_then_reload_replays_each_save_by_its_own_key()
    {
        var s = ExtendedBagSidecar.Load(PathOf);
        s.RecordSave("pt100-aaaaaaaaaaaa", new Dictionary<int, int> { [261] = 2 });
        s.RecordSave("pt200-bbbbbbbbbbbb", new Dictionary<int, int> { [261] = 0 });
        Assert.True(File.Exists(PathOf + ".bak"));   // the SidecarJson chain keeps the previous generation

        var back = ExtendedBagSidecar.Load(PathOf);
        Assert.Equal(PathOf, back.LoadedFrom);
        Assert.Equal(2, back.SaveCount);
        Assert.True(back.TryGetSave("pt100-aaaaaaaaaaaa", out var a) && a[261] == 2);
        Assert.True(back.TryGetSave("pt200-bbbbbbbbbbbb", out var b) && b[261] == 0);   // an explicit 0 is a real answer
        Assert.False(back.TryGetSave("pt300-cccccccccccc", out _));
    }

    [Fact]
    public void Only_the_newest_MaxSaves_keys_survive_and_re_recording_a_key_refreshes_it()
    {
        var s = ExtendedBagSidecar.Load(PathOf);
        for (int i = 0; i < ExtendedBagSidecar.MaxSaves + 5; i++)
            s.RecordSave($"pt{i}-{i:x12}", new Dictionary<int, int> { [261] = i });
        Assert.Equal(ExtendedBagSidecar.MaxSaves, s.SaveCount);
        Assert.False(s.TryGetSave("pt0-000000000000", out _));   // the oldest fell off
        Assert.True(s.TryGetSave($"pt{ExtendedBagSidecar.MaxSaves + 4}-{ExtendedBagSidecar.MaxSaves + 4:x12}", out _));
        s.RecordSave("pt5-000000000005", new Dictionary<int, int> { [261] = 99 });   // re-record: refreshed, not duplicated
        var back = ExtendedBagSidecar.Load(PathOf);
        Assert.Equal(ExtendedBagSidecar.MaxSaves, back.SaveCount);
        Assert.True(back.TryGetSave("pt5-000000000005", out var c) && c[261] == 99);
    }

    [Fact]
    public void A_schema_1_file_migrates_its_counts_into_a_one_shot_legacy_fallback()
    {
        File.WriteAllText(PathOf, "{\"version\":1,\"counts\":{\"261\":2}}");
        var s = ExtendedBagSidecar.Load(PathOf);
        Assert.Contains("schema 1, migrated", s.LoadedFrom);
        Assert.Equal(0, s.SaveCount);
        var legacy = s.TakeLegacy();
        Assert.NotNull(legacy);
        Assert.Equal(2, legacy![261]);
        Assert.Null(s.TakeLegacy());   // once
        s.PersistAfterLegacyTaken();
        var back = ExtendedBagSidecar.Load(PathOf);
        Assert.Null(back.TakeLegacy());   // and not again after a relaunch
        Assert.Equal(PathOf, back.LoadedFrom);
    }

    /// <summary>LW-351: the bag replay moved onto the game's OWN thread (it now runs inside the
    /// load detour, before the game rebuilds its menu templates), so this sidecar is reached from
    /// two threads at once: the detour resolving a load, and Engine's tick recording a save. Its
    /// maps are plain Dictionary/List and its writer is SidecarJson's fixed .tmp path, and
    /// unsynchronised use of those from two threads is undefined behavior rather than a race over
    /// which write wins (the FileConsoleLoggerThreadSafetyTests precedent, same reasoning). This
    /// hammers both roles at once and demands that nothing escapes and the file is still readable
    /// afterwards.</summary>
    [Fact]
    public void Recording_and_reading_from_two_threads_never_throws()
    {
        File.WriteAllText(PathOf, "{\"version\":1,\"counts\":{\"261\":2}}");
        var s = ExtendedBagSidecar.Load(PathOf);
        var failures = new ConcurrentQueue<Exception>();
        using var cap = LogCapture.Start();   // Persist swallows its own faults, so the log is where a lost write shows
        using var done = new CancellationTokenSource();

        var recorder = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 150; i++) s.RecordSave($"pt{i}-{i:x12}", new Dictionary<int, int> { [261] = i & 0xFF });
            }
            catch (Exception ex) { failures.Enqueue(ex); }
            finally { done.Cancel(); }
        });
        var replayer = Task.Run(() =>
        {
            try
            {
                while (!done.IsCancellationRequested)
                {
                    s.TryGetSave("pt7-000000000007", out _);
                    if (s.TakeLegacy() != null) { /* the one-shot, spent on this thread in production */ }
                    s.PersistAfterLegacyTaken();
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        });

        Assert.True(Task.WaitAll(new[] { recorder, replayer }, TimeSpan.FromSeconds(60)));
        Assert.Empty(failures);
        Assert.DoesNotContain(cap.File, l => l.Contains("Failed to save the extended-inventory bag counts"));
        var back = ExtendedBagSidecar.Load(PathOf);
        Assert.True(back.SaveCount > 0, "the sidecar file must still be readable after the two threads ran");
    }

    [Fact]
    public void A_corrupt_or_foreign_file_loads_as_no_saves_never_throws()
    {
        File.WriteAllText(PathOf, "{ not json");
        var corrupt = ExtendedBagSidecar.Load(PathOf);
        Assert.Equal(0, corrupt.SaveCount);
        Assert.Equal("unreadable", corrupt.LoadedFrom);

        File.WriteAllText(PathOf, "{\"version\": 99}");
        Assert.Equal("unrecognised schema", ExtendedBagSidecar.Load(PathOf).LoadedFrom);

        File.WriteAllText(PathOf, "{\"version\":2,\"order\":[\"k\"],\"saves\":{\"k\":{\"261\":4,\"abc\":2,\"262\":300}}}");
        var partly = ExtendedBagSidecar.Load(PathOf);
        Assert.True(partly.TryGetSave("k", out var c));
        Assert.Equal(new Dictionary<int, int> { [261] = 4 }, c);   // only well-formed byte-range entries survive
    }
}
