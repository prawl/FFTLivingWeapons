namespace LivingWeapon;

/// <summary>
/// Static null-object facade over <see cref="FlightRecorder"/>, swappable like ModLogger's Instance.
/// Call sites (TurnTracker, KillTracker, ActorRegister, BannerToast, PromptSwap, Engine, FileConsoleLogger)
/// call Flight.Record/RequestFlush/DrainPending/FlushBattleEnd, which do nothing until <see cref="Init"/>
/// (called from Mod.cs after ModLogger.Init) builds the core. Existing tests never call Init, so they run
/// unmodified (pinned by FlightRecorderTests' facade test).
/// See docs/LOGGING.md ("Flight recorder") for the design.
/// </summary>
internal static class Flight
{
    private static readonly object _lock = new();
    private static FlightRecorder? _core;

    /// <summary>Constructs the real recorder rooted at modDir/flight/. Called once from Mod.StartEngine after ModLogger.Init.</summary>
    public static void Init(string modDir)
    {
        lock (_lock) { _core = new FlightRecorder(modDir); }
    }

    /// <summary>Append one on-change event. No-op before <see cref="Init"/>.</summary>
    public static void Record(string type, string payload) => _core?.Record(type, payload);

    /// <summary>Flag-only flush request (see FlightRecorder.RequestFlush), no I/O on the calling thread.</summary>
    public static void RequestFlush(string trigger) => _core?.RequestFlush(trigger);

    /// <summary>Performs any flush <see cref="RequestFlush"/> queued. Call once per Engine tick.</summary>
    public static void DrainPending() => _core?.DrainPending();

    /// <summary>Synchronous battle-exit flush, called from Engine's loop thread beside KillTally.Save,
    /// never from the game's SetTextString thread.</summary>
    public static void FlushBattleEnd() => _core?.Flush("battle-exit");

    /// <summary>Synchronous battle-enter flush, archiving whatever the ring gathered since the last flush. Added
    /// 2026-07-04 after three sessions ended in a process kill before any exit edge fired. Loop-thread only,
    /// like <see cref="FlushBattleEnd"/>.</summary>
    public static void FlushBattleStart() => _core?.Flush("battle-start");

    /// <summary>Test-only. Drops the current core so a later <see cref="Init"/> starts fresh and a test's
    /// recorder does not leak into others. Like ModLogger.Reset.</summary>
    internal static void Reset() { lock (_lock) { _core = null; } }
}
