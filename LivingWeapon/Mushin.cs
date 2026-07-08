using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Kiku-ichimonji's "Mushin" signature: a full WAIT turn (no move, no action) banks one stack
/// of a power boost, up to Tuning.MushinMaxStacks (3); the wielder's next attack spends every
/// banked stack in one boosted hit, then the buff clears (MushinPolicy.NextStacks / ShouldConsume,
/// see their doc comments).
///
/// CT-DRIVEN (2026-07-07, replaces the ActorPtr/TurnQueue hybrid): live testing showed the
/// ActorPtr arm window flickering on/off the wielder (spurious banks) while the TurnQueue consume
/// false-fired on cursor-follow: both structs track the CURSOR/UI, not the true turn owner.
/// This module now drives turn detection off the wielder's OWN scheduler CT (Offsets.ACtSlam,
/// band +0x25), the same byte Maim/CharmLock already read cleanly for enemy turns (CtTurns.
/// TurnHi/TurnLo): CT climbs to a ceiling then resets low exactly once per turn. Per deployed
/// main-hand wielder (Wielder.ResolveDeployedMainHandAll, twin-filtered so a frozen (0,0) mirror
/// never wins over the real seat), a small state machine tracks Idle -> Active -> Settling:
///   - Idle: waiting. CT reaching CtCeiling means this wielder's turn has begun; snapshot its
///     grid position and go Active.
///   - Active: sample MOVE (grid position diffed from the snapshot) and ACT (the global Acted
///     byte rising while this wielder is the one sitting at the CT ceiling; cursor-independent,
///     since only the truly active unit's CT sits up there). CT falling below CtFloor is the
///     scheduler's own reset, so go Settling.
///   - Settling: keep sampling MOVE/ACT for SettleTicks more ticks (~0.2s grace so an action edge
///     landing right at/after the reset is still caught; live evidence: SPENT fired the instant
///     the CT read 8). If CT climbs back to the ceiling first, the reset was a blip inside the
///     same turn, so return to Active without deciding. Otherwise, once the grace runs out, decide
///     once: an own-turn ACT consumes every banked stack; otherwise a turn with neither MOVE nor
///     ACT banks one (MushinPolicy.ShouldArm/NextStacks); either way the phase returns to Idle.
///
/// The stack-count dictionary lives SHARED with GrowthEngine (constructed once in Engine.cs,
/// passed to both), keyed by wielder fingerprint (lvl,br,fa), NOT weapon-id-keyed, so two
/// Kiku-ichimonji wielders never cross-arm each other.
///
/// A wielder that resolve-misses a tick (Wielder.Locate ambiguity-bails, e.g. mirror churn) is
/// simply skipped that tick; its phase/snapshot sit frozen until it resolves again next tick
/// (self-healing; no live memory write is ever left dangling because this module never writes
/// game memory itself, only the shared stack-count dictionary GrowthEngine.Mushin.cs reads on its
/// own guarded write path).
/// </summary>
internal sealed class Mushin : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);

    private const int KikuIchimonjiId = 45;

    /// <summary>CT (Offsets.ACtSlam) at/above this means the wielder's turn is active. Live-proven
    /// pattern (Maim/CharmLock, CtTurns.TurnHi): the scheduler climbs to ~96 on a unit's turn.</summary>
    internal const int CtCeiling = 90;
    /// <summary>CT below this, after having reached CtCeiling, confirms the scheduler's reset --
    /// one turn taken (CtTurns.TurnLo). Between 70 and 90 is dead zone, never a phase edge.</summary>
    internal const int CtFloor = 70;
    /// <summary>Ticks of grace after the CT reset before deciding, so an action edge landing right
    /// at/just after the reset (live evidence: SPENT fired the instant the CT read 8, i.e. AT the
    /// reset) is still caught. ~0.2s at the 33ms battle poll.</summary>
    internal const int SettleTicks = 6;

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

        // One Acted read per tick (not per wielder: the engine has ONE acting entry at a time).
        bool actedNow = _mem.U8(Offsets.Acted) == 1;
        bool evaluate = _primed;
        if (!_primed) { _prevActed = actedNow; _primed = true; }
        bool actedEdge = evaluate && actedNow && !_prevActed;

        foreach (var (entry, fp) in _wielders)
        {
            if (!_windows.TryGetValue(fp, out var w)) { w = new Window(); _windows[fp] = w; }
            if (!_armed.ContainsKey(fp)) _armed[fp] = 0;

            int ct = _mem.U8(entry + Offsets.ACtSlam);
            int gx = _mem.U8(entry + Offsets.AGx), gy = _mem.U8(entry + Offsets.AGy);

            if (!evaluate)
            {
                // Priming: seed this wielder's phase from its CURRENT CT without deciding anything.
                w.Phase = ct >= CtCeiling ? Phase.Active : Phase.Idle;
                w.StartGx = gx; w.StartGy = gy; w.Moved = false; w.Acted = false;
                continue;
            }

            switch (w.Phase)
            {
                case Phase.Idle:
                    if (ct >= CtCeiling)
                    {
                        w.Phase = Phase.Active;
                        w.StartGx = gx; w.StartGy = gy; w.Moved = false; w.Acted = false;
                        ModLogger.Debug(LogVerb.Trace,
                            $"mushin turn ACTIVE (lvl {fp.lvl} br {fp.br} fa {fp.fa}) ct={ct} at ({gx},{gy})");
                    }
                    break;

                case Phase.Active:
                    if (gx != w.StartGx || gy != w.StartGy) w.Moved = true;
                    if (actedEdge) w.Acted = true;
                    if (ct < CtFloor)
                    {
                        w.Phase = Phase.Settling;
                        w.SettleLeft = SettleTicks;
                        ModLogger.Debug(LogVerb.Trace,
                            $"mushin turn RESET ct={ct} (was high) (lvl {fp.lvl} br {fp.br} fa {fp.fa})");
                    }
                    break;

                case Phase.Settling:
                    if (gx != w.StartGx || gy != w.StartGy) w.Moved = true;
                    if (actedEdge) w.Acted = true;
                    if (ct >= CtCeiling) { w.Phase = Phase.Active; break; }   // defensive: still this turn
                    if (--w.SettleLeft > 0) break;

                    string verdict = "idle";
                    if (w.Acted)
                    {
                        int spent = _armed[fp];
                        if (MushinPolicy.ShouldConsume(spent, ownTurnActedEdge: true))
                        {
                            _armed[fp] = 0;
                            verdict = $"SPENT({spent})";
                            ModLogger.Event(LogVerb.Signature,
                                $"The Kiku-ichimonji wielder's charged strike lands; Mushin's boost is spent ({spent} stack(s); level {fp.lvl}, brave {fp.br}, faith {fp.fa}).");
                        }
                    }
                    else if (MushinPolicy.ShouldArm(turnEnded: true, w.Moved, w.Acted))
                    {
                        int before = _armed[fp];
                        int next = MushinPolicy.NextStacks(before, shouldArm: true, Tuning.MushinMaxStacks);
                        if (next != before)
                        {
                            _armed[fp] = next;
                            verdict = $"BANK({next})";
                            ModLogger.Event(LogVerb.Signature,
                                $"The Kiku-ichimonji wielder stands perfectly still and banks a Mushin charge ({next} of {Tuning.MushinMaxStacks}) (level {fp.lvl}, brave {fp.br}, faith {fp.fa}).");
                        }
                    }
                    ModLogger.Debug(LogVerb.Trace,
                        $"mushin turn DONE: moved={w.Moved} acted={w.Acted} ct={ct} -> {verdict}");
                    w.Phase = Phase.Idle;
                    break;
            }
        }

        _prevActed = actedNow;
    }

    private enum Phase { Idle, Active, Settling }

    /// <summary>Mutable per-wielder turn state, keyed by roster fingerprint in
    /// <see cref="_windows"/>. A reference type so Tick mutates the SAME instance across ticks.</summary>
    private sealed class Window
    {
        public Phase Phase;
        public int StartGx, StartGy;
        public bool Moved;
        public bool Acted;
        public int SettleLeft;
    }
}
