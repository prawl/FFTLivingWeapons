using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-31 stage 2's production painter (docs/TODO.md): in battle, the Abilities menu's Attack
/// hover-card Description becomes the acting unit's weapon dossier (AttackCardText.Compose), or
/// is restored to the vanilla desc when the acting unit is unarmed/unstoried. Mirrors
/// AttackCardSpike's proven locate machinery (a budgeted, high-address-first ScanCursor walk over
/// the "Attack" standalone-string tables) but is driven automatically by battle state rather than
/// a keypress, and writes the composed dossier line instead of a fixed dev probe payload. Split
/// by responsibility, the KillTracker.cs/Corpses.cs/Delayed.cs house shape: this file owns
/// construction/lifecycle; AttackCard.Census.cs is the locate half (finding table copies);
/// AttackCard.Paint.cs is the compose/write half.
///
/// THREE-WAY ANCHOR (EarnedAnchors/CardSites/StoryLines discipline, generalized to a SINGLE
/// current/previous slot: the Attack menu is SHARED across every unit, not keyed per weapon
/// id): at any moment a cached table copy legitimately holds ONE OF the vanilla desc, the
/// painter's CURRENT composed line, or its PREVIOUS composed line. Rotation (current -&gt;
/// previous) happens ONLY on a compose-change edge (AttackCard.Paint.cs's RepaintDriver), never
/// at paint time; a copy holding the previous line is still live and gets repainted FORWARD to
/// current, never evicted for merely lagging one compose-generation behind. A copy holding
/// anything else is foreign (a freed/reused buffer) and is left alone: evicted from the cache,
/// re-censused later (unlike CardSites' rate-limited prune, this cache is tiny, about six
/// copies per the spike's census, so a full re-census on any eviction is cheap enough not to
/// need one).
///
/// TURN-OWNER SEAM: resolves the acting unit's weapons via the SAME seam KillerStamp trusts
/// (ActorRegister.LastPlayerRosterBase/LastPlayerArrivalTick/Trusted, then
/// ActorResolver.HandsFromRoster): never ActorRegister.CurrentBridge/CurrentRosterBase, which
/// can currently be parked on a struck victim rather than the unit about to act. An untrusted or
/// empty register, or a resolved player holding no tracked weapon, both mean "restore vanilla":
/// miss beats mis-showing another unit's dossier. Only the FIRST tracked hand (RRHand-priority,
/// per ActorResolver.Hands) is dossier'd: a dual-wielder's second blade is credited kills same
/// as always, just not featured in this single-line hover text.
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
    private readonly ActorRegister _register;
    private readonly Func<long, List<int>> _handsFromRoster;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly LegendStore _legends;
    private readonly Func<long> _nowMs;

    private readonly List<Hit> _hits = new();
    private bool _needsCensus = true;
    private bool _scanning;
    private List<(long rbase, long rsize)> _regionsDesc = new();
    private RegionCursor _cursor;

    private string? _current;    // null = vanilla is the desired state
    private string? _previous;   // the last DISTINCT composed line before the current rotation
    private long _lastMaintenanceMs = -1;

    public AttackCard(IGameMemory mem, ActorRegister register, Func<long, List<int>> handsFromRoster,
                       Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, LegendStore legends,
                       Func<long>? nowMs = null)
    {
        _mem = mem;
        _reader = new ChunkReader(mem);
        _register = register;
        _handsFromRoster = handsFromRoster;
        _meta = meta;
        _kills = kills;
        _legends = legends;
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
    /// restore vanilla to every live cached copy best-effort, then drop the cache and re-arm a
    /// fresh census for the next battle. Idempotent, mirrors KillTracker/Display's own reset shape.</summary>
    public void ResetBattle()
    {
        RestoreVanillaBestEffort();
        _hits.Clear();
        _scanning = false;
        _needsCensus = true;
        _current = null;
        _previous = null;
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

    private static bool ByteEq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
