#if LWDEV
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// DEV-ONLY spike (LW-27, docs/TODO.md Now section; FlavorSpike/ShowSpike precedent, same
/// #if LWDEV whole-file wrap): the header-repaint research instrument. The equip card's brown
/// header label ("Description") is generic UI chrome the owner wants repainted per weapon
/// (target shape "Kills: 100, Mageslayer"). Unknowns only a live instrument can answer: where
/// the header text buffers live relative to the card body Display/CardSites already paints,
/// whether a write HOLDS across card refreshes or gets re-derived, what the real slot capacity
/// is, and which encodings exist on screen. This spike is the measuring instrument, not the
/// feature.
///
/// Key: F6 (one-shot arm; press again anytime to clear the hit list and re-scan). F6 is
/// deliberately SHARED with ShowSpike's commit-tap and FlavorSpike's P4 arm (the established
/// shared-key convention; in the out-of-battle menus this spike targets, only FlavorSpike
/// co-fires, since ShowSpike ticks on the battle path). F3/F7/F9 are eaten LL-hooks on the
/// owner's machine, and F8 stopped registering there 2026-07-03 (re-confirmed live 2026-07-05
/// on this spike's first flight), which leaves F6 as the one proven-delivered key.
///
/// SCAN SHAPE (rebuilt 2026-07-05 from the first live flight): the first flight's one-shot scan
/// walked committed regions bottom-up and hit its 512MB cap entirely inside LOW addresses before
/// ever reaching the HIGH addresses where the real on-screen strings live (owner's Cheat Engine
/// hits: 0x4D9BFBF253, 0x4EDBAB64B0, 0x4F5BFA5CF5). Worse, that whole 512MB walk ran
/// synchronously inside one Engine.Tick call, which blocked the shared tick thread long enough
/// to swallow AttackCardSpike's independent F6 edge check on the very same press (see
/// AttackCardSpike.cs's class doc for that diagnosis). The press now snapshots regions once,
/// sorts them descending by base address (ScanCursor.SortDescending, so the walk hits the high
/// addresses first) and stores a ScanCursor.RegionCursor; each SUBSEQUENT Tick advances the walk
/// by PerTickBudgetBytes (see that constant's doc for why 48MB) via ScanCursor.NextSlice, in
/// both ASCII and UTF16LE, exact-boundary matched via HeaderProbeText.IsExactHit so
/// "Descriptions"/"XDescription" style false hits are excluded. There is no longer a total-bytes
/// cap: the walk simply continues, one budgeted slice per tick, until the snapshot is fully
/// covered or the 200-hit cap trips. For each accepted hit, logs the absolute address, encoding,
/// a 48-byte-radius context dump (HeaderProbeText.FormatContext), and the distance to the
/// nearest known card-site anchor (from the injected CardSites; "no sites known" when the cache
/// is empty, meaning: open the equip card first so the sweep finds sites), then immediately
/// attempts the guarded write for that hit (per chunk, not batched to the end).
///
/// The write is a guarded same-length overwrite of HeaderProbeText.Payload ("Kills: 100 ", the
/// same length as the label by construction). Writable-gated, and the label bytes are
/// re-verified immediately before the write (never touches a buffer that no longer holds
/// "Description": the race guard against a freed/reused UI buffer). Never writes anything longer
/// than the label.
///
/// After the press, every written address is cheaply re-read (at most once a second, no
/// rescanning) and a line is logged ONLY on a state CHANGE: holding is the expected silent
/// steady state; a line fires when a hit REVERTS to the original label (with elapsed ms since
/// the write) or becomes unreadable. A second F6 press re-arms a fresh scan (aborting any
/// in-progress walk), dropping the previous hit list first.
/// </summary>
internal sealed class HeaderSpike
{
    private const int VkF6 = 0x75;
    private const int HitCap = 200;
    private const long RecheckIntervalMs = 1000;
    private const long ProgressIntervalMs = 1000;

    // 48MB per tick: the loop runs ~33ms, and the first live flight's whole 512MB gulp (roughly
    // 129 4MB chunks, each scanned twice for ASCII+UTF16) evidently took long enough in one
    // synchronous call to swallow a sibling spike's keypress edge (see the class doc above and
    // AttackCardSpike.cs's root-cause account). 48MB amortizes that same total walk across about
    // 11 ticks (a sub-second, tolerable hitch spread over real time) while keeping any SINGLE
    // tick's blocking window a small fraction of a human keypress's hold duration (tens of ms of
    // work against a press typically held 50 to 150ms or more), which is what actually fixes the
    // starvation: no measurement was taken, this is a deliberately conservative budget, not a
    // tuned one.
    private const long PerTickBudgetBytes = 48L * 1024 * 1024;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private sealed class Hit
    {
        public long Addr;
        public int Enc;
        public bool Written;
        public long WrittenAtMs;
        public bool Reverted;
        public bool Unreadable;
    }

