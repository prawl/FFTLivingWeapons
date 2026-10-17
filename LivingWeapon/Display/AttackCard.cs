using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The Attack-menu painter. In battle the Abilities menu's Attack row renames to the
/// acting unit's living weapon ("Fists" if unarmed), and the hover-card desc beside it carries the
/// tier-progress meter (AttackCardTail.cs, 2026-07-06). Locate machinery follows AttackCardSpike
/// (budgeted ScanCursor walk, high addresses first). This file holds construction and lifecycle,
/// AttackCard.Census.cs finds table copies, AttackCard.Paint.cs composes and writes via AttackRow.
///
/// Rename mechanism (2026-07-06, see AttackRow.Policy.cs for record geometry). The row and its
/// hover-card title share one string, driven by a JobCommand text-catalog record at
/// LabelAddr, 0x1FC1. Its nameOff/descOff normally point at the "Attack" label and the vanilla desc
/// after it. The painter leaves the label bytes alone (they anchor the race-guard re-verify), writes
/// a split image "&lt;rowName&gt;\0&lt;tail&gt;" into the desc footprint, and repoints nameOff/descOff
/// into its two halves. The vanilla plan is the same 74-byte shape, so anchors compare whole images.
///
/// Three-way anchor (EarnedAnchors/CardSites/StoryLines style, one current/previous slot because the
/// Attack menu is shared by every unit). A cached copy may hold the vanilla image, the current image
/// or the previous image. Current rotates to previous only on a compose-change edge (RepaintDriver in
/// AttackCard.Paint.cs). A copy holding the previous image is repainted forward, not evicted. Any
/// other content is foreign (a reused buffer, or some other command's row such as a monster
/// catalog's "Slaps with a webbed hand", which must stay untouched forever), so it is evicted and
/// re-censused later. The cache is tiny (about six copies), so a full re-census is cheap. enc==2
/// (UTF16) catalogs never take a composed write, only vanilla restore (none seen in the census).
///
/// Turn owner is cursor-only (2026-07-06; re-anchored 2026-07-21).
/// AttackCard.Resolve.cs's ComposeCurrentPlan uses ActorResolver.TryResolveCursorPlayer alone, which
/// the re-anchor moved from the condensed turn-queue struct (Offsets.TurnQueue, used through 2026-07-14) to
/// the PSX turn-flags owner (Band.FlagOwner, the kill-credit lane) plus the roster bridge.
/// The struct follows the cursor, not the turn, and a T-status detour blanked the row mid-turn
/// (docs/LIVE_LEDGER.md, 2026-07-21 hover-follower row). See ActorResolver.Cursor.cs. The old
/// ActorRegister.LastPlayer* fallback is gone. On 2026-07-06 it put another unit's weapon (a Spark
/// Rod) on Ramza's row when two units shared a (level,hp,maxHp) fingerprint, so no cursor answer
/// now restores vanilla. Shared-fingerprint twins are backlog (the nameId bridge helps
/// partly). The row/tail decision (AttackRow.Policy.ComposeRow) reads the raw main hand
/// (Offsets.RRHand) and sprite byte, since the filtered Hands set cannot tell unarmed from an
/// untracked weapon. Only the main hand is shown. A narrowing gate sits on the cursor answer
/// (CursorGate.Decide, CursorGate.cs) and a per-battle resolve-miss tap
/// (<see cref="CursorMiss"/>) naming which stage refused.
/// </summary>
internal sealed partial class AttackCard
{
    // Same per-tick slice as AttackCardSpike. Finishes a census in a few ticks without hitching the 33ms engine loop.
    private const long PerTickBudgetBytes = 48L * 1024 * 1024;

    // The census found about six copies per launch. The dev spike's cap was 200.
    private const int HitCap = 32;

    // Vanilla and composed lines are capped at AttackCardText.DefaultBudgetChars (73), so a NUL lands within this many chars.
    private const int DescCapChars = AttackCardText.DefaultBudgetChars;

    // Like Display.MaintenanceMs. Re-verifies and repaints every cached copy so drift converges between turn-owner changes.
    private const long MaintenanceMs = 1000;

    private sealed class Hit
    {
        public long LabelAddr;   // the "Attack" label's own address (race-guard re-verify target)
        public long DescAddr;    // where the desc string begins; the only address ever written
        public int Enc;
        public int DescChars;    // the original desc's char count, found at census time (footprint check)
        // 0 = healthy, else the nowMs reading when this hit's strike episode began. Only RepaintAll touches it (AttackCard.Paint.cs).
        public long FirstFailMs;
    }

