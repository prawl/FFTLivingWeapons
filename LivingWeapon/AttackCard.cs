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
/// TURN-OWNER SEAM (CURSOR-ONLY since 2026-07-06; ledger LW-31): AttackCard.Resolve.cs's
/// ComposeCurrentPlan consults ActorResolver.TryResolveCursorPlayer (the condensed turn-queue
/// struct, Offsets.TurnQueue), proven by the 2026-07-05 TurnOwnerSpike tape to snap to the acting
/// unit at TURN OPEN, under strict guards (player team, unambiguous band+nameId bridge; see
/// ActorResolver.Cursor.cs), and NOTHING ELSE. The register fallback stage 2 kept (the KillerStamp
/// seam, ActorRegister.LastPlayer*) is GONE from this surface: the owner watched it put ANOTHER
/// unit's weapon (a Spark Rod) on Ramza's Attack row 2026-07-06 when two party units shared the
/// (level,hp,maxHp) fingerprint; the cursor correctly refused the ambiguous match, and the
/// fallback then served the LAST ACTED player's hands, which is the wrong dossier on every turn
/// the cursor cannot clear. Doctrine: a wrong dossier is worse than vanilla, so no cursor answer
/// now means "restore vanilla", full stop. (Recovering the shared-fingerprint twins case is a
/// backlogged fingerprint extension; ActorRegister itself and the KillerStamp attribution seam
/// elsewhere are untouched.) Once a rosterBase is in hand, the row/tail decision
/// (AttackRow.Policy.ComposeRow) is driven strictly off the RAW main hand (Offsets.RRHand) and
/// sprite byte, never the filtered/tracked Hands() set, which cannot distinguish "unarmed" from
/// "wielding something untracked" and would miss the "Fists" case entirely. Dual wield: only the
/// main hand is ever shown here (a second blade still earns kills via KillTracker same as always,
/// just is not featured in this single row/title).
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
    }

    private readonly IGameMemory _mem;
    private readonly ChunkReader _reader;
    private readonly AttackRow _attackRow;
    private readonly Func<(List<int> Weapons, long RosterBase)?> _resolveCursor;
    private readonly Func<long, int> _rawMainHand;
    private readonly Func<long, byte> _spriteOf;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly Func<long> _nowMs;

    private readonly List<Hit> _hits = new();
    private bool _needsCensus = true;
    private bool _scanning;
    private List<(long rbase, long rsize)> _regionsDesc = new();
    private RegionCursor _cursor;

    // The vanilla plan's own 74-byte image: the flat vanilla desc, no split, exactly the census's
    // own footprint (AttackRow.FootprintBytes). Precomputed once (never depends on any resolve).
    private static readonly byte[] VanillaImage = AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, 1);

    private byte[]? _currentImage;   // null = vanilla is the desired state
    private int _currentRowChars;    // meaningful only when _currentImage != null
    private byte[]? _previousImage;  // the last DISTINCT composed image before the current rotation
    private long _lastMaintenanceMs = -1;

    public AttackCard(IGameMemory mem, Func<(List<int> Weapons, long RosterBase)?> resolveCursor,
                       Func<long, int> rawMainHand, Func<long, byte> spriteOf,
                       Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
                       Func<long>? nowMs = null)
    {
        _mem = mem;
        _reader = new ChunkReader(mem);
        _attackRow = new AttackRow(mem);
        _resolveCursor = resolveCursor;
        _rawMainHand = rawMainHand;
        _spriteOf = spriteOf;
        _meta = meta;
        _kills = kills;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>In-battle tick (the Abilities menu is in-battle; battle-paused ticks still run).
    /// Arms/advances the census one budgeted slice at a time (never both in the same tick, the
    /// spike's own proven anti-starvation shape), then otherwise drives the repaint.</summary>
    public void Tick()
    {
        if (_needsCensus && !_scanning) { Arm(); _needsCensus = false; return; }
        if (_scanning) { StepScan(); return; }
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
    /// Only an empty cache re-arms a full census here, since there is nothing left to re-validate.</summary>
    public void ResetBattle()
    {
        RestoreVanillaBestEffort();
        _scanning = false;
        _needsCensus = _hits.Count == 0;
        _currentImage = null;
        _currentRowChars = 0;
        _previousImage = null;
        _lastMaintenanceMs = -1;
    }

    /// <summary>Test accessor (mirrors CardSites.Count/Display._sites): the number of table
    /// copies currently cached as known-live.</summary>
    internal int HitCountForTests => _hits.Count;

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
