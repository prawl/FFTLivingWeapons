using System;

namespace ModGates.Logging;

/// <summary>
/// A logger bound to one verb and one armed check. Info and Warn demote to Debug while the
/// module is not armed, so call sites never branch on armed themselves.
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

    public void Info(string message)
    {
        if (SafeArmed()) ModLogger.Event(_verb, message);
        else ModLogger.Debug(_verb, message);
    }

    public void Warn(string message)
    {
        if (SafeArmed()) ModLogger.Warn(_verb, message);
        else ModLogger.Debug(_verb, message);
    }

    public void Debug(string message) => ModLogger.Debug(_verb, message);

    /// <summary>A throw inside the armed check must not lose the log line.</summary>
    private bool SafeArmed()
    {
        try { return _armed(); } catch { return false; }
    }
}
