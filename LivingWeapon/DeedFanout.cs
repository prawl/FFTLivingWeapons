namespace LivingWeapon;

/// <summary>
/// LW-167: the composition-root fan-out that lets KillTracker.CreditKill report to BOTH
/// Reliquary (Marks) and LivingPoach (the Poach carcass drop) through the SAME single IDeedSink?
/// seam it already had -- KillTracker.cs's own field/ctor/signature are completely untouched by
/// this feature; only CreditKill's body gained one RecordPoachDeed call alongside RecordDeed
/// (IDeedSink.RecordPoachDeed's default no-op keeps Reliquary itself source-untouched too, see
/// Reliquary.cs's IDeedSink). RecordDeed/DeedMiss forward to the inner (Reliquary) sink;
/// RecordPoachDeed forwards to LivingPoach. Engine constructs exactly ONE of these and passes it
/// as KillTracker's `deeds:` argument.
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
