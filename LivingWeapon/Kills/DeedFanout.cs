namespace LivingWeapon;

/// <summary>
/// Fans KillTracker's single IDeedSink out to Reliquary (Marks) and LivingPoach (carcass drop).
/// RecordDeed and DeedMiss forward to Reliquary, RecordPoachDeed to LivingPoach. Engine passes one
/// instance as KillTracker's `deeds:` argument.
/// </summary>
internal sealed class DeedFanout : IDeedSink
{
    private readonly IDeedSink _deeds;
    private readonly LivingPoach _poach;

    public DeedFanout(IDeedSink deeds, LivingPoach poach)
    {
        _deeds = deeds;
        _poach = poach;
    }

    public void RecordDeed(int weaponId, in VictimSnapshot victim) => _deeds.RecordDeed(weaponId, in victim);

    public void DeedMiss(int slot) => _deeds.DeedMiss(slot);

    public void RecordPoachDeed(int weaponId, in VictimSnapshot victim, int slot, bool delayedOrCharged, bool viaFallback)
        => _poach.RecordPoachDeed(weaponId, in victim, slot, delayedOrCharged, viaFallback);
}
