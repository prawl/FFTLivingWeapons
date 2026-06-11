using System;

namespace LivingWeapon.Tests;

/// <summary>
/// Mutable clock box shared by the Display suites so tests can advance time after
/// Display construction (the injected Func&lt;long&gt; reads Ms on every call).
/// </summary>
internal sealed class TestClock
{
    public long Ms;
    public Func<long> Func => () => Ms;
}
