using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Portable startup fingerprint guard core (LW-50). COPY-FILE PORTABILITY CONTRACT: this file has
/// zero project dependencies (no Offsets, no ModLogger, no IGameMemory, no Flight, no Reloaded
/// types), so a sibling mod adopts the mechanism by copying this one file, not by referencing a
/// shared library or NuGet package (owner decision: no package route).
///
/// SINGLE-THREAD CONTRACT: <see cref="FingerprintGuard.Step"/> is meant to be called from one host
/// loop thread only. Making the armed/stood-down verdict visible to OTHER threads (e.g. a
/// cross-thread write-gate flag) is the ADAPTER's job, not this core's: the core keeps no locks
/// and offers no thread-safety guarantee beyond "called from one thread, one Step at a time."
/// </summary>
internal delegate bool GuardTryRead(long addr, int len, out byte[] buf);

internal enum LandmarkVerdict { Unreadable, Match, Mismatch }

internal enum GuardState { Verifying, Armed, StoodDown }

/// <summary>LW-83: one landmark's verdict PLUS, on a Mismatch only, the observed-vs-expected
/// evidence behind it (Detail is null for every other verdict). The implicit operator from a bare
/// <see cref="LandmarkVerdict"/> is load-bearing: every probe that has no detail to offer (Match,
/// Unreadable, or a Custom landmark that never composes one) keeps returning a bare verdict with
/// no call-site change.</summary>
internal readonly struct LandmarkReading
{
    public LandmarkVerdict Verdict { get; }
    public string? Detail { get; }

    public LandmarkReading(LandmarkVerdict verdict, string? detail = null)
    {
        Verdict = verdict;
        Detail = detail;
    }

    public static implicit operator LandmarkReading(LandmarkVerdict verdict) => new(verdict);
}

/// <summary>One fact the guard checks: a name (for diagnostics) and a probe that reads live
/// memory and returns a landmark reading. A probe never throws: a failed read is Unreadable, not
/// an exception. <see cref="LandmarkReading.Detail"/> carries the observed-vs-expected mismatch
/// evidence and is null on every other verdict (LW-83).</summary>
internal sealed class GuardLandmark
{
    public string Name { get; }
    public Func<LandmarkReading> Probe { get; }

    public GuardLandmark(string name, Func<LandmarkReading> probe)
    {
        Name = name;
        Probe = probe;
    }
}

/// <summary>
/// Retry-until-decidable arming state machine. Born <see cref="GuardState.Verifying"/>; every
/// <see cref="Step"/> probes every landmark fresh (no cross-Step latching of a single landmark's
/// verdict). All landmarks Match in one Step: arms permanently. Any landmark Mismatches: a
/// consecutive-mismatch counter increments; a Step with zero mismatches (all Match, all
/// Unreadable, or a Match/Unreadable mix) resets the counter to zero, so an isolated transient
/// mismatch never accumulates toward stand-down. At <c>mismatchDebounce</c> CONSECUTIVE
/// mismatching Steps the guard stands down PERMANENTLY and never probes again.
///
/// Armed and StoodDown are both terminal: once armed, stays armed for the whole session even
/// though a legitimate in-session write can change a landmark's bytes after arming (e.g.
/// Barrage's JobCommand injection). Re-verifying after arming is deliberately out of scope.
/// </summary>
internal sealed class FingerprintGuard
{
    private readonly IReadOnlyList<GuardLandmark> _landmarks;
    private readonly Action<string> _onStandDown;
    private readonly Action? _onArmed;
    private readonly int _mismatchDebounce;
    private int _mismatchStreak;

    public GuardState State { get; private set; } = GuardState.Verifying;
    public bool Armed => State == GuardState.Armed;

    public FingerprintGuard(IReadOnlyList<GuardLandmark> landmarks, Action<string> onStandDown,
        Action? onArmed = null, int mismatchDebounce = 1)
    {
        _landmarks = landmarks;
        _onStandDown = onStandDown;
        _onArmed = onArmed;
        _mismatchDebounce = mismatchDebounce;
    }

    public void Step()
    {
        if (State != GuardState.Verifying) return;   // terminal: probe delegates are never invoked again

        bool anyMismatch = false;
        bool anyUnreadable = false;
        List<string>? mismatched = null;
        foreach (var landmark in _landmarks)
        {
            var reading = landmark.Probe();
            switch (reading.Verdict)
            {
                case LandmarkVerdict.Mismatch:
                    anyMismatch = true;
                    // LW-83: fold the observed-vs-expected evidence into the entry when the probe
                    // offered one; a bare-verdict probe (still legal via the implicit operator)
                    // keeps the old name-only entry.
                    (mismatched ??= new List<string>()).Add(
                        reading.Detail is null ? landmark.Name : $"{landmark.Name} ({reading.Detail})");
                    break;
                case LandmarkVerdict.Unreadable:
                    anyUnreadable = true;
                    break;
            }
        }

        if (!anyMismatch && !anyUnreadable)
        {
            State = GuardState.Armed;
            _mismatchStreak = 0;
            _onArmed?.Invoke();
            return;
        }

        if (!anyMismatch)
        {
            _mismatchStreak = 0;   // a mismatch-free step (a Match/Unreadable mix): reset the streak
            return;
        }

        _mismatchStreak++;
        if (_mismatchStreak >= _mismatchDebounce)
        {
            State = GuardState.StoodDown;
            // "; " not ", ": a landmark's detail can itself contain commas (LW-83).
            _onStandDown(string.Join("; ", mismatched!));
        }
    }

