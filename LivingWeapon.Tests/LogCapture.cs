using System;
using System.Collections.Generic;
using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-157(b): THE shared ModLogger capture scope. Before this existed the same ritual -- swap a
/// sink-injected <see cref="FileConsoleLogger"/> into <see cref="ModLogger.Instance"/>, run the
/// body, restore in a finally -- was hand-rolled at ~50 sites across ~21 test files, with TWO
/// restore conventions in the wild (most restored the PRIOR instance; one family called
/// <see cref="ModLogger.UseNullLogger"/> instead). A missed finally used to leak a capture logger
/// across the whole suite; this scope makes the restore discipline structural:
/// <c>using var cap = LogCapture.Start();</c> and the restore can no longer be forgotten.
///
/// ONE convention, settled here: <see cref="Dispose"/> restores the PRIOR instance. For a
/// top-level (non-nested) capture the two conventions were always equivalent -- the prior IS the
/// assembly-wide NullLogger default installed by TestLoggingSetup's module initializer -- but
/// prior-restore is strictly more correct: it nests safely, and it never assumes what the
/// surrounding test had installed.
///
/// The knobs mirror the real per-site variants the hand-rolled sites used, and the ctor builds
/// the REAL FileConsoleLogger with the same ctor args those sites passed, so behavior is
/// identical: <paramref name="level"/> covers the <c>{ LogLevel = LogLevel.Debug }</c> family
/// (the default, Info, matches both the explicit-Info sites and the sites that never set it --
/// Info is FileConsoleLogger's own property default); <paramref name="console"/>/<paramref name="file"/>
/// cover the file-only <c>(_ =&gt; { }, file.Add)</c> and console-only <c>(console.Add, _ =&gt; { })</c>
/// wirings (an unselected sink gets the same discard lambda those sites wrote).
/// </summary>
internal sealed class LogCapture : IDisposable
{
    /// <summary>Every line the CONSOLE sink received (empty when started with console: false).</summary>
    public List<string> Console { get; } = new();

    /// <summary>Every line the FILE sink received (empty when started with file: false).</summary>
    public List<string> File { get; } = new();

    private readonly ILogger _prior;

    /// <summary>Swap a sink-injected FileConsoleLogger into ModLogger.Instance until Dispose.</summary>
    public static LogCapture Start(LogLevel level = LogLevel.Info, bool console = true, bool file = true)
        => new(level, console, file);

    private LogCapture(LogLevel level, bool console, bool file)
    {
        _prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(
            console ? Console.Add : _ => { },
            file ? File.Add : _ => { })
        { LogLevel = level };
    }

    /// <summary>Restore the PRIOR logger (see the class doc's convention argument).</summary>
    public void Dispose() => ModLogger.Instance = _prior;
}
