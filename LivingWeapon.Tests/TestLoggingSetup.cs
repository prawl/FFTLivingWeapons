using System.Runtime.CompilerServices;
using LivingWeapon;
using Xunit;

// LoggerTests.cs briefly swaps the process-wide ModLogger.Instance to assert on the static
// facade's routing (ModLogger_routes_every_call_through_the_current_Instance et al). xUnit runs
// different test CLASSES in parallel by default, and every other test class's production code
// calls ModLogger too -- without this, a concurrently-running class could write into a
// LoggerTests fake's list mid-enumeration ("Collection was modified"). Disabling parallelization
// makes the whole suite deterministic with respect to that one shared static; the suite is small
// enough (unit tests, no I/O) that this costs no meaningful wall-clock time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LivingWeapon.Tests;

/// <summary>Runs once when the test assembly loads: swaps ModLogger to the swallow-everything
/// NullLogger so the hundreds of existing tests that exercise ModLogger-calling production code
/// (Barrage, TreasureMaster, KillTracker, ...) never write a real livingweapon.log into the test
/// bin directory or spam the test-run console. LoggerTests.cs constructs its own FileConsoleLogger
/// with injected fake sinks directly, so it is unaffected by this shared static swap.</summary>
internal static class TestLoggingSetup
{
    [ModuleInitializer]
    public static void Init() => ModLogger.UseNullLogger();
}
