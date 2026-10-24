using System;

namespace LivingWeapon;

/// <summary>
/// Frees the Provoke hold and the Bulwark when the game restores the battle's checkpoint.
/// A rewind fires no battle edge, so the rising checkpoint restore count is the only signal there is.
/// It fires when the checkpoint is restored, about two seconds before the rewound battle is back on screen.
/// </summary>
internal sealed class RewindRelease
{
    private readonly Func<long> _restoreCount;
    private readonly Action _release;
    private long _lastSeen = -1;

    public RewindRelease(Func<long> restoreCount, Action release)
    {
        _restoreCount = restoreCount;
        _release = release;
    }

    // When a battle is rewound, the mod lets go of what it set up and tells the player once, but only if it held something.
    // The provoke release keeps the game's own marks, and the bulwark release only restores bytes that still read as ours.
    internal static RewindRelease Wire(Func<long> restoreCount, ProvokeHold provokeHold, Bulwark bulwark, BannerToast toast)
        => new(restoreCount, Sequence(() => provokeHold.Holding, () => bulwark.Holding,
            provokeHold.ReleaseForRewind, bulwark.ReleaseForRewind, toast.DropPendingOnRewind,
            line => ModLogger.Event(LogVerb.Signature, line), detail => Flight.Record("rewind", detail)));

    internal static Action Sequence(Func<bool> provokeHolding, Func<bool> bulwarkHolding,
        Action provoke, Action bulwark, Action toasts, Action<string> log, Action<string> record) => () =>
    {
        bool provokeHeld = provokeHolding();
        bool bulwarkHeld = bulwarkHolding();
        provoke();
        bulwark();
        toasts();
        string? line = Line(provokeHeld, bulwarkHeld);
        if (line != null) log(line);
        record($"release provoke={provokeHeld} bulwark={bulwarkHeld}");
    };

    internal static string? Line(bool provoke, bool bulwark) => (provoke, bulwark) switch
    {
        (true, true) => "The battle was rewound, so the Provoke hold and the Sunderer's Bulwark let go and wait for the rewound battle to set them up again.",
        (true, false) => "The battle was rewound, so the Provoke hold lets go and waits for the rewound battle to set it up again.",
        (false, true) => "The battle was rewound, so the Sunderer's Bulwark lets go and waits for the rewound battle to set it up again.",
        _ => null,
    };

    // The first reading only primes, so a count inherited at launch never counts as a rewind.
    internal static bool ShouldRelease(long lastSeen, long now) => lastSeen >= 0 && now > lastSeen;

    public void Tick()
    {
        long now = _restoreCount();
        if (ShouldRelease(_lastSeen, now)) _release();
        _lastSeen = now;
    }
}
