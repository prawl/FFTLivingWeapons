using System;

namespace LivingWeapon;

/// <summary>
/// Per-module logger bound to one <see cref="LogVerb"/>, built by <see cref="ModLogger.For"/>.
/// It also takes an "is this module armed" predicate. <see cref="Info"/> and <see cref="Warn"/> drop to Debug
/// (file-only by default) when the module is not armed this battle; <see cref="Debug"/> is never gated.
/// </summary>
internal readonly struct ScopedLogger
{
    private readonly LogVerb _verb;
    private readonly Func<bool> _armed;

    internal ScopedLogger(LogVerb verb, Func<bool> armed)
    {
        _verb = verb;
        _armed = armed;
    }

    /// <summary>Info when armed, else demoted to Debug (file-only by default).</summary>
    public void Info(string message)
    {
        if (SafeArmed()) ModLogger.Event(_verb, message);
        else ModLogger.Debug(_verb, message);
    }

    /// <summary>Warning when armed, else Debug. An unarmed module's degraded state is not
    /// worth a console warning.</summary>
    public void Warn(string message)
    {
        if (SafeArmed()) ModLogger.Warn(_verb, message);
        else ModLogger.Debug(_verb, message);
    }

    /// <summary>Always Debug (file-only by default), armed or not.</summary>
    public void Debug(string message) => ModLogger.Debug(_verb, message);

    /// <summary>Armed predicates read live memory (e.g. Wielder.AnyDeployedMainHand), so a
    /// throwing probe counts as not armed instead of killing the log call.</summary>
    private bool SafeArmed()
    {
        try { return _armed(); } catch { return false; }
    }
}
