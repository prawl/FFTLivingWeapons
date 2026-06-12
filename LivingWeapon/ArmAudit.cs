using System;

namespace LivingWeapon;

/// <summary>
/// Stateless gate evaluator for the Treasure Master module. Injected IGameMemory so it is
/// testable without a live process. Performs three evaluations:
///
///   ReadPeBuildKey  -- reads e_lfanew then TimeDateStamp + SizeOfImage from the PE header of the
///                      mapped game image (base 0x140000000). Returns null on any failed read.
///   FingerprintMatches -- TryReadBytes fpLen bytes at TerrainGrid; FNV-1a64 vs map.FpHash.
///   AuditAddrs      -- reads each tile address, classifies via TreasureMaster.ClassifyAddr,
///                      tallies ok/foreign/unreadable, calls TreasureMaster.DecideArm.
///
/// SEAM NOTE: this is NOT a TreasureMaster partial. A partial holding gate logic would be the
/// same-state-machine-in-two-files evasion the house bans (see project rules). The state
/// lives entirely in TreasureMaster.cs; ArmAudit is pure evaluation with no state of its own.
/// </summary>
internal sealed class ArmAudit
{
    private const long ModuleBase = 0x140000000L;
    private const long ELfanewOff = 0x3C;
    private const long TimeDateStampOff = 8;
    private const long SizeOfImageOff   = 0x50;

    private readonly IGameMemory _mem;

    public ArmAudit(IGameMemory mem) => _mem = mem;

    // ── PE build key ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the PE header fields from the mapped game image.
    /// Layout: e_lfanew (U32) @ base+0x3C; TimeDateStamp (U32) @ base+e_lfanew+8;
    /// SizeOfImage (U32) @ base+e_lfanew+0x50. All reads are Readable-guarded.
    /// Returns null if any read is not Readable (e.g., memory not yet mapped or wrong base).
    /// </summary>
    public (uint TimeDateStamp, uint SizeOfImage)? ReadPeBuildKey()
    {
        long elfanewAddr = ModuleBase + ELfanewOff;
        if (!_mem.Readable(elfanewAddr, 4)) return null;
        uint eLfanew = ReadU32(_mem, elfanewAddr);

        long tsAddr = ModuleBase + eLfanew + TimeDateStampOff;
        if (!_mem.Readable(tsAddr, 4)) return null;

        long szAddr = ModuleBase + eLfanew + SizeOfImageOff;
        if (!_mem.Readable(szAddr, 4)) return null;

        return (ReadU32(_mem, tsAddr), ReadU32(_mem, szAddr));
    }

    // ── map-id read ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Gate evaluation for the map-id byte: Readable guard, U8 read, and MapIdValid check.
    /// Returns false on any failed guard or invalid value.
    /// Centralised here because both TickDisarmed and TickArmed use this same gate sequence.
    /// </summary>
    public bool TryReadMapId(out byte id)
    {
        id = 0;
        if (!_mem.Readable(Offsets.LiveBattleMapId, 1)) return false;
        id = _mem.U8(Offsets.LiveBattleMapId);
        return TreasureMaster.MapIdValid(id);
    }

    // ── fingerprint ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <see cref="TreasureMap.FpLen"/> raw bytes at <see cref="Offsets.TerrainGrid"/> and
    /// compares the fingerprint to <see cref="TreasureMap.FpHash"/>. Returns false on read failure
    /// (caller retries up to TreasureArmAttemptCap).
    /// A null FpLen or FpHash means the map is a stub (no fingerprint captured); returns false
    /// so the caller treats it as not-armable (stub maps have no tiles anyway).
    ///
    /// Fingerprint version dispatch:
    ///   fpVer == 3: MaskedTerrainHashV3 (fields {2,3,4,5} per record; immune to field-0/1/6
    ///               animation on water/lava maps -- LIVE INCIDENT #3).
    ///   fpVer == 2: MaskedTerrainHash (field-0 only per record; immune to field-1/6 churn).
    ///   anything else (legacy v1 or null): plain Fnv1a64 over the raw bytes.
    /// </summary>
    public bool FingerprintMatches(TreasureMap map)
    {
        if (map.FpLen is not { } fpLen || map.FpHash is not { } expected)
            return false;

        if (!_mem.TryReadBytes(Offsets.TerrainGrid, fpLen, out var buf))
            return false;

        ulong got = map.FpVer switch
        {
            3 => TreasureMaster.MaskedTerrainHashV3(buf),
            2 => TreasureMaster.MaskedTerrainHash(buf),
            _ => TreasureMaster.Fnv1a64(buf),
        };

        return got == expected;
    }

    // ── per-address audit ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads each tile address across all tiles, classifies each byte via
    /// <see cref="TreasureMaster.ClassifyAddr"/>, tallies ok/foreign/unreadable, and
    /// calls <see cref="TreasureMaster.DecideArm"/>. Returns the verdict + the foreign
    /// count so the caller can log on first occurrence.
    /// Foreign addresses contribute to the foreign tally but do not produce a Disarm verdict;
    /// the quorum check (<paramref name="minPlausible"/>) is the sole arm gate.
    /// </summary>
    public (TreasureMaster.ArmVerdict Verdict, int ForeignCount) AuditAddrs(
        TreasureMap map, int minPlausible)
    {
        int totalAddrs = 0;
        int okCount = 0, foreignCount = 0, unreadableCount = 0;
        foreach (var tile in map.Tiles)
        {
            foreach (var (addr, _) in tile.Addrs)
            {
                totalAddrs++;
                if (!_mem.Readable(addr, 1)) { unreadableCount++; continue; }
                var state = TreasureMaster.ClassifyAddr(_mem.U8(addr));
                if      (state == TreasureMaster.AddrState.Foreign) foreignCount++;
                else                                                 okCount++;
            }
        }
        // Clamp the quorum to the actual address count so small maps can still arm.
        int effectiveMin = Math.Min(minPlausible, totalAddrs);
        return (TreasureMaster.DecideArm(okCount, foreignCount, unreadableCount,
                                          effectiveMin), foreignCount);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Little-endian U32 from four U8 reads. Caller ensures Readable before calling.</summary>
    private static uint ReadU32(IGameMemory mem, long addr)
        => (uint)(mem.U8(addr) | (mem.U8(addr + 1) << 8) | (mem.U8(addr + 2) << 16) | (mem.U8(addr + 3) << 24));
}
