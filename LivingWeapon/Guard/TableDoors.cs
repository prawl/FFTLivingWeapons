using System;

namespace LivingWeapon;

/// <summary>
/// The five table doors, one FingerprintGuard each. Every door asks whether the game still keeps its table
/// where this mod writes, by pinning the game's own addressing instructions (TableSignatures). The two tables
/// the game fills at boot also wait on a fill probe. They step from the first tick and never write.
/// Probes, stall handlers and texts live in TableDoors.Probes.cs.
/// </summary>
internal sealed partial class TableDoors
{
    private const int MismatchDebounce = 30;

    private readonly IGameMemory _mem;
    private readonly Func<string> _hedge;
    private readonly Func<bool> _rosterOpen;
    private readonly Action<string, string>? _recorder;
    private readonly Action<string>? _requestFlush;
    private readonly bool _forceMismatch;
    private readonly FingerprintGuard _terrain;
    private readonly FingerprintGuard _palette;
    private readonly FingerprintGuard _poach;
    private readonly FingerprintGuard _provoke;
    private readonly FingerprintGuard _items;
    private bool _abilityStallNoted;
    private bool _itemStallNoted;

    /// <param name="hedge">The unknown build hedge appended to each stand-down warning, empty on a known build.</param>
    /// <param name="rosterOpen">Whether the roster door is open. The fill watchers count only then.</param>
    /// <param name="forceMismatch">Dev drill LW_FORCE_TABLE_MISMATCH. The palette door reads a mismatch on healthy memory.</param>
    public TableDoors(IGameMemory mem, Func<string> hedge, Func<bool> rosterOpen,
        Action<string, string>? recorder = null, Action<string>? requestFlush = null, bool forceMismatch = false)
    {
        _mem = mem;
        _hedge = hedge;
        _rosterOpen = rosterOpen;
        _recorder = recorder;
        _requestFlush = requestFlush;
        _forceMismatch = forceMismatch;
        _terrain = Guard("terrain-grid", Terrain(), TerrainStandDownText);
        _palette = Guard("palette-banks", Palette(), PaletteStandDownText);
        _poach = Guard("poach-store", Poach(), PoachStandDownText);
        _provoke = Guard("provoke-tables", ProvokeLandmarks(), ProvokeStandDownText);
        _items = Guard("item-stats", ItemLandmarks(), ItemStandDownText);
    }

    /// <summary>Steps every door still verifying. Engine calls it once per tick beside the lane guards.</summary>
    public void Step()
    {
        _terrain.Step();
        _palette.Step();
        _poach.Step();
        _provoke.Step();
        _items.Step();
    }

    public DoorReading TerrainGrid => ReadingOf(_terrain, "the terrain data check has not run yet", "the terrain data is not where this mod expects it");

    public DoorReading PaletteBanks => ReadingOf(_palette, "the weapon palette check has not run yet",
        "the weapon palette is not where this mod expects it" + (_forceMismatch ? ", forced by the " + DrillTrigger.TableFlagName + " drill" : ""));

    public DoorReading PoachStore => ReadingOf(_poach, "the Poacher's Den store check has not run yet", "the Poacher's Den store is not where this mod expects it");

    public DoorReading ProvokeTables => ReadingOf(_provoke, AbilityWaiting, "the ability data is not where this mod expects it");

    public DoorReading ItemStats => ReadingOf(_items, ItemWaiting, "the item data is not where this mod expects it");

    private static DoorReading ReadingOf(FingerprintGuard guard, string waiting, string closed) => guard.State switch
    {
        GuardState.Armed => DoorReading.Open(),
        GuardState.StoodDown => DoorReading.Failed(closed),
        _ => DoorReading.Waiting(waiting),
    };

    private FingerprintGuard Guard(string door, GuardLandmark[] landmarks, Func<string, string> standDownText)
        => new(landmarks,
            diag =>
            {
                _recorder?.Invoke("guard", $"table door stand-down ({door}: {diag})");
                ModLogger.Warn(LogVerb.Startup, standDownText(diag) + _hedge());
                _requestFlush?.Invoke("standdown");
            },
            () => _recorder?.Invoke("guard", $"table door armed ({door})"),
            MismatchDebounce);
}
