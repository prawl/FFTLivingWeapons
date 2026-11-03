using System.Runtime.CompilerServices;
using ModGates.Logging;

// ModLogger.Instance is one process-wide static; running test classes in parallel would let one
// class's logger swap land mid-assertion in another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ModGates.Selftest;

/// <summary>Runs once when the test assembly loads and swaps in the logger that swallows
/// everything, so nothing under test writes a real log file or spams the test console.</summary>
internal static class TestSetup
{
    [ModuleInitializer]
    public static void Init() => ModLogger.UseNullLogger();
}
