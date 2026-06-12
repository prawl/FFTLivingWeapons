using System;

namespace LivingWeapon;

/// <summary>
/// Pure statics behind the Treasure Master module -- no memory access, so they're
/// unit-tested directly. The stateful orchestrator (the tick loop, write+hold, four-layer
/// containment) lives in TreasureMaster.cs (a later stage).
///
/// Contract index:
///   1. MapIdValid      -- 1..127 are the only live-battle map ids (FFTHandsFree contract);
///                         0 = uninitialized, 128+ invalid.
///   2. Fnv1a64         -- standard FNV-1a 64-bit; shared verbatim with the Python capture
///                         tool (gen_treasure_db.py self-test) so cross-language drift is a
///                         compile-time fact, not a runtime surprise.
///   3. AddrState /
///      ClassifyAddr    -- per-byte safety contract over the only legitimate values
///                         {0x00, 0x01, 0x80, 0x81}; anything else is Foreign and is never
///                         written.
///   4. WantWrite       -- OR-only by construction: cur | 0x80. No Clear path exists in this
///                         module.
///   5. ArmVerdict /
///      DecideArm       -- two-outcome arm gate: okCount >= minPlausible -> Arm;
///                         otherwise -> Retry (infinite patience). Foreign bytes are NOT a
///                         veto -- they are off-screen render bytes (camera pan / action
///                         camera) that return to Resting when the tile scrolls back. Per-
///                         write gating in TileHolder (L3) guarantees they are never written.
///                         The Disarm verdict is reserved for fingerprint mismatches only
///                         (handled in TreasureMaster.TickArming, not DecideArm).
///   6. BuildKeyMatches -- exact equality on both PE header fields (TimeDateStamp +
///                         SizeOfImage); a single-field mismatch is a hard global disarm
///                         until re-capture.
/// </summary>
internal sealed partial class TreasureMaster
{
    // ---- #3 per-byte classification ----

    /// <summary>
    /// The runtime-visible states of a tile's render-flag byte.
    /// Only <see cref="Resting"/> bytes are candidates for a <see cref="WantWrite"/>; a
    /// <see cref="Held"/> byte is already marked; a <see cref="Foreign"/> byte is never written.
    /// </summary>
    internal enum AddrState { Resting, Held, Foreign }

    // ---- #5 arm verdict ----

    /// <summary>Outcome of a per-tick address audit.
    /// <see cref="Disarm"/> is no longer returned by <see cref="DecideArm"/> (foreign bytes
    /// at arm time are off-screen render bytes, not a signal to disarm). It remains in the enum
    /// for the fingerprint-mismatch path in <see cref="TickArming"/>, which bypasses DecideArm.</summary>
    internal enum ArmVerdict { Arm, Retry, Disarm }

    // ---- #1 MapIdValid ----

    /// <summary>
    /// True for map ids 1..127 -- the FFTHandsFree LiveBattleMapId valid range.
    /// 0 is the uninitialized value (address not yet populated); 128+ are not assigned.
    /// Never read outside <see cref="BattleState.InLiveBattle"/>.
    /// </summary>
    internal static bool MapIdValid(byte id) => id >= 1 && id <= 127;

    // ---- #2 Fnv1a64 ----

    // Pinned constants -- must stay bit-for-bit identical to the Python capture tool.
    private const ulong FnvBasis = 0xcbf29ce484222325UL;
    private const ulong FnvPrime = 0x00000100000001b3UL;

