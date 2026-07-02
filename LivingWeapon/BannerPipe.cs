using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// The isolated native surface for the tier-up toast: locates the game's own battle-callout
/// text holder, then hijacks its show on a rising edge by calling the game's own
/// SetTextStringAndCommit. Migrated from BannerSpike.cs (the dev-only spike that proved the
/// hijack live 2026-07-02) into a production module gated by Config.BannerToasts.
///
/// This is the ONE production module that calls game code. The call runs on the mod's loop
/// thread while the UI thread may be mid-show -- a race whose risk is accepted on the strength
/// of the spike's ~10 crash-free call runs (including the on-screen hijack itself) and the
/// Config kill-switch (BannerToast never polls when disabled, so this call site goes unreached).
/// Commit fires only on a rising show edge with a queued toast (BannerToast.cs), so live
/// exposure is a handful of calls per session, not a hot loop.
///
/// Locate is lazy + budgeted (Tick, called every in-battle tick): settle 300 ticks (the spike's
/// ValidateAtTick precedent -- UI objects may not exist yet at process start), then sweep
/// writable-heap regions (IGameMemory.Regions(), already MEM_COMMIT+MEM_PRIVATE+writable
/// filtered by Mem.Regions) in 1 MB slices, up to 8 MB/tick, resuming across ticks via a cursor.
/// A sweep that finds exactly one candidate caches it; zero or multiple goes INERT for the
/// battle -- committing to a wrong same-class instance can AV inside the native call, so
/// ambiguity fails closed, never blind. ResetBattle revalidates the cache (or re-arms the scan)
/// every battle boundary.
///
/// Locate + validate + call ONLY -- the wording/queue/poll-decision logic lives in BannerToast.cs.
/// </summary>
internal sealed class BannerPipe : ICalloutPipe
{
    // Heap object PROVEN LAUNCH-STABLE 2026-07-02 (validated by its static vtable/id every
    // battle before anything is dereferenced; held across 3+ relaunches in the spike). Internal
    // (not private) so the locate/revalidate tests can fabricate a matching holder in FakeHeap.
    internal const long HolderVtable = 0x140718278;
    internal const ulong HolderId = 0x999;    // holder+0x08 (unique among 538 class instances)
    // The game's own "set literal text + commit to the inner widget" method (traced from the
    // banner orchestrator at 0x140111E89; call proven executed in-process by read-back).
    private const long FnSetTextCommit = 0x14028F720;   // rcx = holder, rdx = char* (ANSI)

    private const int SettleTicks = 300;             // ~10s at 33ms -- BannerSpike's ValidateAtTick
    private const long ChunkBytes = 0x100000;         // 1 MB per TryReadBytes slice
    private const long TickBudgetBytes = 0x800000;    // 8 MB scanned per tick
    private const int MaxPayloadLen = 96;             // the bubble is a nine-grid; guard a runaway string

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SetTextCommitFn(nint holder, nint text);

    private readonly IGameMemory _mem;

    private int _tick;
    private bool _settled;
    private bool _inert;
    private long? _holder;

    private List<(long baseAddr, long size)>? _regions;
    private int _regionIdx;
    private long _offsetInRegion;
    private readonly List<long> _found = new();

    public BannerPipe(IGameMemory? mem = null) => _mem = mem ?? new LiveMemory();

    /// <summary>One in-battle tick: settle, then sweep for the holder, budgeted across ticks.
    /// No-op once located (or gone inert for the battle).</summary>
    public void Tick()
    {
        if (_inert || _holder != null) return;
        if (!_settled)
        {
            if (++_tick < SettleTicks) return;
            _settled = true;
        }
        ContinueSweep();
    }

    private void ContinueSweep()
    {
        _regions ??= new List<(long, long)>(_mem.Regions());
        long budget = TickBudgetBytes;
        while (budget > 0 && _regionIdx < _regions.Count)
        {
            var (baseAddr, size) = _regions[_regionIdx];
            long regionEnd = baseAddr + size;
            long addr = baseAddr + _offsetInRegion;
            if (addr >= regionEnd)
            {
                _regionIdx++;
                _offsetInRegion = 0;
                continue;
            }
            long chunkLen = Math.Min(ChunkBytes, regionEnd - addr);
            if (_mem.TryReadBytes(addr, (int)chunkLen, out byte[] buf))
                ScanChunk(addr, buf);
            _offsetInRegion += chunkLen;
            budget -= chunkLen;
        }
        if (_regionIdx >= _regions.Count) CompleteSweep();
    }

