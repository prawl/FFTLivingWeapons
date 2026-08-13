using System;

namespace LivingWeapon;

/// <summary>
/// The closed event-verb glossary for every log line the runtime emits. docs/LOGGING.md commits
/// a verb table that must match this enum one-for-one (LogContractTests pins the two in
/// lockstep, parsing the doc the same way MetaSchemaTests walks up from the test bin dir). The
/// set is CLOSED: a new subsystem reuses one of these verbs, or the facelift doc gets amended
/// deliberately; no ad-hoc per-module prefixes.
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
    Treasure,
    Engine,
    Trace,
}

/// <summary>Enum member -&gt; the literal kebab-case bracket token rendered in log lines and
/// committed in docs/LOGGING.md's verb table. One-to-one; the enum's declaration order need not
/// match the doc table's grouping.</summary>
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
        LogVerb.Treasure => "treasure",
        LogVerb.Engine => "engine",
        LogVerb.Trace => "trace",
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "unmapped LogVerb: add it to both the enum's Token() switch and docs/LOGGING.md's verb table"),
    };
}
