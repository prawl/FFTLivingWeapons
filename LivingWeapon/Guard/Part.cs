namespace LivingWeapon;

/// <summary>The fourteen switchable parts of the mod, in report order. Dependency free.</summary>
internal enum Part
{
    Sensing,
    KillCounting,
    StatGrowth,
    CardPaint,
    BandSignatures,
    WeaponCommands,
    Banners,
    ExtendedInventory,
    TwinPistols,
    Bulwark,
    WeaponColors,
    GunGrowth,
    PoachDen,
    Provoke,
}

/// <summary>The thirteen checks that feed the parts registry, in the order a part lists them.</summary>
internal enum Door { Roster, KitLane, Sensing, PromptHook, ExtendedArm, TwinPistols, TerrainGrid, PaletteBanks, PoachStore, ProvokeTables, ItemStats, Band, StaticArray }

internal enum DoorState { Waiting, Open, Closed }

/// <summary>One door's answer for one tick. FailedCheck separates a check against the game that came
/// out wrong from a plain absence (nothing shipped, no loader hooks), and only the former may box.</summary>
internal readonly struct DoorReading
{
    public DoorState State { get; }
    public string Reason { get; }
    public bool FailedCheck { get; }

    private DoorReading(DoorState state, string reason, bool failedCheck)
    {
        State = state;
        Reason = reason;
        FailedCheck = failedCheck;
    }

    public static DoorReading Waiting(string reason) => new(DoorState.Waiting, reason, false);
    public static DoorReading Open() => new(DoorState.Open, "", false);
    public static DoorReading Failed(string reason) => new(DoorState.Closed, reason, true);
    public static DoorReading Absent(string reason) => new(DoorState.Closed, reason, false);
}

/// <summary>A door reading written by one thread and read by another. The struct is wrapped in an
/// immutable holder so a reader never sees a state from one write and a reason from the next.</summary>
internal sealed class DoorCell
{
    private sealed class Holder
    {
        public readonly DoorReading Reading;
        public Holder(DoorReading reading) { Reading = reading; }
    }

    private volatile Holder _holder;

    public DoorCell(DoorReading initial) { _holder = new Holder(initial); }

    public DoorReading Value => _holder.Reading;

    public void Set(DoorReading reading) => _holder = new Holder(reading);
}