    /// <summary>8-ALIGNED scan for the vtable pointer's byte pattern (absolute chunk addresses
    /// are always 8-aligned: region bases come from page-aligned VirtualQuery results and
    /// ChunkBytes is a multiple of 8, so no aligned match can straddle a chunk boundary). A
    /// vtable hit is confirmed a candidate only when the id qword at +0x08 also matches -- a
    /// LIVE read (not the buffered bytes), mirroring BannerSpike's identity-check discipline.</summary>
    private void ScanChunk(long chunkBase, byte[] buf)
    {
        byte[] pattern = BitConverter.GetBytes(HolderVtable);
        for (int off = 0; off + 8 <= buf.Length; off += 8)
        {
            bool match = true;
            for (int j = 0; j < 8; j++)
                if (buf[off + j] != pattern[j]) { match = false; break; }
            if (!match) continue;
            long hit = chunkBase + off;
            if (_mem.U64(hit + 8) == HolderId) _found.Add(hit);
        }
    }

    private void CompleteSweep()
    {
        if (_found.Count == 1)
        {
            long h = _found[0];
            _holder = h;
            Log.Info($"banner-pipe: callout holder located at 0x{h:X} (idle len 0x{_mem.U32(h + 0x30):X} cap 0x{_mem.U32(h + 0x38):X})");
        }
        else
        {
            Log.Info($"banner-pipe: locate found {_found.Count} candidate(s) -- inert until next battle");
            _inert = true;
        }
        _regions = null;
        _found.Clear();
    }

    /// <summary>Battle-edge revalidation. A cached holder that still checks out is kept as-is
    /// (launch-stable -- no need to rescan every battle). Anything else -- never located, gone
    /// inert, or a cache that failed revalidation -- re-arms the scan from scratch.</summary>
    public void ResetBattle()
    {
        if (_holder is long h && _mem.U64(h) == HolderVtable && _mem.U64(h + 8) == HolderId)
            return;
        if (_holder != null)
            Log.Info($"banner-pipe: cached holder 0x{_holder:X} failed revalidation -- rescanning");
        _holder = null;
        _inert = false;
        _tick = 0;
        _settled = false;
        _regions = null;
        _regionIdx = 0;
        _offsetInRegion = 0;
        _found.Clear();
    }

    public IReadOnlyList<long> Candidates() => _holder is long h ? new[] { h } : Array.Empty<long>();

    /// <summary>Validated in-flight payload swap. Revalidates FIRST -- never calls through a
    /// failed validation -- then truncates an oversized payload and calls the game's own setter.
    /// The native call is wrapped in try/catch for MANAGED exceptions only: an access violation
    /// inside game code is uncatchable and would crash the whole process, which is exactly why
    /// the validation above is mandatory rather than a safety net.</summary>
    public void Commit(long holder, string payload)
    {
        if (_mem.U64(holder) != HolderVtable || _mem.U64(holder + 8) != HolderId)
        {
            Log.Error($"banner-pipe: commit aborted -- holder 0x{holder:X} failed revalidation");
            return;
        }
        string safe = payload;
        if (safe.Length > MaxPayloadLen)
        {
            Log.Info($"banner-pipe: payload truncated from {safe.Length} to {MaxPayloadLen} chars");
            safe = safe.Substring(0, MaxPayloadLen);
        }
        nint text = Marshal.StringToHGlobalAnsi(safe);   // names are ASCII today; not codepage-safe beyond that
        try
        {
            var setText = Marshal.GetDelegateForFunctionPointer<SetTextCommitFn>(unchecked((nint)FnSetTextCommit));
            setText(unchecked((nint)holder), text);
            Log.Info($"banner-toast: \"{safe}\" hijacked onto the next callout");
        }
        catch (Exception ex)
        {
            Log.Error("banner-pipe: managed exception during commit -- " + ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }
}
