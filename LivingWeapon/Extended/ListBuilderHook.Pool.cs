using System;
using System.Threading;

namespace LivingWeapon;

/// <summary>
/// The scratch slots a stack caller's list is built in, and the copy that brings it back cut to size.
/// There are several so the builder can call back into itself on one thread. With all of them busy
/// the caller gets an empty list, which is counted and warned about once a session.
///
/// Every way this can fail gives back 0 and, if it can, an empty list. The game wrote its list into
/// our slot, not the caller's buffer, so passing on its count (say 200 to fnA) would make the caller
/// append at index 200 and write its terminator past the stack cookie.
/// </summary>
internal sealed partial class ListBuilderHook
{
    private long _truncations;
    private long _poolExhausted;
    private bool _exhaustionWarned;
    private long _copyBackRefusals;
    private bool _copyBackWarned;

    /// <summary>Stack caller lists that were longer than <see cref="StackCallerCap"/> and got cut.</summary>
    public long Truncations => Interlocked.Read(ref _truncations);
    /// <summary>Calls that found every pool slot busy and got an empty list.</summary>
    public long PoolExhaustedCount => Interlocked.Read(ref _poolExhausted);
    /// <summary>Calls whose copy back failed (a slot read, a caller write, or a throw) and got an empty
    /// list instead of the game's count.</summary>
    public long CopyBackRefusals => Interlocked.Read(ref _copyBackRefusals);

    /// <summary>Copies the slot's list into the caller's buffer, cut to fit. The read stops one word short of the slot's
    /// end, so a garbage count cannot read past our page.</summary>
    /// <remarks>A zero count skips the read, because <c>LiveCodePatcher.TryRead</c> refuses <c>count &lt;= 0</c> and would
    /// log a refusal that never happened.</remarks>
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

    /// <summary>Hands the caller an empty list if it can, warns once a session and logs at Debug after. <see cref="Process"/>'s
    /// catch can call it before CopyBack has written anything, so it guards its own write like <see cref="PoolExhausted"/> does.</summary>
    private void CopyBackRefused(nint list, string why)
    {
        Interlocked.Increment(ref _copyBackRefusals);
        try { _mem.TryWrite((long)list, TruncatedTail(Array.Empty<ushort>(), 0)); }
        catch (Exception) { /* nothing more can safely be done from inside the game */ }
        string line = $"The item list at 0x{(long)list:X} could not be copied back safely ({why}); the caller was given an empty list instead of a possibly-untruncated one.";
        if (_copyBackWarned) { SafeLog(() => ModLogger.Debug(LogVerb.Engine, line)); return; }
        _copyBackWarned = true;
        SafeLog(() => ModLogger.Warn(LogVerb.Engine, line));
    }

    /// <summary>With every slot busy there is nowhere to build the list, so the caller gets an empty one.</summary>
    private void PoolExhausted(nint list)
    {
        Interlocked.Increment(ref _poolExhausted);
        // Nothing above catches for this call, so the write guards itself in case ICodePatcher ever throws.
        try { _mem.TryWrite((long)list, TruncatedTail(Array.Empty<ushort>(), 0)); }
        catch (Exception) { /* nothing more can safely be done from inside the game */ }
        if (_exhaustionWarned) return;
        _exhaustionWarned = true;
        SafeLog(() => ModLogger.Warn(LogVerb.Engine,
            "Every list-builder pool slot was busy at once (never observed before this session); a menu briefly listed nothing, nothing was corrupted."));
    }

    internal int AcquireSlot()
    {
        for (int i = 0; i < PoolSlots; i++)
            if (Interlocked.CompareExchange(ref _busy[i], 1, 0) == 0) return i;
        return -1;
    }

    internal void ReleaseSlot(int index) => Interlocked.Exchange(ref _busy[index], 0);
}
