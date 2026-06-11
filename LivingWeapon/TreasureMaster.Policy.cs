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
///      DecideArm       -- three-outcome arm gate: all ok -> Arm; any Foreign -> immediate
///                         Disarm; unreadables only -> Retry with grace window (the Plague
///                         grace-window lesson), Disarm after attemptCap.
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

    /// <summary>Outcome of a per-tick address audit.</summary>
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
    ///   <item>Any <paramref name="foreignCount"/> > 0 -> <see cref="ArmVerdict.Disarm"/>
    ///     immediately (wrong map or shifted buffers -- do not write).</item>
    ///   <item>All addresses ok, none unreadable -> <see cref="ArmVerdict.Arm"/>.</item>
    ///   <item>Unreadable addresses only (load-race grace window) -> <see cref="ArmVerdict.Retry"/>
    ///     while <paramref name="attempt"/> &lt; <paramref name="attemptCap"/>, then
    ///     <see cref="ArmVerdict.Disarm"/>.</item>
    /// </list>
    /// Modeled on the Plague grace-window lesson: transient read failures suspend writes
    /// immediately but disarm only after a finite grace.
    /// </summary>
    internal static ArmVerdict DecideArm(
        int okCount, int foreignCount, int unreadableCount,
        int attempt, int attemptCap)
    {
        if (foreignCount > 0)
            return ArmVerdict.Disarm;

        if (unreadableCount == 0)
            return ArmVerdict.Arm;

        // Unreadables only -- grace window.
        return attempt < attemptCap ? ArmVerdict.Retry : ArmVerdict.Disarm;
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
}
