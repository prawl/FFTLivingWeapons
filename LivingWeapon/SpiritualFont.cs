using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Wellspring Rod's "Spiritual Font" signature: at each action edge of the +3 wielder
/// (the actor latch OPENS with weapon id 51 -- a rising edge in KillTracker.LastPlayerWeapons),
/// IF the wielder's grid position changed since their PREVIOUS action edge, the RUNTIME
/// restores Tuning.FontHpPct of max HP (LifeSap.NewHp: floor 1, clamp at full, never revive)
/// and Tuning.FontMpPct of max MP (NewMp: floor 1, clamp at maxMp; never into a corpse).
///
/// TRIGGER HISTORY: three prior designs failed live:
///   (1) Global TurnTracker acted-edge -- the active struct at 0x14077D2A0 follows the CURSOR,
///       not the turn owner; mis-credited every edge to whoever the cursor rested on.
///   (2) Band +0x25 (ACtSlam) CT read -- that byte is ExtraTurn's write target and never
///       ticked reliably for reads; zero transitions observed across full player turns.
///   (3) Band +0x09 (ACtTurn) CT read -- also never reached >= 90 across full player turns
///       in the watcher, even with sampling extended to all live-battle ticks.
/// The actor latch (KillTracker.LastPlayerWeapons) is the proven signal: it powers kill
/// attribution live and logged "active player weapon(s) -> 51" on every wielder action.
///
/// KNOWN GAP: a move-only turn (unit moves but never acts) raises no latch; its movement is
/// credited at the wielder's NEXT action instead (the position delta accumulates) -- late,
/// never lost. Documented in items.json and docs/living_weapon_rods.csv.
///
/// MP SAFETY: the band +0x18/+0x1A (mp/maxMp) pair is PROVISIONAL -- never live-verified.
/// Every MP write is gated behind a per-battle layout validation (MpLayoutOk, pure + tested):
/// on the first field tick with >= 2 valid band units, every valid unit's mp/maxMp must read
/// sane or MP writes stay disabled for the battle (the HP half still fires). Each MP write is
/// re-read and logged SET/MISS. Position snapshots happen ONLY at action edges; the first edge
/// of a battle (or after a re-equip) snapshots without firing. All writes guarded (Mem).
/// </summary>
internal sealed partial class SpiritualFont
{
    private const int WellspringId = 51;

    private static readonly LiveMemory Live = new();   // Wielder/Band reads ride IGameMemory; == Mem

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly List<int> _hands = new();
    private bool _latchWasIn;            // was WellspringId in the latch last tick?
    private int _actionEdgeCount;        // edges seen this battle (for logging)
    private bool _posKnown;              // a action-edge position snapshot exists
    private int _lastGx, _lastGy;       // the wielder's tile at their previous action edge
    private bool _wasActive;
    private bool _wasLocated;            // diagnostic: located-state change is logged once per flip
    private bool _mpChecked;             // per-battle MP layout verdict latched?
    private bool _mpOk;

    public SpiritualFont(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker)
    {
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
    }

    public void ResetBattle()
    {
        _latchWasIn = false;
        _actionEdgeCount = 0;
        _posKnown = false;
        _wasActive = false;
        _wasLocated = false;
        _mpChecked = false;
        _mpOk = false;
    }

    public void Tick(bool onField, bool inLive)
    {
        if (!inLive) return;
        if (!_meta.TryGetValue(WellspringId, out var m) || m.Signature is null) return;
        if (onField && !_mpChecked) ValidateMpLayout();   // band most coherent on field ticks; latches once
        int tier = Tuning.TierFor(_kills.TryGetValue(WellspringId, out int k) ? k : 0);
        (int lvl, int br, int fa) fp = default;
        bool active = IsActive(m.Signature, tier) && Wielder.TryResolve(Live, WellspringId, out fp, _hands);
        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info($"font {(active ? "ACTIVE (+3 Wellspring Rod wielded)" : "inactive")}");
        }
        if (!active)
        {
            _latchWasIn = false; _posKnown = false;   // unequip: re-baseline latch AND tile
            return;
        }

        bool isIn = _tracker.LastPlayerWeapons.Contains(WellspringId);
        bool edge = IsLatchEdge(_latchWasIn, isIn);
        _latchWasIn = isIn;
        if (!edge) return;

        long e = Wielder.Locate(Live, WellspringId, _hands, fp);
        bool nowLocated = e != 0;
        if (nowLocated != _wasLocated)
        {
            _wasLocated = nowLocated;
            Log.Info($"font: wielder {(_wasLocated ? "located" : "UNLOCATED (action edge skipped)")}");
        }
        if (e == 0) return;   // unlocated: skip, position snapshot deferred

        _actionEdgeCount++;
        int gx = Live.U8(e + Offsets.AGx), gy = Live.U8(e + Offsets.AGy);
        bool fire = ShouldFire(_posKnown, _lastGx, _lastGy, gx, gy);
        Log.Info($"font: action edge {_actionEdgeCount} at ({gx},{gy}) " +
                 (fire ? "moved -> firing" : _posKnown ? "no move -> skip" : "first action -> baseline"));
        _lastGx = gx; _lastGy = gy; _posKnown = true;   // snapshot per action edge
        if (!fire) return;
        Replenish(e, _actionEdgeCount);
    }

    /// <summary>One moved-action restore: the HP half always (clamped, never revives), the MP half
    /// only for a LIVING wielder while this battle's layout validation passed (MpHalfAllowed --
    /// a corpse gains nothing) -- with a post-write SET/MISS read-back (the live verification
    /// signal for the provisional offsets).</summary>
    private void Replenish(long e, int edge)
    {
        int hp = Live.U16(e + Offsets.AHp), maxHp = Live.U16(e + Offsets.AMaxHp);
        int newHp = LifeSap.NewHp(hp, maxHp, LifeSap.HealAmount(maxHp, Tuning.FontHpPct));
        if (newHp != hp) LifeSap.WriteHp(e, newHp);
        Log.Info($"font: moved -> edge {edge} hp {hp} -> {newHp} (max {maxHp})");
        if (!MpHalfAllowed(hp, _mpOk)) return;   // layout unproven OR a dead wielder: no MP write
        int mp = Live.U16(e + Offsets.AMp), maxMp = Live.U16(e + Offsets.AMaxMp);
        int newMp = NewMp(mp, maxMp, LifeSap.HealAmount(maxMp, Tuning.FontMpPct));
        if (newMp == mp) { Log.Info($"font: mp full ({mp}/{maxMp})"); return; }
        WriteMp(e, newMp);
        bool landed = Live.U16(e + Offsets.AMp) == newMp;   // re-read after EVERY MP write
        Log.Info($"font: mp {mp} -> {newMp} (max {maxMp}) readback={(landed ? "SET" : "MISS")}");
    }

    /// <summary>The per-battle gate on the provisional MP offsets: sample every valid band unit's
    /// mp/maxMp and latch the pure verdict once &gt;= 2 units are visible (fewer: retry next tick).
    /// On fail, MP writes stay off for the whole battle -- the HP half is unaffected.</summary>
    private void ValidateMpLayout()
    {
        var samples = new List<(int mp, int maxMp)>();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(Live, e)) continue;
            samples.Add((Live.U16(e + Offsets.AMp), Live.U16(e + Offsets.AMaxMp)));
        }
        if (samples.Count < 2) return;   // band not populated yet -- validate on a later tick
        _mpChecked = true;
        _mpOk = MpLayoutOk(samples);
        Log.Info(_mpOk ? $"font: MP layout OK ({samples.Count} units)"
                       : "font: MP layout validation FAILED - HP-only this battle");
    }
}