    private readonly IGameMemory _mem;
    private readonly ChunkReader _reader;
    private readonly AttackRow _attackRow;
    private readonly Func<(CursorAnswer? Answer, CursorMiss Miss)> _resolveCursor;
    private readonly Func<long, byte> _spriteOf;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly Action<string, string>? _recorder;   // flight tap for the tripwire, Flight.Record's idiom
    private readonly Func<long> _nowMs;

    private readonly List<Hit> _hits = new();
    private bool _needsCensus = true;
    private bool _scanning;
    private bool _sweepCompleted;   // honest "no census has ever completed"; Arm gates re-arm on this too
    private bool _nextIsRepaint;    // alternation toggle. Which phase Tick runs next while a sweep is in flight
    private int _rejectedThisCensus;   // census-lifecycle state. Reset in Arm, reported by Finish
    private List<(long rbase, long rsize)> _regionsDesc = new();
    private RegionCursor _cursor;

    // The vanilla plan's 74-byte image, the same size as the census footprint (AttackRow.FootprintBytes).
    private static readonly byte[] VanillaImage = AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, 1);

    private byte[]? _currentImage;   // null = vanilla is the desired state
    private int _currentRowChars;    // meaningful only when _currentImage != null
    private byte[]? _previousImage;  // the last distinct composed image before the current rotation
    private long _lastMaintenanceMs = -1;

    public AttackCard(IGameMemory mem, Func<(CursorAnswer? Answer, CursorMiss Miss)> resolveCursor,
                       Func<long, byte> spriteOf,
                       Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
                       Action<string, string>? recorder = null, Func<long>? nowMs = null)
    {
        _mem = mem;
        _reader = new ChunkReader(mem);
        _attackRow = new AttackRow(mem);
        _resolveCursor = resolveCursor;
        _spriteOf = spriteOf;
        _meta = meta;
        _kills = kills;
        _recorder = recorder;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>In-battle tick. Arms the census, then while a sweep runs alternates RepaintDriver and
    /// StepScan so the first battle does not show vanilla for the whole census. The tick after
    /// Arm is pinned to repaint so tests can observe the order. An empty cache scans every tick.</summary>
    public void Tick()
    {
        if (_needsCensus && !_scanning) { Arm(); _needsCensus = false; return; }
        if (_scanning)
        {
            if (_hits.Count == 0) { StepScan(); return; }
            if (_nextIsRepaint) { RepaintDriver(); _nextIsRepaint = false; }
            else { StepScan(); _nextIsRepaint = true; }
            return;
        }
        RepaintDriver();
    }

    /// <summary>Battle-enter and battle-exit edge. Restores vanilla to cached copies best-effort and
    /// resets compose state. The copy cache stays warm so the next battle needs no fresh census;
    /// the next RepaintAll SyncHit pass re-verifies each copy and evicts stale ones. A census is
    /// re-armed when the cache is empty, when a sweep was aborted before Finish, or when one
    /// was already pending.</summary>
    public void ResetBattle()
    {
        RestoreVanillaBestEffort();
        _scanning = false;
        _needsCensus = _needsCensus || _hits.Count == 0 || !_sweepCompleted;
        _currentImage = null;
        _currentRowChars = 0;
        _previousImage = null;
        _lastMaintenanceMs = -1;
        foreach (var hit in _hits) hit.FirstFailMs = 0;   // fresh battle, fresh strike grace
        _reportedRefusals.Clear();   // the per-battle tripwire dedup set (AttackCard.Resolve.cs)
        _reportedMisses.Clear();     // the per-battle resolve-miss dedup set (AttackCard.Resolve.cs)
    }

    /// <summary>Test accessor. Number of cached table copies.</summary>
    internal int HitCountForTests => _hits.Count;

    /// <summary>Test accessor. Cached hits mid strike-episode (last SyncHit failed, not yet evicted).</summary>
    internal int StrikingCountForTests
    {
        get
        {
            int n = 0;
            foreach (var h in _hits) if (h.FirstFailMs != 0) n++;
            return n;
        }
    }

    /// <summary>Test accessor for <c>_needsCensus</c>.</summary>
    internal bool PendingCensusForTests => _needsCensus;

    /// <summary>Test-only. Forces a census on the next Tick, as an eviction in RepaintAll would.</summary>
    internal void ForceRecensusForTests() => _needsCensus = true;

    /// <summary>Test-only. Shrinks the first hit's footprint to exercise SyncHit's repair,
    /// which re-pins it to the vanilla desc's 73 chars.</summary>
    internal void PoisonFirstHitFootprintForTests(int chars)
    {
        if (_hits.Count > 0) _hits[0].DescChars = chars;
    }

    private static bool ByteEq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
