using System;

namespace ModGates.Logging;

/// <summary>
/// Every verb a log line may carry. The set is closed, and the table in docs/LOGGING.md lists
/// the same verbs, so a new subsystem reuses one of these.
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

/// <summary>The bracket token each verb renders as.</summary>
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
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "unmapped LogVerb"),
    };
}
