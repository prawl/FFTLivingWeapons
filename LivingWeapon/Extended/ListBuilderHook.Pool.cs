using System;
using System.Threading;

namespace LivingWeapon;

/// <summary>
/// D3's pool-lifecycle half of <see cref="ListBuilderHook"/>: the per-slot try-acquire/release and
/// the copy-back that reads what the original wrote into a slot and truncates it into the caller's
/// real buffer. More than one slot exists for REENTRANCY (the builder or the game re-entering the
/// detour on the same thread), not for concurrency (Q8 stays an assumption the design does not
/// lean on). All four busy is never observed in practice; the only safe refusal is an empty list
/// through the guarded patcher, counted once and Warned once per session.
///
/// F1 (plan v1.2, Phase 4 BLOCKER): EVERY failure path below -- a failed slot read, a refused
/// caller-buffer write, or an exception in between (ListBuilderHook.Process's own outer catch) --
/// returns 0 with a best-effort empty terminator, NEVER the original's own count. The original's
/// world lives in the POOL SLOT (r9 pointed there, never at the caller's real buffer), so handing
/// back its count without ever having written the caller's buffer would tell a stack caller (e.g.
/// fnA, told eax = 200) to append its own two hand items at index 200 and write ITS terminator past
/// the GS cookie: silent corruption of a higher frame, strictly worse than the honest empty list.
/// </summary>
internal sealed partial class ListBuilderHook
{
    private long _truncations;
    private long _poolExhausted;
    private bool _exhaustionWarned;
    private long _copyBackRefusals;
    private bool _copyBackWarned;

    /// <summary>Stack-caller calls whose list was cut down to <see cref="StackCallerCap"/>.</summary>
    public long Truncations => Interlocked.Read(ref _truncations);
    /// <summary>Calls that found every pool slot busy and refused with an empty list (D3).</summary>
    public long PoolExhaustedCount => Interlocked.Read(ref _poolExhausted);
    /// <summary>Calls whose copy-back could not complete safely (a failed slot read, a refused
    /// caller-buffer write, or a thrown exception in between) and refused with an empty list
    /// instead of the original's own count (F1, v1.2).</summary>
    public long CopyBackRefusals => Interlocked.Read(ref _copyBackRefusals);

    /// <summary>Reads back what the original wrote into the pool slot and truncates it into the
    /// caller's real buffer. The read is clamped to the slot's own capacity (minus one word of
    /// margin) so a corrupted/oversized count can never walk the read past the page this mod owns
    /// -- Q11 (the builder always writes exactly cap + 1 words) says a truthful count never needs
    /// the clamp, so it only ever bites a lying/corrupted one. A failed read or a refused write is
    /// answered by <see cref="CopyBackRefused"/> (F1): 0, never <paramref name="count"/>. Q12
    /// (v1.3): a legitimately empty build (<paramref name="count"/> == 0) is short-circuited BEFORE
    /// the slot read -- <c>LiveCodePatcher.TryRead</c> refuses <c>count &lt;= 0</c> while the test
    /// fake accepts it, so taking the zero-length read at all would fire a false
    /// <see cref="CopyBackRefused"/> live (right bytes, wrong diagnostic: an empty list is a
    /// result, not a refusal).</summary>
    private int CopyBack(nint list, long slotAddr, int count)
    {
        if (count <= 0) { _mem.TryWrite((long)list, TruncatedTail(Array.Empty<ushort>(), 0)); return 0; }
        int n = Math.Clamp(count, 0, SlotBytes / 2 - 1);
        if (!_mem.TryRead(slotAddr, n * 2, out var raw)) { CopyBackRefused(list, "the pool slot refused the read-back"); return 0; }
        var words = ToWords(raw);
        var tail = TruncatedTail(words, StackCallerCap);
        if (!_mem.TryWrite((long)list, tail)) { CopyBackRefused(list, "the caller's buffer refused the truncated copy-back write"); return 0; }
        int kept = TruncatedCount(words, StackCallerCap);
        if (kept < words.Length) Interlocked.Increment(ref _truncations);
        return kept;
    }

    /// <summary>F1 (v1.2): the one safe answer to a copy-back that could not complete -- a
    /// best-effort empty-list write (never the original's own count) through the guarded patcher,
    /// counted and Warned once per session, Debug after (the PoolExhausted idiom). Called both from
    /// <see cref="CopyBack"/>'s own failed-read/failed-write branches and from
    /// <see cref="Process"/>'s outer catch (an exception can reach there before CopyBack's own
    /// write attempt ever runs, e.g. a throw on the read itself), so it must be self-guarding the
    /// same way <see cref="PoolExhausted"/> is.</summary>
    private void CopyBackRefused(nint list, string why)
    {
        Interlocked.Increment(ref _copyBackRefusals);
        try { _mem.TryWrite((long)list, TruncatedTail(Array.Empty<ushort>(), 0)); }
        catch (Exception) { /* best-effort: nothing more can safely be done here */ }
        string line = $"The item list at 0x{(long)list:X} could not be copied back safely ({why}); the caller was given an empty list instead of a possibly-untruncated one.";
        if (_copyBackWarned) { SafeLog(() => ModLogger.Debug(LogVerb.Engine, line)); return; }
        _copyBackWarned = true;
        SafeLog(() => ModLogger.Warn(LogVerb.Engine, line));
    }

    /// <summary>D3/Q11: the only safe refusal when every slot is busy is an empty list -- there is
    /// no buffer left it would be safe to hand the original at all (Process never calls it in this
    /// branch), so unlike <see cref="CopyBackRefused"/> there is no "the original's count" to avoid
    /// returning in the first place.</summary>
    private void PoolExhausted(nint list)
    {
        Interlocked.Increment(ref _poolExhausted);
        // Process calls this OUTSIDE any try (there is no original call in this branch to protect
        // a try/finally around), so the write itself must be self-guarding: ICodePatcher's own
        // contract is "never throws", but the hot-path detour's own contract is stronger still --
        // it must not throw even if that contract is ever violated.
        try { _mem.TryWrite((long)list, TruncatedTail(Array.Empty<ushort>(), 0)); }
        catch (Exception) { /* best-effort: nothing more can safely be done here */ }
        if (_exhaustionWarned) return;
        _exhaustionWarned = true;
        SafeLog(() => ModLogger.Warn(LogVerb.Engine,
            "Every list-builder pool slot was busy at once (never observed before this session); a menu briefly listed nothing, nothing was corrupted (LW-372 D3)."));
    }

    internal int AcquireSlot()
    {
        for (int i = 0; i < PoolSlots; i++)
            if (Interlocked.CompareExchange(ref _busy[i], 1, 0) == 0) return i;
        return -1;
    }

    internal void ReleaseSlot(int index) => Interlocked.Exchange(ref _busy[index], 0);
}
