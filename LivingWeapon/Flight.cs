namespace LivingWeapon;

/// <summary>
/// Static null-object facade over <see cref="FlightRecorder"/> -- mirrors <c>ModLogger</c>'s own
/// swappable-<c>Instance</c> idiom (see ModLogger.cs's doc comment): every call site in the
/// runtime (TurnTracker, KillTracker, ActorRegister, BannerToast, PromptSwap, Engine,
/// FileConsoleLogger) calls <c>Flight.Record</c>/<c>RequestFlush</c>/<c>DrainPending</c>/
/// <c>FlushBattleEnd</c> directly, never a concrete <see cref="FlightRecorder"/>. Every one of
/// those calls is a silent no-op until <see cref="Init"/> constructs the real core -- called once
/// from Mod.cs at startup, right after <c>ModLogger.Init</c>. This is what lets every existing
/// test (TurnTrackerTests, KillTrackerTests, ActorRegisterTests, BannerToastTests,
/// PromptSwapTests, ...) run unmodified: none of them call <see cref="Init"/>, so every
/// <c>Flight.*</c> call inside the production code they exercise does nothing (B2 of the stage-C
/// review; pinned directly by FlightRecorderTests' facade test).
///
/// See docs/LOGGING.md ("Flight recorder") for the full design writeup -- what gets captured,
/// where files land, retention, and the two accepted loss modes.
/// </summary>
internal static class Flight
{
    private static readonly object _lock = new();
    private static FlightRecorder? _core;

    /// <summary>Constructs the real recorder rooted at modDir/flight/. Called once from
    /// Mod.StartEngine, after ModLogger.Init. Every Flight.* call before this is inert.</summary>
    public static void Init(string modDir)
    {
        lock (_lock) { _core = new FlightRecorder(modDir); }
    }

    /// <summary>Append one on-change event. No-op (accumulates nothing to replay later) before
    /// <see cref="Init"/>.</summary>
    public static void Record(string type, string payload) => _core?.Record(type, payload);

    /// <summary>Flag-only flush request (see FlightRecorder.RequestFlush's B1 doc) -- no I/O on
    /// the calling thread. No-op before <see cref="Init"/>.</summary>
    public static void RequestFlush(string trigger) => _core?.RequestFlush(trigger);

    /// <summary>Performs any flush <see cref="RequestFlush"/> queued. Call once per Engine tick.</summary>
    public static void DrainPending() => _core?.DrainPending();

    /// <summary>Synchronous battle-exit flush -- called from Engine's own loop thread beside
    /// KillTally.Save() (S2), never from the game's SetTextString thread.</summary>
    public static void FlushBattleEnd() => _core?.Flush("battle-exit");

    /// <summary>Test-only: drop the current core so a later <see cref="Init"/> starts fresh, and
    /// so a test that called Init doesn't leak a live recorder (with a real modDir/flight folder)
    /// into every later test in the assembly. Mirrors ModLogger.Reset().</summary>
    internal static void Reset() { lock (_lock) { _core = null; } }
}
