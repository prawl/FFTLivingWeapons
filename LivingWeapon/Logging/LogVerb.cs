using System;

namespace LivingWeapon;

/// <summary>
/// Closed glossary of event verbs for log lines. docs/LOGGING.md carries a matching verb
/// table, and LogContractTests keeps the two in lockstep. New subsystems reuse a verb.
/// </summary>
internal enum LogVerb
{
    Startup,
    Config,
    BattleStart,
    BattleEnd,
    Kill,
    Credit,
    Mark,
    Tier,
    Grant,
    Signature,
    Toast,
    Save,
    Display,
    Growth,
    Turn,
    Engine,
    Trace,
}

/// <summary>Enum member to the kebab-case bracket token used in log lines and docs/LOGGING.md.</summary>
internal static class LogVerbToken
{
    public static string Token(this LogVerb verb) => verb switch
    {
        LogVerb.Startup => "startup",
        LogVerb.Config => "config",
        LogVerb.BattleStart => "battle-start",
        LogVerb.BattleEnd => "battle-end",
        LogVerb.Kill => "kill",
        LogVerb.Credit => "credit",
        LogVerb.Mark => "mark",
        LogVerb.Tier => "tier",
        LogVerb.Grant => "grant",
        LogVerb.Signature => "signature",
        LogVerb.Toast => "toast",
        LogVerb.Save => "save",
        LogVerb.Display => "display",
        LogVerb.Growth => "growth",
        LogVerb.Turn => "turn",
        LogVerb.Engine => "engine",
        LogVerb.Trace => "trace",
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "unmapped LogVerb: add it to both the enum's Token() switch and docs/LOGGING.md's verb table"),
    };
}