    private readonly IGameMemory _mem;
    private readonly CardSites _sites;
    private readonly ChunkReader _reader;
    private readonly byte[] _pat1;
    private readonly byte[] _pat2;
    private readonly byte[] _pay1;
    private readonly byte[] _pay2;
    private readonly List<Hit> _hits = new();
    private bool _f6Was;
    private long _lastRecheckMs;

    // Walk-in-progress state, armed fresh on every F6 press.
    private bool _scanning;
    private List<(long rbase, long rsize)> _regionsDesc = new();
    private List<CardSites.Site> _siteSnapshot = new();
    private RegionCursor _cursor;
    private long _scanned;
    private long _totalBytes;
    private int _hitsAscii;
    private int _hitsUtf16;
    private int _written;
    private int _skipped;
    private bool _capped;
    private Stopwatch _sw = new();
    private long _lastProgressMs;

    public HeaderSpike(IGameMemory mem, CardSites sites)
    {
        _mem = mem;
        _sites = sites;
        _reader = new ChunkReader(mem);
        _pat1 = HeaderProbeText.Pattern(1);
        _pat2 = HeaderProbeText.Pattern(2);
        _pay1 = HeaderProbeText.PayloadBytes(1);
        _pay2 = HeaderProbeText.PayloadBytes(2);
        ModLogger.Event(LogVerb.Trace, "header-spike: armed (F6 scans the heap for every on-screen \"Description\" header and overwrites it, walking high addresses first, a budgeted slice per tick; press again anytime to re-scan; F6 also fires the sibling spikes by design)");
    }

    /// <summary>Loop-thread tick: F6 edge-detect arms a fresh walk (no scanning work on the
    /// arming tick itself, so a slow sibling never eats this key check's turn); a walk in
    /// progress advances one budgeted slice; otherwise the cheap once-per-second recheck of
    /// whatever this instrument already wrote.</summary>
    public void Tick()
    {
        if (Pressed(VkF6, ref _f6Was)) { Arm(); return; }
        if (_scanning) { StepScan(); return; }
        Recheck();
    }

    private static bool Pressed(int vk, ref bool was)
    {
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        bool pressed = down && !was;
        was = down;
        return pressed;
    }

    /// <summary>Arms a fresh walk: snapshot regions, sort descending, reset every counter. Does
    /// no scanning itself, on purpose (see the Tick doc).</summary>
    private void Arm()
    {
        _hits.Clear();
        _reader.Snapshot();
        _regionsDesc = ScanCursor.SortDescending(_reader.Regions);
        _siteSnapshot = _sites.Snapshot();
        _cursor = RegionCursor.AtStart(_regionsDesc);
        _totalBytes = 0;
        foreach (var (_, rsize) in _regionsDesc) _totalBytes += rsize;
        _scanned = 0;
        _hitsAscii = 0;
        _hitsUtf16 = 0;
        _written = 0;
        _skipped = 0;
        _capped = false;
        _sw = Stopwatch.StartNew();
        _lastProgressMs = Environment.TickCount64;
        _scanning = true;
        ModLogger.Event(LogVerb.Trace, $"header-spike: scan started, {_regionsDesc.Count} regions ({_totalBytes} bytes) highest address first, {PerTickBudgetBytes / (1024 * 1024)}MB per tick");
    }

