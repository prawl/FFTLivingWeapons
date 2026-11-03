using System;

namespace ModGates.Logging;

/// <summary>What a mod tells the logger at Init. Both hooks are optional.</summary>
internal sealed record LogSetup(
    string ModDir,
    string Prefix,
    string FileStem,
    Action<string>? FaultNote = null,
    Action? ErrorFlush = null)
{
    public static LogSetup Default(string modDir) => new(modDir, "[Mod]", "mod");
}
