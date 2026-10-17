using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Portable startup fingerprint guard core. It has no project dependencies (no Offsets,
/// ModLogger, IGameMemory, Flight or Reloaded types), so a sibling mod can copy this one file.
///
/// <see cref="FingerprintGuard.Step"/> must be called from a single host loop thread. The core keeps
/// no locks; publishing the armed/stood-down verdict to other threads is the adapter's job.
/// </summary>
internal delegate bool GuardTryRead(long addr, int len, out byte[] buf);

internal enum LandmarkVerdict { Unreadable, Match, Mismatch }

internal enum GuardState { Verifying, Armed, StoodDown }

/// <summary>one landmark's verdict, plus the observed-vs-expected evidence on a Mismatch.
/// Detail is null for Match; an Unreadable may carry a reason that only an adapter reads.
/// The implicit conversion from <see cref="LandmarkVerdict"/> lets probes with no detail return a
/// bare verdict.</summary>
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

/// <summary>One fact the guard checks. A diagnostic name and a probe that reads live memory. A probe
/// does not throw; a failed read is Unreadable. See <see cref="LandmarkReading.Detail"/>.</summary>
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
/// Retry-until-decidable arming state machine. Starts <see cref="GuardState.Verifying"/> and probes
/// every landmark fresh on each <see cref="Step"/>. One Step with all landmarks matching arms it.
/// Each Step with a mismatch bumps a consecutive counter; a Step without one resets it. At
/// <c>mismatchDebounce</c> consecutive mismatching Steps it stands down.
///
/// Armed and StoodDown are terminal. It does not re-verify after arming, since a legitimate
/// in-session write (e.g. Barrage's JobCommand injection) can change a landmark's bytes.
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
        if (State != GuardState.Verifying) return;   // terminal. Probe delegates are never invoked again

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
                    // Include the probe's evidence when it gave any, else just the name.
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
            // Joined with "; " because a landmark's detail can contain commas.
            _onStandDown(string.Join("; ", mismatched!));
        }
    }

    // ---- static landmark factories ----

    // Standard PE header offsets. e_lfanew (u32) is at moduleBase+0x3C, TimeDateStamp (u32) at
    // moduleBase+e_lfanew+8, SizeOfImage (u32) at moduleBase+e_lfanew+0x50. The expected values for
    // a game build live in the adapter (LaunchGuard.cs). Also used, since LW-397, by the build-stamp
    // report line (LaunchGuard.Stamp.cs).
    private const long ELfanewOffset = 0x3C;
    private const long TimeDateStampOffset = 8;
    private const long SizeOfImageOffset = 0x50;

    /// <summary>Landmark over the image's PE build key (TimeDateStamp + SizeOfImage). A failed or
    /// short read is Unreadable; a differing value is a Mismatch whose detail lists expected and
    /// observed values.</summary>
    public static GuardLandmark PeBuildKey(GuardTryRead tryRead, long moduleBase,
        uint expectedTimeDateStamp, uint expectedSizeOfImage)
    {
        return new GuardLandmark("pe-build-key", () =>
        {
            if (!TryReadPeBuildKey(tryRead, moduleBase, out uint timeDateStamp, out uint sizeOfImage))
                return LandmarkVerdict.Unreadable;
            if (timeDateStamp == expectedTimeDateStamp && sizeOfImage == expectedSizeOfImage)
                return LandmarkVerdict.Match;
            return new LandmarkReading(LandmarkVerdict.Mismatch,
                $"expected TimeDateStamp=0x{expectedTimeDateStamp:X8} SizeOfImage=0x{expectedSizeOfImage:X8}, "
                + $"observed TimeDateStamp=0x{timeDateStamp:X8} SizeOfImage=0x{sizeOfImage:X8}");
        });
    }

    /// <summary>reads the PE build key, the same three reads as <see cref="PeBuildKey"/>.
    /// Returns false with both outs zero on any failed or short read.</summary>
    public static bool TryReadPeBuildKey(GuardTryRead tryRead, long moduleBase,
        out uint timeDateStamp, out uint sizeOfImage)
    {
        timeDateStamp = 0;
        sizeOfImage = 0;
        if (!TryReadU32(tryRead, moduleBase + ELfanewOffset, out uint eLfanew)) return false;
        if (!TryReadU32(tryRead, moduleBase + eLfanew + TimeDateStampOffset, out uint tds)) return false;
        if (!TryReadU32(tryRead, moduleBase + eLfanew + SizeOfImageOffset, out uint soi)) return false;
        timeDateStamp = tds;
        sizeOfImage = soi;
        return true;
    }

    /// <summary>A fixed byte window at <paramref name="addr"/>. A failed or short read is Unreadable.
    /// An all-zero buffer is also Unreadable, since a table the engine has not built yet reads as
    /// zero during boot. A Mismatch detail shows expected and observed bytes as hyphen-hex.</summary>
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

    /// <summary>A game-specific probe supplied by the adapter (e.g. the Ramza roster row shape), so
    /// the core never sees a game offset.</summary>
    public static GuardLandmark Custom(string name, Func<LandmarkReading> probe) => new(name, probe);

    private static bool TryReadU32(GuardTryRead tryRead, long addr, out uint value)
    {
        value = 0;
        if (!tryRead(addr, 4, out var buf) || buf.Length < 4) return false;
        value = (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
        return true;
    }
}
