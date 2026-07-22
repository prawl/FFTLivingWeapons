namespace LivingWeapon;

/// <summary>
/// Pure helpers for the engine's status-apply pipeline (LW-58, decoded 2026-07-09 from owner CE
/// captures + disasm; see docs/LIVE_LEDGER.md's status-system row and tools/probes/spawn_probe.py's
/// findings block). No live memory here: the address math and bitfield layout, unit-testable.
///
/// The engine keeps 40 status ids (0..39) in 5-byte MSB-first bitfields, three layers per unit
/// (band-entry-relative): a PENDING-ADD request field, an INFLICTED persistent layer, and a
/// COMPOSED per-frame layer (composed = inflicted OR innate, re-derived every frame). The apply
/// engine (StatusSpike.FnApplyStatuses = 0x150BF66DC, args slot + mode) walks all 40 ids for one
/// unit, reads the pending field, conflict-scans, and ORs accepted bits into the inflicted layer.
///
/// The "slot" argument is the index into the battle-stats array at BattleUnitsBase 0x141853CE0
/// (stride 0x200). Our band seats anchor on CombatAnchor 0x141855CE0 = BattleUnitsBase + 0x2000
/// = +16 slots, so engine slot = band seat - 8 (seat s -> n = s - 24 -> 16 + n = s - 8). The
/// first enemy, band seat 8, maps to engine slot 0 (owner census 2026-07-09).
/// </summary>
internal static class StatusApply
{
    /// <summary>The apply engine's slot-0 unit base (== BattleUnitsBase; CombatAnchor - 0x2000).</summary>
    public const long BattleUnitsBase = Offsets.CombatAnchor - 0x2000;

    // Band-entry-relative bitfield bases (combat base + 0x1C convention; from the deathdiff/inflict tapes).
    public const int PendingAdd = 0x1BF;   // the ADD-request field the apply engine reads
    public const int Inflicted  = 0x1D3;   // persistent layer accepted bits OR into (the "+0x18E mirror")
    public const int Composed   = 0x45;    // current layer (== Offsets.ADeadStatus); composed each frame

    // Status ids whose composed bits are already proven in Offsets (these cross-check the bit math
    // in StatusApplyTests, tying this pure layer to the observed live status offsets).
    public const int DeadId     = 2;    // composed +0x45 / 0x20
    public const int TreasureId = 15;   // composed +0x46 / 0x01 (the unit-to-chest conversion)
    public const int PoisonId   = 24;   // composed +0x48 / 0x80
    public const int HasteId    = 28;   // composed +0x48 / 0x08 (the harmless canary status)

    /// <summary>The battle-stats array holds up to this many units (FFHacktics: 21 = 0x15 Battle
    /// Stats slots). An engine slot outside [0, MaxEngineSlot] would index off the array; a cold
    /// call with such a slot could AV inside the engine, so callers must reject it.</summary>
    public const int MaxEngineSlot = 20;

    /// <summary>Engine battle-stats-array index for a band seat (0..BandSlots-1):
    /// engine slot = seat - 8 (see the class doc's derivation). NEGATIVE for band seats 0..7,
    /// which sit BELOW BattleUnitsBase (the forecast/scratch region, never real units): those are
    /// not callable, see <see cref="IsCallableSeat"/>.</summary>
    public static int EngineSlot(int seat) => seat - 8;

    /// <summary>True iff a band seat maps to an in-range engine slot [0, MaxEngineSlot] and so is
    /// safe to hand to the apply engine's cold call. Seats 0..7 (negative engine slot) and any seat
    /// past the array are rejected: a wrong slot risks an uncatchable AV inside the engine.</summary>
    public static bool IsCallableSeat(int seat)
    {
        int slot = EngineSlot(seat);
        return slot >= 0 && slot <= MaxEngineSlot;
    }

    /// <summary>Byte offset within a 5-byte MSB-first status bitfield for status id 0..39.</summary>
    public static int StatusByte(int id) => id >> 3;

    /// <summary>Bit mask within that byte, MSB-first (id 0 = 0x80, id 7 = 0x01).</summary>
    public static byte StatusMask(int id) => (byte)(0x80 >> (id & 7));

    /// <summary>True iff the status bit is ALREADY present in either live layer before a fire.
    /// A target that already holds the status can never prove the cold call did anything.</summary>
    public static bool AlreadyHeld(byte composedBefore, byte inflictedBefore, byte mask)
        => ((composedBefore | inflictedBefore) & mask) != 0;

    /// <summary>The trustworthy APPLIED verdict: the bit is present AFTER only because the call set
    /// it (it was absent from both layers BEFORE). Guards against a re-found target or a
    /// pre-existing status reporting a false success.</summary>
    public static bool NewlyApplied(byte composedBefore, byte inflictedBefore,
                                    byte composedAfter, byte inflictedAfter, byte mask)
        => !AlreadyHeld(composedBefore, inflictedBefore, mask)
           && ((composedAfter | inflictedAfter) & mask) != 0;
}
