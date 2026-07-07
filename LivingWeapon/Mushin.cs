using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Kiku-ichimonji's "Mushin" signature: a full WAIT turn (no move, no action) banks one stack
/// of a power boost, up to Tuning.MushinMaxStacks (3); the wielder's next attack spends every
/// banked stack in one boosted hit, then the buff clears (MushinPolicy.NextStacks / ShouldConsume,
/// see their doc comments).
///
/// Turn-window apparatus mirrors Iai.cs's proven ActorPtr apparatus: the engine's own acting-
/// unit pointer (Offsets.ActorPtr, resolved to a band-entry address via Band.ActorEntry) yields
/// the ACTING entry; that entry's frame nameId (Offsets.ANameId) is the IDENTITY this module
/// tracks against. Between arrival and departure, this module samples:
///   - MOVE: the wielder's grid position (Offsets.AGx/AGy), read off the wielder's LOCATED band
///     entry (Wielder.ResolveDeployedMainHandAll), diffs from its turn-start snapshot.
///   - ACT: the global Acted byte (Offsets.Acted) rising 0-&gt;1 while the acting entry still
///     identity-matches the wielder, the same S2 signal Iai.Policy.ReleaseSignal uses, scoped the
///     same way. An Acted edge that fires while the acting entry names a DIFFERENT unit (an
///     enemy's turn) never satisfies this, so it neither arms nor consumes (own-turn scoping).
/// At departure, MushinPolicy.NextStacks decides whether the just-closed turn banks a stack
/// (neither MOVE nor ACT happened). An ACT edge observed DURING the window also offers an
/// immediate consume (MushinPolicy.ShouldConsume) when at least one stack is already banked: an
/// attack spends every stack the instant it lands, not at the end of that attacking turn.
///
/// IDENTITY HARDENING (2026-07-07, ports Iai.cs's v2 rebuild): the engine mirrors a wielder at a
/// second identical combat slot (confirmed live at slots 24 and 28), which makes the ACTING
/// entry's ADDRESS and the wielder's LOCATED entry ADDRESS disagree at the wrong moments --
/// Wielder.Locate's deterministic tie-break can hand back the mirror's address while ActorPtr
/// always names the real frame. Comparing raw addresses (acting == entry) then live-glitched two
/// ways: a wait sometimes false-consumed the buff, and an attack sometimes failed to consume it.
/// Fix: each wielder's window captures its roster nameId ONCE, at window-creation time
/// (Wielder.RosterNameId, reading the ROSTER copy -- unaffected by band mirror confusion), and
/// arrival/departure/the own-acted-edge all identity-match the ACTING entry's frame nameId
/// (Offsets.ANameId, one guarded U16 read per tick) against that capture, instead of comparing
/// band-entry addresses. The nameId&gt;0 guard is LOAD-BEARING (the "0==0 trap" Iai documents): a
/// failed capture (RosterNameId &lt;= 0) must never match an equally-failed acting read of 0. A
/// wielder whose capture fails falls back to the original address compare (degraded, like Iai's
/// fallback, never worse). The MOVE read still uses the wielder's LOCATED entry (Locate's
/// address), unaffected by this hardening -- only arrival/departure/own-acted-edge changed.
///
/// The stack-count dictionary lives SHARED with GrowthEngine (constructed once in Engine.cs,
/// passed to both), keyed by wielder fingerprint (lvl,br,fa), like Iai's fp-keyed _holds, NOT
/// weapon-id-keyed, so two Kiku-ichimonji wielders never cross-arm each other.
///
/// SIMPLER than Iai deliberately: no field-max scan, no wall-clock cap. A window that never
/// closes (a wielder resolve-misses on the exact departure tick, or a mirror clone confuses that
/// one sample) just skips that turn's stack evaluation (self-healing next turn), and no live
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
    private long _prevActing;
    private bool _prevActed;
    private int _prevActingNameId;

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

        // One pointer + one Acted read per tick (not per wielder: the engine has ONE acting
        // unit at a time), exactly Iai's pattern.
        long acting = Band.ActorEntry(_mem);
        bool actedNow = _mem.U8(Offsets.Acted) == 1;
        bool evaluate = _primed;
        // One guarded read for the acting entry's frame nameId (Offsets.ANameId). U16 fail-safes
        // to 0 on an unreadable/invalid address, which a wielder's NameId>0 guard never matches
        // (the 0==0 trap) -- see Mushin.Policy.cs / Iai.cs's identical pattern.
        int actingNameId = acting != 0 ? _mem.U16(acting + Offsets.ANameId) : 0;
        if (!_primed) { _prevActing = acting; _prevActed = actedNow; _prevActingNameId = actingNameId; _primed = true; }

        foreach (var (entry, fp) in _wielders)
        {
            if (!_windows.TryGetValue(fp, out var w))
            {
                // Arm-time identity capture (Iai's pattern): read the ROSTER copy, unaffected by
                // band mirror confusion. <= 0 (ambiguous or unseeded) routes this wielder to the
                // address-fallback path below for the whole battle.
                w = new Window { NameId = Wielder.RosterNameId(_mem, KikuIchimonjiId, fp) };
                _windows[fp] = w;
            }
            if (!_armed.ContainsKey(fp)) _armed[fp] = 0;
            if (!evaluate) continue;

            bool haveId = w.NameId > 0;
            bool arrived = haveId
                ? actingNameId == w.NameId && _prevActingNameId != w.NameId
                : acting == entry && acting != 0 && acting != _prevActing;
            bool departed = haveId
                ? _prevActingNameId == w.NameId && actingNameId != w.NameId
                : _prevActing == entry && acting != entry;

            if (arrived)
            {
                w.Tracking = true;
                w.StartGx = _mem.U8(entry + Offsets.AGx);
                w.StartGy = _mem.U8(entry + Offsets.AGy);
                w.Moved = false;
                w.Acted = false;
            }

            if (w.Tracking)
            {
                int gx = _mem.U8(entry + Offsets.AGx), gy = _mem.U8(entry + Offsets.AGy);
                if (gx != w.StartGx || gy != w.StartGy) w.Moved = true;

                bool ownActedEdge = haveId
                    ? actingNameId == w.NameId && actedNow && !_prevActed
                    : acting == entry && actedNow && !_prevActed;
                if (ownActedEdge)
                {
                    w.Acted = true;
                    int spent = _armed[fp];
                    if (MushinPolicy.ShouldConsume(spent, ownActedEdge))
                    {
                        _armed[fp] = 0;
                        ModLogger.Event(LogVerb.Signature,
                            $"The Kiku-ichimonji wielder's charged strike lands; Mushin's boost is spent ({spent} stack(s); level {fp.lvl}, brave {fp.br}, faith {fp.fa}).");
                    }
                }
            }

            if (departed && w.Tracking)
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
                w.Tracking = false;
            }
        }

        _prevActing = acting;
        _prevActed = actedNow;
        _prevActingNameId = actingNameId;
    }

    /// <summary>Mutable per-wielder turn-window state, keyed by roster fingerprint in
    /// <see cref="_windows"/>. A reference type so Tick mutates the SAME instance across ticks.</summary>
    private sealed class Window
    {
        public bool Tracking;
        public int StartGx, StartGy;
        public bool Moved;
        public bool Acted;
        /// <summary>Roster nameId captured ONCE at window-creation time (Wielder.RosterNameId);
        /// &lt;= 0 = capture failed (ambiguous or unseeded) -- routes this wielder's
        /// arrival/departure/own-acted-edge to the address-fallback path for the whole battle.</summary>
        public int NameId = -1;
    }
}
