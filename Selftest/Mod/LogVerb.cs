using System;

namespace ModGates.Logging;

/// <summary>
/// The verb set the self test logs with. A mod supplies its own file of this shape, in this
/// namespace, with a Trace member, and keeps it in lockstep with its docs/LOGGING.md table.
/// </summary>
internal enum LogVerb
{
    Startup,
    Gate,
    Trace,
}

/// <summary>The bracket token each verb renders as.</summary>
internal static class LogVerbToken
{
    public static string Token(this LogVerb verb) => verb switch
    {
        LogVerb.Startup => "startup",
        LogVerb.Gate => "gate",
        LogVerb.Trace => "trace",
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "unmapped LogVerb"),
    };
}