    /// <summary>Advances the walk by one budgeted slice. Never throws (catches and logs).</summary>
    private void StepScan()
    {
        try
        {
            var slice = ScanCursor.NextSlice(_regionsDesc, ref _cursor, PerTickBudgetBytes);
            foreach (var (rbase, rend, chunkStart) in slice)
            {
                if (_hits.Count >= HitCap) { _capped = true; break; }

                int read = _reader.ReadInRegion(chunkStart, rbase, rend, out int lookback, out int searchable);
                if (read == 0) continue;

                long bufBase = chunkStart - lookback;
                int windowEnd = lookback + searchable;
                ScanAndWrite(_reader.Buf, lookback, windowEnd, bufBase, _pat1, _pay1, 1, ref _hitsAscii);
                ScanAndWrite(_reader.Buf, lookback, windowEnd, bufBase, _pat2, _pay2, 2, ref _hitsUtf16);
                _scanned += read;
            }

            if (_hits.Count >= HitCap) _capped = true;
            if (!_capped && !_cursor.Done) { MaybeLogProgress(); return; }

            Finish();
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, "header-spike: scan failed; " + ex.Message);
            _scanning = false;
        }
    }

    private void MaybeLogProgress()
    {
        long now = Environment.TickCount64;
        if (now - _lastProgressMs < ProgressIntervalMs) return;
        _lastProgressMs = now;
        ModLogger.Event(LogVerb.Trace, $"header-spike: scanned {_scanned / (1024 * 1024)} of {_totalBytes / (1024 * 1024)} MB, {_hits.Count} hits so far");
    }

    private void Finish()
    {
        _sw.Stop();
        _scanning = false;

        if (_capped)
            ModLogger.Event(LogVerb.Trace, $"header-spike: hit cap reached ({_hits.Count} hits); stopped early, results below are partial");
        ModLogger.Event(LogVerb.Trace, $"header-spike: scan finished in {_sw.ElapsedMilliseconds}ms; {_scanned} bytes scanned, {_hitsAscii} ascii + {_hitsUtf16} utf16 exact hits");
        ModLogger.Event(LogVerb.Trace, $"header-spike: {_written} written, {_skipped} skipped of {_hits.Count} hits; holding is silent now, watch this log for a revert or unreadable line");
    }

    /// <summary>Find exact label hits in one chunk's search window, log each with its context
    /// dump and nearest known card-site anchor, record it for Recheck, and immediately attempt
    /// its guarded write.</summary>
    private void ScanAndWrite(byte[] buf, int lookback, int windowEnd, long bufBase, byte[] pat, byte[] payload,
                              int enc, ref int hitCounter)
    {
        var positions = new List<int>();
        ByteScan.FindAll(buf, pat, lookback, windowEnd, positions);

        foreach (int pos in positions)
        {
            if (_hits.Count >= HitCap) return;
            if (!HeaderProbeText.IsExactHit(buf, pos, enc)) continue;

            long addr = bufBase + pos;
            var hit = new Hit { Addr = addr, Enc = enc };
            _hits.Add(hit);
            hitCounter++;

            string ctx = HeaderProbeText.FormatContext(buf, pos, enc);
            string nearest = NearestSiteDescription(addr, _siteSnapshot);
            ModLogger.Event(LogVerb.Trace, $"header-spike: hit addr=0x{addr:X} enc={enc}; {nearest}; {ctx}");

            TryWrite(hit, pat, payload);
        }
    }

    private void TryWrite(Hit hit, byte[] pat, byte[] payload)
    {
        if (!_mem.TryReadBytes(hit.Addr, pat.Length, out var cur) || !ByteEq(cur, pat))
        {
            ModLogger.Event(LogVerb.Trace, $"header-spike: addr=0x{hit.Addr:X} enc={hit.Enc} no longer holds the label at write time; skipped (race guard)");
            _skipped++;
            return;
        }
        if (!_mem.Writable(hit.Addr, payload.Length))
        {
            ModLogger.Event(LogVerb.Trace, $"header-spike: addr=0x{hit.Addr:X} enc={hit.Enc} is not writable; skipped");
            _skipped++;
            return;
        }

        _mem.WriteBytes(hit.Addr, payload);
        hit.Written = true;
        hit.WrittenAtMs = Environment.TickCount64;
        _written++;
        ModLogger.Event(LogVerb.Trace, $"header-spike: wrote addr=0x{hit.Addr:X} enc={hit.Enc}");
    }

    private static string NearestSiteDescription(long addr, List<CardSites.Site> sites)
    {
        if (sites.Count == 0) return "no sites known (open the equip card first)";
        long best = long.MaxValue;
        int bestId = -1;
        foreach (var s in sites)
        {
            long d = Math.Abs(s.AnchorAddr - addr);
            if (d < best) { best = d; bestId = s.Id; }
        }
        return $"nearest known card site is weapon {bestId}, {best} bytes away";
    }

    /// <summary>Cheap steady-state check: at most once a second, re-read every address this
    /// instrument wrote and log ONLY on a state change. Holding is silent by design.</summary>
    private void Recheck()
    {
        if (_hits.Count == 0) return;
        long now = Environment.TickCount64;
        if (now - _lastRecheckMs < RecheckIntervalMs) return;
        _lastRecheckMs = now;

        foreach (var hit in _hits)
        {
            if (!hit.Written || hit.Reverted || hit.Unreadable) continue;

            byte[] pat = hit.Enc == 1 ? _pat1 : _pat2;
            if (!_mem.TryReadBytes(hit.Addr, pat.Length, out var cur))
            {
                hit.Unreadable = true;
                ModLogger.Event(LogVerb.Trace, $"header-spike: addr=0x{hit.Addr:X} enc={hit.Enc} became unreadable {now - hit.WrittenAtMs}ms after the write");
                continue;
            }
            if (ByteEq(cur, pat))
            {
                hit.Reverted = true;
                ModLogger.Event(LogVerb.Trace, $"header-spike: addr=0x{hit.Addr:X} enc={hit.Enc} reverted to the original label {now - hit.WrittenAtMs}ms after the write");
            }
            // else: still holds our payload, the expected silent steady state
        }
    }

    private static bool ByteEq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
#endif
