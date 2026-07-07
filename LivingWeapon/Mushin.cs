using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Kiku-ichimonji's "Mushin" signature: a full WAIT turn (no move, no action) banks one stack
/// of a power boost, up to Tuning.MushinMaxStacks (3); the wielder's next attack spends every
/// banked stack in one boosted hit, then the buff clears (MushinPolicy.NextStacks / ShouldConsume,
/// see their doc comments).
///
/// HYBRID (2026-07-07): the ARM WINDOW (turn-start snapshot, move sampling, the falling-edge arm
/// decision, w.Active) rides Offsets.ActorPtr's full fingerprint via Band.ActorEntry, while ACT +
/// CONSUME still rides the TurnQueue fingerprint (Band.ActiveOwner). Live testing the prior
/// TurnQueue-only window (same day) showed the arm lagging: Band.ActiveOwner follows the cursor
/// and BAILS (returns false) whenever two deployed units share maxHp+hp+level, so a wielder seated
/// beside a same-level ally could sit through its whole own turn with Band.ActiveOwner never
/// naming it, arming stacks ~2 turns late. ActorPtr always names whichever entry the engine is
/// CURRENTLY acting on, which is exactly what a turn's SPAN needs, and its identity check here
/// uses the full fingerprint (lvl,br,fa) read off the ActorPtr entry, not nameId, so it stays
/// collision-safe and never confuses a frozen (0,0) mirror twin for the real entry (an address
/// match can only ever name one specific frame). ActorPtr can still DWELL on a struck victim for a
/// beat past the real turn end (the same residual the pre-2026-07-07 ActorPtr design carried), so
/// act/consume identity deliberately stays on Band.ActiveOwner: correct for the INSTANT of an
/// action even though it bails too often to track the window around it.
///
/// Per tick, for each deployed main-hand wielder (Wielder.ResolveDeployedMainHandAll), this
/// module asks two questions: does the ActorPtr entry's fingerprint equal this wielder's
/// fingerprint right now ("actorNamesWielder", drives the window), and does Band.ActiveOwner's
/// fingerprint equal it ("tqNamesWielder", drives act/consume)? Between an actorNamesWielder
/// rising and falling edge it samples:
///   - MOVE: the wielder's grid position, read off the ActorPtr entry, diffed from its turn-start
///     snapshot.
///   - ACT: the global Acted byte (Offsets.Acted) rising 0 to 1 while tqNamesWielder holds,
///     independent of whether the ActorPtr window is currently open. An Acted edge observed while
///     Band.ActiveOwner names a DIFFERENT unit (an enemy's turn) never satisfies this, so it
///     neither arms nor consumes (own-turn scoping).
/// At the falling edge (actorNamesWielder goes false while the window was open), MushinPolicy.
/// NextStacks decides whether the just-closed turn banks a stack (neither MOVE nor ACT happened).
/// An ACT edge observed during the window also offers an immediate consume (MushinPolicy.
/// ShouldConsume) when at least one stack is already banked: an attack spends every stack the
/// instant it lands, not at the end of that attacking turn. The consume runs BEFORE the window-END
/// read of w.Acted, so an attack that also closes the window on the same tick still suppresses
/// that turn's bank.
///
/// The stack-count dictionary lives SHARED with GrowthEngine (constructed once in Engine.cs,
/// passed to both), keyed by wielder fingerprint (lvl,br,fa), NOT weapon-id-keyed, so two
/// Kiku-ichimonji wielders never cross-arm each other.
///
/// SIMPLER than Iai deliberately: no field-max scan, no wall-clock cap. A window that never
/// closes (a wielder resolve-misses on the exact departure tick, or the ActorPtr entry never
/// validates) just skips that turn's stack evaluation (self-healing next turn), and no live
/// memory write is ever left dangling (unlike Iai's Speed hold) because this module never writes
/// game memory itself; only the shared stack-count dictionary GrowthEngine.Mushin.cs reads on its
/// own guarded write path.</summary>
internal sealed class Mushin : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);

    private const int KikuIchimonjiId = 45;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly Dictionary<(int lvl, int br, int fa), int> _armed;
    private readonly List<(long entry, (int lvl, int br, int fa) fp)> _wielders = new();
    private readonly Dictionary<(int lvl, int br, int fa), Window> _windows = new();

    // Priming (F5, Iai's contract): seed prev-state on the first evaluated tick without deciding.
    private bool _primed;
    private bool _prevActed;

    public Mushin(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
                  Dictionary<(int lvl, int br, int fa), int> armed, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _armed = armed;
    }

    public void ResetBattle()
    {
        _windows.Clear();
        _armed.Clear();
        _primed = false;
    }

    public void Tick(bool onField)
    {
        if (!onField) return;
        if (!_meta.TryGetValue(KikuIchimonjiId, out var m) || m.Signature is null || !m.Signature.Mushin) return;

        int tier = Tuning.TierOf(_kills, KikuIchimonjiId);
        if (tier < m.Signature.AtTier) return;

        Wielder.ResolveDeployedMainHandAll(_mem, KikuIchimonjiId, _wielders);

        // Two identity resolves + one Acted read per tick (not per wielder: the engine has ONE
        // acting entry at a time): the ActorPtr entry drives the arm window, Band.ActiveOwner
        // (TurnQueue) drives act/consume. See the class doc comment for why they must split.
        long acting = Band.ActorEntry(_mem);
        bool actorValid = acting != 0 && Band.IsValid(_mem, acting);
        (int lvl, int br, int fa) actorFp = actorValid
            ? (_mem.U8(acting + Offsets.ALevel), _mem.U8(acting + Offsets.ABrave), _mem.U8(acting + Offsets.AFaith))
            : default;
        bool tqResolved = Band.ActiveOwner(_mem, out var tqFp, out _);
        bool actedNow = _mem.U8(Offsets.Acted) == 1;
        bool evaluate = _primed;
        if (!_primed) { _prevActed = actedNow; _primed = true; }
        bool actedEdge = evaluate && actedNow && !_prevActed;

        foreach (var (_, fp) in _wielders)
        {
            if (!_windows.TryGetValue(fp, out var w)) { w = new Window(); _windows[fp] = w; }
            if (!_armed.ContainsKey(fp)) _armed[fp] = 0;

            bool actorNamesWielder = actorValid && actorFp == fp;   // ARM WINDOW (prompt, ActorPtr)
            bool tqNamesWielder = tqResolved && tqFp == fp;         // ACT + CONSUME (reliable, TurnQueue)

            if (!evaluate) { w.Active = actorNamesWielder; continue; }   // priming: seed state, decide nothing

            if (actorNamesWielder && !w.Active)
            {
                w.StartGx = _mem.U8(acting + Offsets.AGx);
                w.StartGy = _mem.U8(acting + Offsets.AGy);
                w.Moved = false;
                w.Acted = false;
                ModLogger.Debug(LogVerb.Trace,
                    $"mushin window OPEN (lvl {fp.lvl} br {fp.br} fa {fp.fa}) at ({w.StartGx},{w.StartGy})");
            }

            if (actorNamesWielder)
            {
                int gx = _mem.U8(acting + Offsets.AGx), gy = _mem.U8(acting + Offsets.AGy);
                if (gx != w.StartGx || gy != w.StartGy) w.Moved = true;
            }

            // ACT + CONSUME (TurnQueue): independent of the ActorPtr window, so it must run
            // BEFORE the window-END read of w.Acted below (an attack that also closes the window
            // this same tick must still suppress that turn's bank).
            bool wielderActed = actedEdge && tqNamesWielder;
            if (wielderActed)
            {
                w.Acted = true;
                int spent = _armed[fp];
                if (MushinPolicy.ShouldConsume(spent, wielderActed))
                {
                    _armed[fp] = 0;
                    ModLogger.Event(LogVerb.Signature,
                        $"The Kiku-ichimonji wielder's charged strike lands; Mushin's boost is spent ({spent} stack(s); level {fp.lvl}, brave {fp.br}, faith {fp.fa}).");
                }
            }

            if (!actorNamesWielder && w.Active)
            {
                bool shouldArm = MushinPolicy.ShouldArm(turnEnded: true, w.Moved, w.Acted);
                int before = _armed[fp];
                int next = MushinPolicy.NextStacks(before, shouldArm, Tuning.MushinMaxStacks);
                if (next != before)
                {
                    _armed[fp] = next;
                    ModLogger.Event(LogVerb.Signature,
                        $"The Kiku-ichimonji wielder stands perfectly still and banks a Mushin charge ({next} of {Tuning.MushinMaxStacks}) (level {fp.lvl}, brave {fp.br}, faith {fp.fa}).");
                }
                ModLogger.Debug(LogVerb.Trace,
                    $"mushin window CLOSE (lvl {fp.lvl} br {fp.br} fa {fp.fa}): moved={w.Moved} acted={w.Acted} -> {(shouldArm ? "BANK" : "no bank")}");
            }

            w.Active = actorNamesWielder;
        }

        _prevActed = actedNow;
    }

    /// <summary>Mutable per-wielder turn-window state, keyed by roster fingerprint in
    /// <see cref="_windows"/>. A reference type so Tick mutates the SAME instance across ticks.</summary>
    private sealed class Window
    {
        /// <summary>True while the ActorPtr entry's fingerprint names this wielder: the window is
        /// "open" for MOVE sampling. Also doubles as last tick's actorNamesWielder value so a
        /// rising/falling edge can be detected without a second stored field.</summary>
        public bool Active;
        public int StartGx, StartGy;
        public bool Moved;
        public bool Acted;
    }
}
