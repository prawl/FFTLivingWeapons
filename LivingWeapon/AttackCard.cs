using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-31 stage 3's production painter (docs/TODO.md): in battle, the Abilities menu's Attack
/// ROW ITSELF renames to the acting unit's living weapon (name + trimmed tier suffix, or "Fists"
/// for an unarmed human), and the hover-card desc that follows it in the same table copy carries
/// the tier-progress "Kills: N/T to +..." meter (AttackCardTail.cs, owner decision 2026-07-06).
/// Mirrors AttackCardSpike's proven locate machinery (a budgeted,
/// high-address-first ScanCursor walk over the "Attack" standalone-string tables) but is driven
/// automatically by battle state rather than a keypress. Split by responsibility, the
/// KillTracker.cs/Corpses.cs/Delayed.cs house shape: this file owns construction/lifecycle;
/// AttackCard.Census.cs is the locate half (finding table copies); AttackCard.Paint.cs is the
/// compose/write half (resolve, decide, and drive AttackRow's guarded record I/O).
///
/// THE RENAME MECHANISM (live-proven 2026-07-06, owner eyewitness with a screenshot on file; see
/// AttackRow.Policy.cs's class doc for the full record geometry): the row and its hover-card title
/// share ONE string, driven by a JobCommand text-catalog record at LabelAddr - 0x1FC1 whose
/// nameOff/descOff fields normally point at the "Attack" label itself and the vanilla desc right
/// after it. The rename never touches the label bytes (they stay the race-guard re-verify anchor
/// forever): it writes a split image "&lt;rowName&gt;\0&lt;tail&gt;" into the SAME desc footprint
/// stage 2 already wrote flat text into, then repoints nameOff/descOff into that footprint's two
/// halves. Stage 2's flat-desc write survives as the VANILLA plan's own shape (a "split" of one
/// name-less half): both plans are 74-byte images, so the three-way anchor below now compares
/// IMAGES byte-for-byte rather than NUL-terminated strings.
///
/// THREE-WAY ANCHOR (EarnedAnchors/CardSites/StoryLines discipline, generalized to a SINGLE
/// current/previous slot: the Attack menu is SHARED across every unit, not keyed per weapon
/// id): at any moment a cached table copy legitimately holds ONE OF the vanilla image, the
/// painter's CURRENT composed image, or its PREVIOUS composed image. Rotation (current -&gt;
/// previous) happens ONLY on a compose-change edge (AttackCard.Paint.cs's RepaintDriver), never
/// at paint time; a copy holding the previous image is still live and gets repainted FORWARD to
/// current, never evicted for merely lagging one compose-generation behind. A copy holding
/// anything else is foreign (a freed/reused buffer, or some OTHER command's row, e.g. a monster
/// catalog's "Slaps with a webbed hand", which must stay untouched forever) and is left alone:
/// evicted from the cache, re-censused later (unlike CardSites' rate-limited prune, this cache is
/// tiny, about six copies per the spike's census, so a full re-census on any eviction is cheap
/// enough not to need one). enc==2 (UTF16) catalogs never participate in the split-image mechanism
/// at all (AttackRow's record is enc1-only; live census found zero enc2 "Attack" catalogs, so this
/// is a dead path kept safe): they only ever get vanilla-restore, never a composed write.
///
/// TURN-OWNER SEAM (CURSOR-ONLY since 2026-07-06, ledger LW-31; RE-ANCHORED 2026-07-21, LW-87):
/// AttackCard.Resolve.cs's ComposeCurrentPlan consults ActorResolver.TryResolveCursorPlayer, and
/// NOTHING ELSE. Through 2026-07-14 that method read the condensed turn-queue struct
/// (Offsets.TurnQueue); LW-87 re-anchored it on the PSX turn-flags owner (Band.FlagOwner, the same
/// exclusive-ownership walk LW-63's kill-credit lane already trusted) plus the roster bridge,
/// because the struct FOLLOWS THE CURSOR rather than the turn: the owner watched a T-status
/// detour blank the whole row to vanilla mid-turn even though the acting unit's own menu was still
/// open (docs/LIVE_LEDGER.md's 2026-07-21 hover-follower row). See ActorResolver.Cursor.cs's own
/// class doc for the full resolve shape. The register fallback stage 2 kept (the KillerStamp
/// seam, ActorRegister.LastPlayer*) is GONE from this surface, unaffected by the re-anchor: the
/// owner watched it put ANOTHER unit's weapon (a Spark Rod) on Ramza's Attack row 2026-07-06 when
/// two party units shared the (level,hp,maxHp) fingerprint; the cursor correctly refused the
/// ambiguous match, and the fallback then served the LAST ACTED player's hands, which is the wrong
/// dossier on every turn the cursor cannot clear. Doctrine: a wrong dossier is worse than vanilla,
/// so no cursor answer now means "restore vanilla", full stop. (Recovering the shared-fingerprint
/// twins case is a backlogged fingerprint extension, LW-39, though LW-87's nameId bridge already
/// gives this surface partial relief; ActorRegister itself and the KillerStamp attribution seam
/// elsewhere are untouched.) Once a rosterBase is in hand, the row/tail decision
/// (AttackRow.Policy.ComposeRow) is driven strictly off the RAW main hand (Offsets.RRHand) and
/// sprite byte, never the filtered/tracked Hands() set, which cannot distinguish "unarmed" from
/// "wielding something untracked" and would miss the "Fists" case entirely. Dual wield: only the
/// main hand is ever shown here (a second blade still earns kills via KillTracker same as always,
/// just is not featured in this single row/title). LW-55 adds a narrowing-only gate on top of the
/// cursor answer itself (CursorGate.Decide, see CursorGate.cs and AttackCard.Resolve.cs); LW-87
/// adds a per-battle resolve-miss tap (<see cref="CursorMiss"/>) alongside it, naming WHICH stage
/// refused when there is no answer at all.
/// </summary>
internal sealed partial class AttackCard
{
    // Same bounded per-tick slice as AttackCardSpike (see its class doc for the full account of
    // why): large enough to finish a census in a handful of ticks, small enough that one Tick can
    // never plausibly hitch the shared 33ms engine loop.
    private const long PerTickBudgetBytes = 48L * 1024 * 1024;

