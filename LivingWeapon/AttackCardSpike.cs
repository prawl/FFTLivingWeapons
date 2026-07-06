#if LWDEV
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// DEV-ONLY spike (LW-31, docs/TODO.md Now section; HeaderSpike blueprint, same #if LWDEV
/// whole-file wrap): the Attack-menu census instrument. In battle, the Abilities submenu's
/// "Attack" row and its hover card render from a packed heap table: the standalone C-string
/// "Attack" immediately followed (next byte after its NUL) by the attack description string.
/// Owner-proven live 2026-07-05: writing past "Attack"'s 6 chars eats the NUL and bleeds into
/// the desc (rendered "Save th4"); the row text and hover-card title share ONE string;
/// multiple copies of the table exist. This spike censuses those tables from a clean launch
/// (canonical desc text, copy count, encodings, revert behavior) and live-tests a
/// footprint-safe write INTO THE DESC ONLY. The row string is READ-ONLY here: its 6-char
/// prison is a later stage's problem (staged plan in docs/TODO.md's LW-31 entry).
///
/// Key: F6 (one-shot arm; press again anytime to clear the hit list and re-scan). F6 is
/// deliberately SHARED with ShowSpike's commit-tap, FlavorSpike's P4 arm, and HeaderSpike's
/// header-repaint arm (the established shared-key convention; this is the FOURTH tenant on the
/// same key). The Abilities submenu this spike targets is an IN-BATTLE menu, so in practice
/// only HeaderSpike (which also ticks in battle) co-fires reliably here; ShowSpike/FlavorSpike
/// target out-of-battle menus and are unlikely to find anything when F6 is pressed on this
/// spike's actual target screen. F3/F7/F9 are eaten LL-hooks on the owner's machine, and F8
/// stopped registering there 2026-07-03, which leaves F6 as the one proven-delivered key.
///
/// ROOT CAUSE of the first live flight's total silence (2026-07-05): this spike's OneShot used
/// to be a single synchronous pass over up to 512MB of committed regions (roughly 129 4MB
/// chunks, each scanned twice for ASCII+UTF16), all inside ONE Engine.Tick call. HeaderSpike
/// ticks immediately before this spike at every call site in Engine.cs, and on the press that
/// hit HeaderSpike's scan cap, HeaderSpike's OneShot alone ran long enough (its own log showed
/// "scanned 539961984 bytes... stopped early") to keep the shared tick thread busy well past the
/// human keypress's hold duration. By the time control finally reached THIS spike's
/// Pressed(VkF6, ref _f6Was) call in that same Tick, GetAsyncKeyState(VkF6) legitimately reported
/// the key already released: down=false, so pressed=false, so OneShot() was never invoked and
/// Recheck() no-oped on an empty hit list (nothing written yet to recheck). No exception was
/// thrown (the try/catch around OneShot was never entered), no early return was skipped (both
/// Engine.cs call sites call this spike's Tick unconditionally, symmetric with HeaderSpike's),
/// and the edge-detect logic itself was never the bug: each spike already samples the key by
/// LEVEL (the high bit, which is not consumed by another reader) against its OWN independent
/// _was flag, so multiple listeners on one key do not interfere with each other's edge detection
/// UNDER NORMAL TIMING. The actual defect was architectural: a slow sibling ticked earlier in
/// the same frame can burn through the ENTIRE physical press duration before a later sibling's
/// independent key check gets its turn. The fix (shared with HeaderSpike, see its class doc) is
/// to shrink each spike's per-tick work to a small budgeted slice (ScanCursor,
/// PerTickBudgetBytes) so no single Tick call can plausibly outlast a human keypress again; the
/// arming tick itself (on the F6 edge) does no scanning at all, only cheap state setup, so a
/// press is registered and logged before any scanning work can compete for the thread.
///
/// On press: snapshot committed heap regions (the same ChunkReader source Display's sweep
/// walks), sort them descending by base address (ScanCursor.SortDescending: this game's UI text
/// arenas live at the top of the committed space) and store a cursor. Each subsequent Tick
/// advances the walk by a budgeted slice (ScanCursor.NextSlice), scanning for the literal
/// "Attack" in both ASCII and UTF16LE, STANDALONE-C-string matched via
/// AttackCardProbeText.IsStandaloneHit (stricter than a simple word-boundary check:
/// "Attacks"/"attack power" style prose hits are excluded, since only the standalone
/// row/hover-card string is the target). There is no total-bytes cap anymore: the walk continues
/// until the snapshot is fully covered or the 200-hit cap trips (logged when it does). For each
/// accepted hit, logs the absolute address, encoding, the desc text and its char length
/// (AttackCardProbeText.ReadDesc), and a 48-byte-radius context dump
/// (AttackCardProbeText.FormatContext); this is the census: it runs and logs for every hit
/// whether or not a write follows, and it happens as each chunk is scanned, not batched to the
/// end.
///
/// Then, for each hit: a guarded write into the DESC ONLY (never the label, never before it),
/// AttackCardProbeText.Payload plus its own NUL terminator, gated on TWO checks: the
/// footprint check (FitsFootprint: the write can never exceed the original desc's own byte
/// footprint) and a race guard (the "Attack" label bytes are re-verified immediately before the
/// write, so this never touches a buffer that no longer holds the label: a freed/reused UI
/// buffer). Writable-gated on top of both. Each hit logs exactly one of: wrote, skipped-small
/// (footprint check failed), or skipped (race guard or not writable).
///
/// After the press, every written desc address is cheaply re-read (at most once a second, no
/// rescanning) and a line is logged ONLY on a state CHANGE: holding is the expected silent
/// steady state; a line fires when a written desc no longer holds our payload (reverted, with
/// elapsed ms since the write) or becomes unreadable. A second F6 press re-arms a fresh scan
/// (aborting any in-progress walk), dropping the previous hit list first.
/// </summary>
internal sealed class AttackCardSpike
{
    private const int VkF6 = 0x75;
    private const int HitCap = 200;
    private const long RecheckIntervalMs = 1000;
    private const long ProgressIntervalMs = 1000;
    private const int DescCapChars = 128;