    /// <summary>
    /// Standard FNV-1a 64-bit hash. Pinned test vectors (also in the Python self-test):
    ///   empty   -> 0xcbf29ce484222325
    ///   "a"     -> 0xaf63dc4c8601ec8c
    ///   "foobar" -> 0x85944171f73967e8
    /// </summary>
    internal static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong h = FnvBasis;
        foreach (byte b in data)
        {
            h ^= b;
            h *= FnvPrime;
        }
        return h;
    }

    // ---- #3 ClassifyAddr ----

    /// <summary>
    /// Maps a raw byte from the tile's render-flag address to its <see cref="AddrState"/>.
    /// Legitimate values are exactly {0x00, 0x01, 0x80, 0x81}:
    ///   bit 0x80 set, no other high bits (i.e. byte masked to 0xFE == 0x80) -> Held.
    ///   0x00 or 0x01 (bit 0x80 clear, no stray high bits)                   -> Resting.
    ///   anything else                                                         -> Foreign.
    /// The low bit is engine-driven don't-care; the runtime audits with it elsewhere.
    /// </summary>
    internal static AddrState ClassifyAddr(byte cur)
    {
        // Mask off the engine don't-care low bit; then test the high byte.
        byte masked = (byte)(cur & 0xFE);
        return masked switch
        {
            0x80 => AddrState.Held,
            0x00 => AddrState.Resting,
            _    => AddrState.Foreign,
        };
    }

    // ---- #4 WantWrite ----

    /// <summary>
    /// The value to write for this byte: <paramref name="cur"/> OR 0x80.
    /// OR-only by construction -- no Clear path exists in this module; the engine clears
    /// marks itself (ledger-proven).
    /// </summary>
    internal static byte WantWrite(byte cur) => (byte)(cur | 0x80);

    // ---- #5 DecideArm ----

    /// <summary>
    /// Per-tick arm decision from an address audit summary.
    /// <list type="bullet">
    ///   <item><paramref name="okCount"/> &gt;= <paramref name="minPlausible"/> ->
    ///     <see cref="ArmVerdict.Arm"/>. Foreign bytes are not a veto: they are off-screen
    ///     render bytes (camera pan, action camera) that return to Resting when the tile
    ///     scrolls back into view. Per-write gating in <see cref="TileHolder.Hold"/> ensures
    ///     they are never written.</item>
    ///   <item>Otherwise -> <see cref="ArmVerdict.Retry"/>. The module polls until enough
    ///     tiles are on-screen. The caller logs once when attempts pass
    ///     <see cref="Tuning.TreasureArmAttemptCap"/>.</item>
    /// </list>
    /// Never returns <see cref="ArmVerdict.Disarm"/> -- that path lives in
    /// <see cref="TickArming"/> for fingerprint mismatches only.
    /// </summary>
    internal static ArmVerdict DecideArm(
        int okCount, int foreignCount, int unreadableCount,
        int minPlausible)
    {
        return okCount >= minPlausible ? ArmVerdict.Arm : ArmVerdict.Retry;
    }

    // ---- #6 BuildKeyMatches ----

    /// <summary>
    /// True when the dataset's baked PE header fields exactly match the live header.
    /// A single-field mismatch means the game was patched since capture; the module
    /// globally disarms and logs once until the dataset is rebuilt.
    /// </summary>
    internal static bool BuildKeyMatches(
        uint dsTimeDateStamp, uint dsSizeOfImage,
        uint liveTimeDateStamp, uint liveSizeOfImage) =>
        dsTimeDateStamp == liveTimeDateStamp && dsSizeOfImage == liveSizeOfImage;

    // ---- #7 MaskedTerrainHash (fingerprint v2) and MaskedTerrainHashV3 ----

    // Grid layout: 208 records x 7 bytes each.  Field 1 and field 6 are live state
    // (change during play); field 0 (tile height) is pure map geometry.  The v1
    // fingerprint hashed all 1456 raw bytes and falsely detected changes when field 1
    // or field 6 mutated mid-battle (LIVE INCIDENT #2).  V2 hashes only field 0 of
    // every record, making the fingerprint immune to in-battle field mutations.
    //
    // LIVE INCIDENT #3 (Zeirchele Falls, map 83, water map): field 0 (height), field 1
    // (slope), and field 6 (flow) ALL animate on water/lava maps, so v2 (field-0 only)
    // cycles with the animation and triggers spurious disarm/re-arm.  V3 hashes fields
    // {2,3,4,5} only -- those are static geometry on all map types.
    private const int TerrainRecordLen = 7;   // bytes per terrain record

    /// <summary>
    /// Fingerprint v2: FNV-1a64 over field-0 bytes only (byte at offset i*7) extracted
    /// from the raw terrain window.  Records beyond the buffer length are silently skipped
    /// (partial reads are handled by the caller returning false before this is reached).
    ///
    /// Pinned test vector (shared with the Python self-test -- never let them drift):
    ///   raw = [ 01 02 03 04 05 06 07 | 11 12 13 14 15 16 17 ]  (2 records)
    ///   field-0 bytes = [0x01, 0x11]
    ///   result = Fnv1a64([0x01, 0x11]) = 0x082f3307b4e8a9a7
    ///
    /// Kept for dual-version support: 10 dry-land maps captured with fpVer=2 continue to use this.
    /// Water/lava maps use MaskedTerrainHashV3 instead.
    /// </summary>
    internal static ulong MaskedTerrainHash(ReadOnlySpan<byte> raw)
    {
        int numRecords = raw.Length / TerrainRecordLen;
        ulong h = FnvBasis;
        for (int i = 0; i < numRecords; i++)
        {
            byte b = raw[i * TerrainRecordLen];  // field 0 only
            h ^= b;
            h *= FnvPrime;
        }
        return h;
    }

    /// <summary>
    /// Fingerprint v3: FNV-1a64 over bytes at positions where (pos % 7) is in {2,3,4,5}
    /// -- i.e. fields {2,3,4,5} of each record.  These are the static geometry fields;
    /// fields {0,1,6} (height, slope, flow) animate on water/lava maps and are excluded.
    ///
    /// Pinned test vector (shared with the Python self-test and gen_treasure_db.py):
    ///   raw = [ 01 02 03 04 05 06 07 | 11 12 13 14 15 16 17 ]  (2 records)
    ///   fields {2,3,4,5} bytes = [03 04 05 06 13 14 15 16]
    ///     (indices 2,3,4,5 from record 0; indices 9,10,11,12 from record 1)
    ///   result = Fnv1a64([03 04 05 06 13 14 15 16]) = 0x05708f90b5f5fac5
    /// </summary>
    internal static ulong MaskedTerrainHashV3(ReadOnlySpan<byte> raw)
    {
        ulong h = FnvBasis;
        for (int i = 0; i < raw.Length; i++)
        {
            int fieldOff = i % TerrainRecordLen;
            if (fieldOff >= 2 && fieldOff <= 5)
            {
                h ^= raw[i];
                h *= FnvPrime;
            }
        }
        return h;
    }
}