    // The census proved about six copies live per launch; a generous multiple with headroom
    // (never the dev spike's 200; this runs unattended every battle, not on a one-shot keypress).
    private const int HitCap = 32;

    // Both vanilla and every composed line are budget-capped at AttackCardText.DefaultBudgetChars
    // (73); a NUL always lands within that many chars, so this is enough to read any of the three
    // known lines whole without a rescan.
    private const int DescCapChars = AttackCardText.DefaultBudgetChars;

    // Mirrors Display.MaintenanceMs: a steady-state cadence that re-verifies/repaints every cached
    // copy even when the composed line hasn't changed, so a stray drift or a newly-discovered copy
    // still converges without waiting for the next turn-owner change.
    private const long MaintenanceMs = 1000;

    private sealed class Hit
    {
        public long LabelAddr;   // the "Attack" label's own address (race-guard re-verify target)
        public long DescAddr;    // where the desc string begins; the only address ever written
        public int Enc;
        public int DescChars;    // the original desc's char count, found at census time (footprint check)
        // LW-91: 0 = healthy. Nonzero = the clock reading (AttackCard's injected nowMs) of this
        // Hit's strike episode start; RepaintAll owns every read/write of this field (SyncHit
        // never touches it -- see AttackCard.Paint.cs's RepaintAll for the retain/evict policy).
        public long FirstFailMs;
    }

    private readonly IGameMemory _mem;
    private readonly ChunkReader _reader;
    private readonly AttackRow _attackRow;
    private readonly Func<(CursorAnswer? Answer, CursorMiss Miss)> _resolveCursor;
    private readonly Func<long, byte> _spriteOf;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly Action<string, string>? _recorder;   // LW-55: flight tap for the tripwire, Flight.Record's idiom
    private readonly Func<long> _nowMs;

    private readonly List<Hit> _hits = new();
    private bool _needsCensus = true;
    private bool _scanning;
    private bool _sweepCompleted;   // honest "no census has ever completed"; Arm gates re-arm on this too
    private bool _nextIsRepaint;    // alternation toggle: which phase Tick runs next while a sweep is in flight
    private int _rejectedThisCensus;   // census-lifecycle state: reset in Arm, reported by Finish
    private List<(long rbase, long rsize)> _regionsDesc = new();
    private RegionCursor _cursor;

