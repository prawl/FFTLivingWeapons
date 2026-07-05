using System;

namespace LivingWeapon;

/// <summary>
/// A per-module logger scoped to one <see cref="LogVerb"/> and one "is this module armed"
/// predicate (e.g. <c>Wielder.AnyDeployedMainHand(mem, GalewindId)</c>). Built by
/// <see cref="ModLogger.For"/>. Implements every audited GATE-ON-ARMED disposition
/// structurally: <see cref="Info"/> and <see cref="Warn"/> demote to Debug (file-only by
/// default) when the module is not armed this battle, instead of every signature module
/// hand-rolling its own "if (armed) Event else Debug" branch. <see cref="Debug"/> is never
/// gated: file-only evidence is always worth keeping regardless of armed state.
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

    /// <summary>Warning when armed, else demoted to Debug (file-only by default): an unarmed
    /// module's degraded state is not worth a console warning nobody fielded the weapon for.</summary>
    public void Warn(string message)
    {
        if (SafeArmed()) ModLogger.Warn(_verb, message);
        else ModLogger.Debug(_verb, message);
    }

    /// <summary>Always Debug (file-only by default), armed or not: the evidence chain.</summary>
    public void Debug(string message) => ModLogger.Debug(_verb, message);

    /// <summary>Armed predicates read live memory (Wielder.AnyDeployedMainHand et al.); never
    /// let a probe throw and silently kill the whole log call, mirroring the project's
    /// fail-safe Mem philosophy.</summary>
    private bool SafeArmed()
    {
        try { return _armed(); } catch { return false; }
    }
}
