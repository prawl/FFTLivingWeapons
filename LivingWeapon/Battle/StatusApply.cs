namespace LivingWeapon;

/// <summary>
/// Pure helpers for the status-apply pipeline (decoded 2026-07-09 from CE captures and disasm; see docs/LIVE_LEDGER.md's
/// status-system row and tools/probes/spawn_probe.py). 40 status ids (0..39) sit in 5-byte MSB-first bitfields, three layers per unit
/// (band-entry-relative): pending-add, inflicted, and composed (inflicted OR innate, re-derived every frame).
/// StatusSpike's v1 target (FnApplyStatuses = 0x150BF66DC) died 2026-07-22 (prologue mismatch), so v2 decodes the dispatch thunk at
/// 0x1401FB064 each launch and landmark-verifies the destination (StatusSpike.cs v2 block). Argument order (id + 1, mode, slot) is inferred.
/// The slot argument indexes the battle-stats array at BattleUnitsBase 0x141853CE0 (stride 0x200). Band seats anchor on CombatAnchor 0x141855CE0
/// = BattleUnitsBase + 0x2000 (16 slots), so engine slot = seat, 8; the first enemy (seat 8) is slot 0 (census 2026-07-09).
/// </summary>
internal static class StatusApply
{
    /// <summary>The apply engine's slot-0 unit base, same constant as <see cref="Offsets.BattleUnitsBase"/>.</summary>
    public static long BattleUnitsBase => Offsets.BattleUnitsBase;

    // Band-entry-relative bitfield bases (combat base + 0x1C; from the deathdiff/inflict tapes).
    public const int PendingAdd = 0x1BF;   // the ADD-request field the apply engine reads
    public const int Inflicted = 0x1D3;   // persistent layer accepted bits OR into (the "+0x18E mirror")
    public const int Composed = 0x45;    // current layer (== Offsets.ADeadStatus); composed each frame

    // Status ids already known in Offsets; StatusApplyTests cross-checks the bit math against them.
    public const int DeadId = 2;    // composed +0x45 / 0x20
    public const int TreasureId = 15;   // composed +0x46 / 0x01 (the unit-to-chest conversion)
    public const int PoisonId = 24;   // composed +0x48 / 0x80
    public const int HasteId = 28;   // composed +0x48 / 0x08 (the harmless canary status)

    /// <summary>The battle-stats array holds up to this many units (FFHacktics 21 = 0x15 Battle Stats slots). A slot outside
    /// [0, MaxEngineSlot] indexes off the array and a cold call could AV inside the engine, so callers must reject it.</summary>
    public const int MaxEngineSlot = 20;

    /// <summary>Engine battle-stats-array index for a band seat (0..BandSlots-1), seat, 8 (see the class doc). Negative for seats 0..7,
    /// which sit below BattleUnitsBase (forecast/scratch region, not real units) and are not callable, see <see cref="IsCallableSeat"/>.</summary>
    public static int EngineSlot(int seat) => seat - 8;

    /// <summary>True when a band seat maps to an engine slot in [0, MaxEngineSlot] and is safe for the apply engine's cold call.
    /// Seats 0..7 and any seat past the array are rejected, since a wrong slot risks an uncatchable AV.</summary>
    public static bool IsCallableSeat(int seat)
    {
        int slot = EngineSlot(seat);
        return slot >= 0 && slot <= MaxEngineSlot;
    }

    /// <summary>Byte offset within a 5-byte MSB-first status bitfield for status id 0..39.</summary>
    public static int StatusByte(int id) => id >> 3;

    /// <summary>Bit mask within that byte, MSB-first (id 0 = 0x80, id 7 = 0x01).</summary>
    public static byte StatusMask(int id) => (byte)(0x80 >> (id & 7));

    /// <summary>True when the status bit is already present in either live layer before a fire; such a target cannot prove the cold call did anything.</summary>
    public static bool AlreadyHeld(byte composedBefore, byte inflictedBefore, byte mask)
        => ((composedBefore | inflictedBefore) & mask) != 0;

    /// <summary>The trustworthy applied verdict. The bit is present after and was absent from both layers before,
    /// so a re-found target or pre-existing status cannot report a false success.</summary>
    public static bool NewlyApplied(byte composedBefore, byte inflictedBefore,
                                    byte composedAfter, byte inflictedAfter, byte mask)
        => !AlreadyHeld(composedBefore, inflictedBefore, mask)
           && ((composedAfter | inflictedAfter) & mask) != 0;
}