    // ---- static landmark factories ----

    // PE header layout (independently re-derived; matches ArmAudit.cs:22-24, the Treasure Master
    // build-key gate this guard borrows the same field offsets from): e_lfanew (u32) at
    // moduleBase+0x3C; TimeDateStamp (u32) at moduleBase+e_lfanew+8; SizeOfImage (u32) at
    // moduleBase+e_lfanew+0x50. This is generic PE/COFF header shape, not an FFT-specific magic
    // number; the EXPECTED values for one game build are the adapter's job (LaunchGuard.cs).
    private const long ELfanewOffset = 0x3C;
    private const long TimeDateStampOffset = 8;
    private const long SizeOfImageOffset = 0x50;

    /// <summary>A landmark over the mapped image's PE build key (TimeDateStamp + SizeOfImage): a
    /// failed or short read at any of the three fields is Unreadable (the image may not be
    /// mapped/readable yet); otherwise both fields equal is Match, else Mismatch with a detail
    /// naming both fields' expected and observed values (LW-83).</summary>
    public static GuardLandmark PeBuildKey(GuardTryRead tryRead, long moduleBase,
        uint expectedTimeDateStamp, uint expectedSizeOfImage)
    {
        return new GuardLandmark("pe-build-key", () =>
        {
            if (!TryReadU32(tryRead, moduleBase + ELfanewOffset, out uint eLfanew))
                return LandmarkVerdict.Unreadable;
            if (!TryReadU32(tryRead, moduleBase + eLfanew + TimeDateStampOffset, out uint timeDateStamp))
                return LandmarkVerdict.Unreadable;
            if (!TryReadU32(tryRead, moduleBase + eLfanew + SizeOfImageOffset, out uint sizeOfImage))
                return LandmarkVerdict.Unreadable;
            if (timeDateStamp == expectedTimeDateStamp && sizeOfImage == expectedSizeOfImage)
                return LandmarkVerdict.Match;
            return new LandmarkReading(LandmarkVerdict.Mismatch,
                $"expected TimeDateStamp=0x{expectedTimeDateStamp:X8} SizeOfImage=0x{expectedSizeOfImage:X8}, "
                + $"observed TimeDateStamp=0x{timeDateStamp:X8} SizeOfImage=0x{sizeOfImage:X8}");
        });
    }

    /// <summary>A fixed byte window at <paramref name="addr"/>. A failed or short read is
    /// Unreadable and never indexes past the returned buffer's length. An ALL-ZERO buffer is also
    /// Unreadable, not Mismatch: the documented boot-window escape for a table region the engine
    /// has not built yet (uninitialized process memory reads as zero, not as foreign bytes). A
    /// Mismatch's detail carries the expected and observed byte windows, hyphen-hex formatted
    /// (LW-83).</summary>
    public static GuardLandmark ByteSignature(GuardTryRead tryRead, long addr, byte[] expected, string name)
    {
        return new GuardLandmark(name, () =>
        {
            if (!tryRead(addr, expected.Length, out var buf) || buf.Length < expected.Length)
                return LandmarkVerdict.Unreadable;
            bool allZero = true;
            for (int i = 0; i < expected.Length; i++)
                if (buf[i] != 0) { allZero = false; break; }
            if (allZero) return LandmarkVerdict.Unreadable;
            for (int i = 0; i < expected.Length; i++)
                if (buf[i] != expected[i])
                    return new LandmarkReading(LandmarkVerdict.Mismatch,
                        $"expected {BitConverter.ToString(expected)}, "
                        + $"observed {BitConverter.ToString(buf, 0, expected.Length)}");
            return LandmarkVerdict.Match;
        });
    }

    /// <summary>An arbitrary game-knowledge probe (e.g. the Ramza roster row shape, or a
    /// boot-window-guarded composite of other windows). The core stays agnostic: this is how the
    /// adapter plugs game-specific logic in without the core ever seeing a game offset.</summary>
    public static GuardLandmark Custom(string name, Func<LandmarkReading> probe) => new(name, probe);

    private static bool TryReadU32(GuardTryRead tryRead, long addr, out uint value)
    {
        value = 0;
        if (!tryRead(addr, 4, out var buf) || buf.Length < 4) return false;
        value = (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
        return true;
    }
}
