using System;

namespace LivingWeapon;

/// <summary>
/// What a table writer asks before its first write. Open allows. Closed refuses and warns once for the
/// session. Waiting refuses quietly, since a table that has not filled in yet is an ordinary state.
/// </summary>
internal sealed class DoorLatch
{
    private readonly Func<DoorReading> _door;
    private readonly string _refusal;
    private bool _warned;
    private bool _waitNoted;

    public DoorLatch(Func<DoorReading> door, string refusal)
    {
        _door = door;
        _refusal = refusal;
    }

    public bool Allows()
    {
        var reading = _door();
        switch (reading.State)
        {
            case DoorState.Open:
                return true;
            case DoorState.Waiting:
                if (!_waitNoted)
                {
                    _waitNoted = true;
                    ModLogger.Debug(LogVerb.Startup, $"A table write is waiting for its check ({reading.Reason}).");
                }
                return false;
            default:
                if (!_warned)
                {
                    _warned = true;
                    ModLogger.Warn(LogVerb.Startup, _refusal);
                }
                return false;
        }
    }
}
