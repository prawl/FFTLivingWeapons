using System;
using System.Threading;

namespace LivingWeapon;

/// <summary>
/// Pool-slot lifecycle for <see cref="ListBuilderHook"/>: try-acquire/release per slot, and the
/// copy-back that truncates a slot's list into the caller's buffer. Several slots exist for reentrancy
/// on one thread, not concurrency. If all are busy the answer is an empty list, counted and
/// Warned once per session.
///
/// F1 (plan v1.2). Every failure path returns 0 with a best-effort empty terminator, never the
/// original's count. The original wrote into the pool slot, not the caller's buffer, so returning its
/// count (e.g. 200 to fnA) would make the caller append at index 200 and write its terminator past
/// the GS cookie.
/// </summary>
internal sealed partial class ListBuilderHook
{
    private long _truncations;
    private long _poolExhausted;
    private bool _exhaustionWarned;
    private long _copyBackRefusals;
    private bool _copyBackWarned;

    /// <summary>Stack-caller calls cut down to <see cref="StackCallerCap"/>.</summary>
    public long Truncations => Interlocked.Read(ref _truncations);
    /// <summary>Calls that found every pool slot busy and got an empty list.</summary>
    public long PoolExhaustedCount => Interlocked.Read(ref _poolExhausted);
    /// <summary>Calls whose copy-back failed (slot read, caller write, or exception) and got an empty
    /// list instead of the original's count (F1).</summary>
    public long CopyBackRefusals => Interlocked.Read(ref _copyBackRefusals);

    /// <summary>Copies the slot's list into the caller's buffer, truncated. The read is clamped to the
    /// slot capacity minus one word, so a corrupt count cannot read past our page (a truthful
    /// count never needs it). Failures go to <see cref="CopyBackRefused"/> (F1). A zero count skips
    /// the read, since <c>LiveCodePatcher.TryRead</c> refuses <c>count &lt;= 0</c> and
    /// would log a false refusal.</summary>
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

    /// <summary>F1. Writes a best-effort empty list through the guarded patcher, counted and Warned once
    /// per session, Debug after. Also called from <see cref="Process"/>'s outer catch, which can fire
    /// before CopyBack writes anything, so it guards itself like <see cref="PoolExhausted"/>.</summary>
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

    /// <summary>With every slot busy there is no buffer to hand the original, so the only
    /// answer is an empty list.</summary>
    private void PoolExhausted(nint list)
    {
        Interlocked.Increment(ref _poolExhausted);
        // Called outside any try, so the write must not throw even if ICodePatcher's never-throws promise breaks.
        try { _mem.TryWrite((long)list, TruncatedTail(Array.Empty<ushort>(), 0)); }
        catch (Exception) { /* best-effort: nothing more can safely be done here */ }
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
