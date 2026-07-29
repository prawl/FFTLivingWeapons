using System;
using System.IO;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-147: the one shared temp-directory fixture for suites that need a real, isolated directory
/// on disk (persistence round-trips for KillTally/LegendStore/FlightRecorder/SaveLocation/..., PS
/// pipeline probes, marker-file checks). Before this, most of those suites carried their own
/// private `TempDir()`/`TempRoot()` helper that created a directory and NEVER deleted it -- an
/// audit counted ~16 leaking suites. Each test method still gets its own directory (via each
/// suite's own helper, now backed by <see cref="Create"/>), so the LEAK was per-test-run, not
/// per-suite: a full `dotnet test` pass could leave hundreds of orphaned directories under the OS
/// temp root over time.
///
/// Uniqueness: <see cref="Create"/> suffixes <paramref name="prefix"/> with a fresh Guid, so
/// parallel test runs (and repeated runs on the same box) never collide on the same directory --
/// this repo's test assembly happens to run serialized
/// ([assembly: CollectionBehavior(DisableTestParallelization = true)], see
/// TestLoggingSetup.cs), but every converted suite keeps its own recognizable prefix (e.g.
/// "lw_tally_", "lw_saveloc_") for the same reason the pre-LW-147 helpers did: a stray leftover
/// directory (e.g. from a killed test run) is still identifiable by name.
/// </summary>
internal sealed class TempDirs : IDisposable
{
    /// <summary>The created directory's full path.</summary>
    public string Dir { get; }

    private TempDirs(string dir) => Dir = dir;

    /// <summary>Creates a fresh, uniquely-named directory under the OS temp root and returns an
    /// IDisposable that deletes it (recursively) on Dispose. Most call sites hold the returned
    /// instance in a per-test-class list (see e.g. KillTallyTests) and dispose all of them from
    /// an IDisposable.Dispose implementing xUnit's per-test teardown; a single ad hoc use can
    /// also do <c>using var temp = TempDirs.Create("prefix_");</c> directly.</summary>
    public static TempDirs Create(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new TempDirs(dir);
    }

    /// <summary>Deletes the directory recursively. Swallows IOException (a lingering file handle,
    /// the directory already gone, etc.) -- cleanup is best-effort; a delete failure must never
    /// fail or mask the test it is tearing down after.</summary>
    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); }
        catch (IOException) { }
    }
}
