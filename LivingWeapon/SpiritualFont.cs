using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Umbral Rod's "Spiritual Font" signature. T1 weapons carry no signatures; this was moved
/// from Wellspring Rod (id 51, tier 1) to Umbral Rod (id 56, tier 3) for that reason.
/// At each tick (inLive, active, located via Wielder.LocateAll): reads the wielder's
/// current (gx, gy) from the first located band entry and feeds it to a MoveWatch
/// state machine. When MoveWatch confirms a stable position change (new tile held for
/// StabilityTicks=3 consecutive ticks), the RUNTIME restores Tuning.FontHpPct of max HP
/// (LifeSap.NewHp: floor 1, clamp at full, never revive) and Tuning.FontMpPct of max MP
/// (NewMp: floor 1, clamp at maxMp; never into a corpse). Rate-capped at one fire per
/// RateCap=90 ticks (~3 s). Move-only turns NOW PAY (the old gap is closed).
///
/// TRIGGER HISTORY: four prior designs failed live:
///   (1) Global TurnTracker acted-edge -- the active struct at 0x14077D2A0 follows the CURSOR,
///       not the turn owner; mis-credited every edge to whoever the cursor rested on.
///   (2) Band +0x25 (ACtSlam) CT read -- that byte is ExtraTurn's write target and never
///       ticked reliably for reads; zero transitions observed across full player turns.
///   (3) Band +0x09 (ACtTurn) CT read -- also never reached >= 90 across full player turns
///       in the watcher, even with sampling extended to all live-battle ticks.
///   (4) Actor latch (KillTracker.LastPlayerWeapons) rising edge -- live log showed it fired
///       for action 1 ("action edge 1 ... baseline") then the SECOND player action produced
///       NO latch line, NO turn line, NOTHING -- the global Acted flag (0x14077CA8C) pulses
///       unreliably (the documented condensed-struct-follows-cursor / pulse trap family).
///       Also cost kill credits when shared with KillTracker. Every engine-bookkeeping signal
///       has now failed; position-poll needs no engine cooperation.
/// Position-poll design: the pure MoveWatch class (SpiritualFont.Policy.cs) maintains a
/// position snapshot and a 3-tick stability gate with a 90-tick rate cap. No engine flags.
///
/// MP SAFETY: the band +0x18/+0x1A (mp/maxMp) pair is LIVE-VERIFIED on screen 2026-06-10.
/// Every MP write is gated behind a per-battle layout validation (MpLayoutOk, pure + tested):
/// on the first field tick with >= 2 valid band units, every valid unit's mp/maxMp must read
/// sane or MP writes stay disabled for the battle (the HP half still fires). Each MP write is
/// re-read and logged SET/MISS. All writes guarded (Mem).
/// </summary>
internal sealed partial class SpiritualFont : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField, ctx.InLive);
    private const int UmbralId = 56;

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    // KillTracker parameter retained for Engine wiring stability (nothing read from it here).
    private readonly List<int> _hands = new();
    private readonly List<long> _locateAllBuf = new();   // reused buffer for LocateAll calls
    private readonly MoveWatch _watch = new();
    private int _moveCount;                // moves fired this battle (for logging)
    private bool _wasActive;
    private int _pulseTicks;             // DEV PULSE clock (Tuning.FontDevPulse; ~10s at 33ms)
    private bool _wasLocated;            // diagnostic: located-state change is logged once per flip
    private bool _mpChecked;             // per-battle MP layout verdict latched?
    private bool _mpOk;

    public SpiritualFont(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        // tracker not stored: no latch machinery remains. Parameter kept so Engine.cs wires cleanly.
    }

    public void ResetBattle()
    {
        _watch.Reset();
        _moveCount = 0;
        _wasActive = false;
        _wasLocated = false;
        _mpChecked = false;
        _mpOk = false;
    }

    public void Tick(bool onField, bool inLive)
    {
        if (!inLive) return;
        if (!_meta.TryGetValue(UmbralId, out var m) || m.Signature is null) return;
        if (onField && !_mpChecked) ValidateMpLayout();   // band most coherent on field ticks; latches once
        int tier = Tuning.TierOf(_kills, UmbralId);
        (int lvl, int br, int fa) fp = default;
        bool active = IsActive(m.Signature, tier) && Wielder.TryResolveMainHand(_mem, UmbralId, out fp, _hands);
        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info($"font {(active ? "ACTIVE -- Umbral Rod at +3 is wielded, move-triggered HP/MP restore is enabled" : "inactive")}");
        }
        if (!active)
        {
            _watch.Reset();   // unequip: re-baseline the MoveWatch
            return;
        }

        // DEV PULSE: trigger bypass -- force a replenish every ~10s (300 ticks) so the band
        // addressing + HP write + provisional MP pair can be verified ON SCREEN, independent
        // of any edge detection. External RPM probes are Denuvo-walled; this is the instrument.
        // Verified live 2026-06-10: dev-pulse replenish visibly raised HP, clamped at max.
        if (Tuning.FontDevPulse && ++_pulseTicks >= 300)
        {
            _pulseTicks = 0;
            _locateAllBuf.Clear();
            Wielder.LocateAll(_mem, UmbralId, _hands, fp, _locateAllBuf);
            Log.Info($"font: DEV PULSE -- {(_locateAllBuf.Count == 0 ? "wielder could not be located" : "forcing a replenish to verify HP/MP writes")}");
            if (_locateAllBuf.Count > 0) ReplenishAll(_locateAllBuf, 0);
            else Wielder.DumpCandidates(_mem, _hands, fp);   // name the rejecting predicate
        }

        _locateAllBuf.Clear();
        Wielder.LocateAll(_mem, UmbralId, _hands, fp, _locateAllBuf);
        bool nowLocated = _locateAllBuf.Count > 0;
        if (nowLocated != _wasLocated)
        {
            _wasLocated = nowLocated;
            Log.Info($"font: wielder {(_wasLocated ? "located in memory -- tracking position" : "not found this tick -- skipping")}");
        }
        if (_locateAllBuf.Count == 0) return;   // unlocated: skip; MoveWatch keeps its baseline

        long e = _locateAllBuf[0];   // read position from the first entry (live or frozen -- same tile)
        int gx = _mem.U8(e + Offsets.AGx), gy = _mem.U8(e + Offsets.AGy);
        bool fire = _watch.Observe(gx, gy);
        if (_watch.IsFresh) Log.Info($"font: position baseline set -- wielder is at ({gx},{gy}), watching for movement");   // first sighting log
        if (!fire) return;

        _moveCount++;
        Log.Info($"font: wielder moved to a new tile ({gx},{gy}) -- triggering HP/MP restore (move #{_moveCount} this battle)");
        ReplenishAll(_locateAllBuf, _moveCount);
    }

    /// <summary>One moved-turn restore written to every located twin (idempotent -- the live
    /// copy takes effect, frozen twins are inert). HP/MP amounts computed once from the first
    /// entry; HP written to all entries, MP written to all entries but read-back logged on the
    /// first only (avoids log spam for the frozen copy). HP half always (clamped, never revives);
    /// MP half only for a LIVING wielder while layout validation passed (MpHalfAllowed).</summary>
    private void ReplenishAll(List<long> entries, int move)
    {
        long first = entries[0];
        int hp = _mem.U16(first + Offsets.AHp), maxHp = _mem.U16(first + Offsets.AMaxHp);
        int newHp = LifeSap.NewHp(hp, maxHp, LifeSap.HealAmount(maxHp, Tuning.FontHpPct));
        Log.Info($"font: move #{move} -- restored HP {hp}->{newHp} (max {maxHp}, {entries.Count} band entries updated)");
        foreach (long ent in entries) if (newHp != hp) LifeSap.WriteHp(_mem, ent, newHp);
        if (!MpHalfAllowed(hp, _mpOk)) return;   // layout unproven OR a dead wielder: no MP write
        int mp = _mem.U16(first + Offsets.AMp), maxMp = _mem.U16(first + Offsets.AMaxMp);
        int newMp = NewMp(mp, maxMp, LifeSap.HealAmount(maxMp, Tuning.FontMpPct));
        if (newMp == mp) { Log.Info($"font: MP already full ({mp}/{maxMp}) -- no MP restore needed"); return; }
        foreach (long ent in entries) WriteMp(_mem, ent, newMp);
        bool landed = _mem.U16(first + Offsets.AMp) == newMp;   // read-back on first entry only
        Log.Info($"font: restored MP {mp}->{newMp} (max {maxMp}) {(landed ? "(write verified)" : "(write did NOT stick)")}");
    }

    /// <summary>The per-battle gate on the provisional MP offsets: sample every valid band unit's
    /// mp/maxMp and latch the pure verdict once &gt;= 2 units are visible (fewer: retry next tick).
    /// On fail, MP writes stay off for the whole battle -- the HP half is unaffected.</summary>
    private void ValidateMpLayout()
    {
        var samples = new List<(int mp, int maxMp)>();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long ent = Band.Entry(s);
            if (!Band.IsValid(_mem, ent)) continue;
            samples.Add((_mem.U16(ent + Offsets.AMp), _mem.U16(ent + Offsets.AMaxMp)));
        }
        if (samples.Count < 2) return;   // band not populated yet -- validate on a later tick
        _mpChecked = true;
        _mpOk = MpLayoutOk(samples);
        Log.Info(_mpOk ? $"font: MP field layout verified across {samples.Count} units -- MP restores enabled this battle"
                       : "font: MP field layout check failed -- HP-only restores for this battle");
    }
}