    // The vanilla plan's own 74-byte image: the flat vanilla desc, no split, exactly the census's
    // own footprint (AttackRow.FootprintBytes). Precomputed once (never depends on any resolve).
    private static readonly byte[] VanillaImage = AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, 1);

    private byte[]? _currentImage;   // null = vanilla is the desired state
    private int _currentRowChars;    // meaningful only when _currentImage != null
    private byte[]? _previousImage;  // the last DISTINCT composed image before the current rotation
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

    /// <summary>In-battle tick (the Abilities menu is in-battle; battle-paused ticks still run).
    /// Arms the census, then while a sweep is in flight ALTERNATES ticks between RepaintDriver and
    /// StepScan (never both in the same tick, the spike's own proven anti-starvation shape) so a
    /// sweep never starves the repaint driver: without this, the session's first battle shows the
    /// vanilla Attack row for the whole census, since nothing paints until Finish (LW-57). Arm
    /// pins the tick immediately after it to the REPAINT phase (deterministic: a test fixture's
    /// sweep finishes in one StepScan, so the phase order is what makes the behavior observable).
    /// An empty cache has nothing to repaint yet, so it scans every tick, same as before.</summary>
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

    /// <summary>Battle-enter AND battle-exit edge (Engine.ResetBattleState fires on both):
    /// restore vanilla to every live cached copy best-effort, then reset compose state for the
    /// next battle. LW-38: unlike KillTracker/Display's own reset shape, the copy cache itself is
    /// KEPT warm across the edge rather than dropped, so the next battle's first repaint does not
    /// have to wait out a fresh multi-tick committed-heap census before the Attack row can be
    /// renamed again. This is safe for free: RepaintAll's very next pass runs SyncHit over every
    /// cached copy, which fully re-verifies the "Attack" label bytes and the footprint image from
    /// scratch, evicting (and re-arming a census for) anything that went stale in the meantime.
    /// Re-arms a full census here on any of three conditions: an empty cache (nothing left to
    /// re-validate), a sweep this battle that was ABORTED before it could Finish (a battle edge mid
    /// sweep would otherwise leave a partial cache silently masquerading as complete forever,
    /// LW-57), or a re-census already pending from an eviction the battle edge would otherwise
    /// swallow (the leading `_needsCensus ||` term).</summary>
    public void ResetBattle()
    {
        RestoreVanillaBestEffort();
        _scanning = false;
        _needsCensus = _needsCensus || _hits.Count == 0 || !_sweepCompleted;
        _currentImage = null;
        _currentRowChars = 0;
        _previousImage = null;
        _lastMaintenanceMs = -1;
        foreach (var hit in _hits) hit.FirstFailMs = 0;   // LW-91: fresh battle, fresh strike grace
        _reportedRefusals.Clear();   // LW-55: the per-battle tripwire dedup set (AttackCard.Resolve.cs)
        _reportedMisses.Clear();     // LW-87: the per-battle resolve-miss dedup set (AttackCard.Resolve.cs)
    }

    /// <summary>Test accessor (mirrors CardSites.Count/Display._sites): the number of table
    /// copies currently cached as known-live.</summary>
    internal int HitCountForTests => _hits.Count;

    /// <summary>Test accessor: how many cached hits are currently mid strike-episode (LW-91: the
    /// last SyncHit failed and the copy is retained pending recovery, not yet evicted).</summary>
    internal int StrikingCountForTests
    {
        get
        {
            int n = 0;
            foreach (var h in _hits) if (h.FirstFailMs != 0) n++;
            return n;
        }
    }

    /// <summary>Test accessor: mirrors <c>_needsCensus</c> directly, so a test can check whether a
    /// census is armed/pending arm without having to observe a full Arm/StepScan cycle.</summary>
    internal bool PendingCensusForTests => _needsCensus;

    /// <summary>Test-only hook: force a fresh census on the next Tick, the same effect any live
    /// eviction (RepaintAll) triggers on its own. Lets a test reach the mid-battle re-census case
    /// (a copy still holding a KNOWN line when the census re-inspects it) without fabricating a
    /// foreign-buffer write to provoke a real eviction.</summary>
    internal void ForceRecensusForTests() => _needsCensus = true;

    /// <summary>Test-only hook (mirrors ForceRecensusForTests): directly poisons the first cached
    /// hit's footprint to a too-small value, the same effect a corrupted/stale cache entry would
    /// have, without needing to fabricate a live scenario that produces one organically. Exercises
    /// SyncHit's own repair (LW-33): a full live read confirming a known line re-pins the
    /// footprint back to the vanilla desc's own 73 chars.</summary>
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