    // 48MB per tick, same choice and same reasoning as HeaderSpike.PerTickBudgetBytes (see that
    // constant's doc): small enough that one tick's blocking window cannot plausibly outlast a
    // human keypress, which is what starved this spike's own edge check on the first live flight.
    private const long PerTickBudgetBytes = 48L * 1024 * 1024;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private sealed class Hit
    {
        public long Addr;       // the "Attack" label's own address (race-guard re-verify target)
        public long DescAddr;   // where the desc string begins; the ONLY address ever written
        public int Enc;
        public int DescChars;   // desc length found at scan time (drives the footprint check)
        public bool Written;
        public long WrittenAtMs;
        public bool Reverted;
        public bool Unreadable;
    }

    private readonly IGameMemory _mem;
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

    public AttackCardSpike(IGameMemory mem)
    {
        _mem = mem;
        _reader = new ChunkReader(mem);
        _pat1 = AttackCardProbeText.Pattern(1);
        _pat2 = AttackCardProbeText.Pattern(2);
        _pay1 = AttackCardProbeText.PayloadWithTerminator(1);
        _pay2 = AttackCardProbeText.PayloadWithTerminator(2);
        ModLogger.Event(LogVerb.Trace, "attack-card-spike: armed (F6 scans the heap for every standalone \"Attack\" table and, when the desc has room, overwrites the desc only, walking high addresses first, a budgeted slice per tick; press again anytime to re-scan; F6 also fires the sibling spikes by design)");
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
    /// no scanning itself, on purpose (see the Tick doc and the class doc's root-cause account).</summary>
    private void Arm()
    {
        _hits.Clear();
        _reader.Snapshot();
        _regionsDesc = ScanCursor.SortDescending(_reader.Regions);
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
        ModLogger.Event(LogVerb.Trace, $"attack-card-spike: scan started, {_regionsDesc.Count} regions ({_totalBytes} bytes) highest address first, {PerTickBudgetBytes / (1024 * 1024)}MB per tick");
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
                ScanAndWrite(_reader.Buf, lookback, windowEnd, bufBase, _pat1, 1, ref _hitsAscii);
                ScanAndWrite(_reader.Buf, lookback, windowEnd, bufBase, _pat2, 2, ref _hitsUtf16);
                _scanned += read;
            }

            if (_hits.Count >= HitCap) _capped = true;
            if (!_capped && !_cursor.Done) { MaybeLogProgress(); return; }

            Finish();
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, "attack-card-spike: scan failed; " + ex.Message);
            _scanning = false;
        }
    }

    private void MaybeLogProgress()
    {
        long now = Environment.TickCount64;
        if (now - _lastProgressMs < ProgressIntervalMs) return;
        _lastProgressMs = now;
        ModLogger.Event(LogVerb.Trace, $"attack-card-spike: scanned {_scanned / (1024 * 1024)} of {_totalBytes / (1024 * 1024)} MB, {_hits.Count} hits so far");
    }

    private void Finish()
    {
        _sw.Stop();
        _scanning = false;

        if (_capped)
            ModLogger.Event(LogVerb.Trace, $"attack-card-spike: hit cap reached ({_hits.Count} hits); stopped early, results below are partial");
        ModLogger.Event(LogVerb.Trace, $"attack-card-spike: scan finished in {_sw.ElapsedMilliseconds}ms; {_scanned} bytes scanned, {_hitsAscii} ascii + {_hitsUtf16} utf16 standalone hits");
        ModLogger.Event(LogVerb.Trace, $"attack-card-spike: {_written} written, {_skipped} skipped of {_hits.Count} hits; holding is silent now, watch this log for a revert or unreadable line");
    }

    /// <summary>Find standalone "Attack" hits in one chunk's search window, log each with its
    /// desc text/length and context dump, record it for Recheck, and immediately attempt its
    /// guarded write into the desc.</summary>
    private void ScanAndWrite(byte[] buf, int lookback, int windowEnd, long bufBase, byte[] pat, int enc, ref int hitCounter)
    {
        var positions = new List<int>();
        ByteScan.FindAll(buf, pat, lookback, windowEnd, positions);

        foreach (int pos in positions)
        {
            if (_hits.Count >= HitCap) return;
            if (!AttackCardProbeText.IsStandaloneHit(buf, pos, enc)) continue;

            long addr = bufBase + pos;
            int descPos = AttackCardProbeText.DescStart(pos, enc);
            var (descText, descChars) = AttackCardProbeText.ReadDesc(buf, descPos, enc, DescCapChars);
            long descAddr = bufBase + descPos;

            var hit = new Hit { Addr = addr, DescAddr = descAddr, Enc = enc, DescChars = descChars };
            _hits.Add(hit);
            hitCounter++;

            string ctx = AttackCardProbeText.FormatContext(buf, pos, enc);
            ModLogger.Event(LogVerb.Trace, $"attack-card-spike: hit addr=0x{addr:X} enc={enc} desc=\"{descText}\" ({descChars} chars); {ctx}");

            TryWrite(hit, pat);
        }
    }

    private void TryWrite(Hit hit, byte[] pat)
    {
        if (!AttackCardProbeText.FitsFootprint(hit.DescChars))
        {
            ModLogger.Event(LogVerb.Trace, $"attack-card-spike: addr=0x{hit.Addr:X} enc={hit.Enc} desc is only {hit.DescChars} chars, too small for the payload; skipped-small");
            _skipped++;
            return;
        }

        if (!_mem.TryReadBytes(hit.Addr, pat.Length, out var cur) || !ByteEq(cur, pat))
        {
            ModLogger.Event(LogVerb.Trace, $"attack-card-spike: addr=0x{hit.Addr:X} enc={hit.Enc} no longer holds the \"Attack\" label at write time; skipped (race guard)");
            _skipped++;
            return;
        }

        byte[] payload = hit.Enc == 1 ? _pay1 : _pay2;
        if (!_mem.Writable(hit.DescAddr, payload.Length))
        {
            ModLogger.Event(LogVerb.Trace, $"attack-card-spike: addr=0x{hit.DescAddr:X} enc={hit.Enc} desc is not writable; skipped");
            _skipped++;
            return;
        }

        _mem.WriteBytes(hit.DescAddr, payload);
        hit.Written = true;
        hit.WrittenAtMs = Environment.TickCount64;
        _written++;
        ModLogger.Event(LogVerb.Trace, $"attack-card-spike: wrote desc addr=0x{hit.DescAddr:X} enc={hit.Enc} (label addr=0x{hit.Addr:X})");
    }

    /// <summary>Cheap steady-state check: at most once a second, re-read every desc address this
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

            byte[] payload = hit.Enc == 1 ? _pay1 : _pay2;
            if (!_mem.TryReadBytes(hit.DescAddr, payload.Length, out var cur))
            {
                hit.Unreadable = true;
                ModLogger.Event(LogVerb.Trace, $"attack-card-spike: addr=0x{hit.DescAddr:X} enc={hit.Enc} became unreadable {now - hit.WrittenAtMs}ms after the write");
                continue;
            }
            if (!ByteEq(cur, payload))
            {
                hit.Reverted = true;
                ModLogger.Event(LogVerb.Trace, $"attack-card-spike: addr=0x{hit.DescAddr:X} enc={hit.Enc} reverted away from the written payload {now - hit.WrittenAtMs}ms after the write");
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
