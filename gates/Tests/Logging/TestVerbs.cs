using System;
using System.Linq;
using ModGates.Logging;

namespace ModGates.Tests.Logging;

/// <summary>
/// Verbs for the shared logger tests, picked from whatever LogVerb the mod supplies. Trace is
/// the one member every mod must carry; A and B are the first two others, in enum order.
/// </summary>
internal static class TestVerbs
{
    private static readonly LogVerb[] Others = Enum.GetValues<LogVerb>().Where(v => v != LogVerb.Trace).ToArray();

    public static LogVerb A => Others[0];
    public static LogVerb B => Others[1];

    public static string Bracket(LogVerb verb) => $"[{verb.Token()}]";
}
