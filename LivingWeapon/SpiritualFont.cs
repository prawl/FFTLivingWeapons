using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Wellspring Rod's "Spiritual Font" signature, REWORKED: at each of the +3 wielder's
/// completed-turn edges, IF the wielder's grid position changed since their PREVIOUS turn
/// edge, the RUNTIME restores Tuning.FontHpPct of max HP (LifeSap.NewHp: floor 1, clamp at
/// full, never revive) and Tuning.FontMpPct of max MP -- never into a corpse (MpHalfAllowed).
///
/// THE EDGE is the wielder's OWN scheduler CT pull-down (CtTurns, band +0x09 / ACtTurn --
/// Maim's READ-PROVEN victim-turn byte; NOT +0x25 / ACtSlam which is ExtraTurn's write target
/// and does not tick reliably for reads), NOT the global acted-edge TurnTracker: the acted edge fingerprints
/// the active struct at 0x14077D2A0, which follows the CURSOR and mis-credited turns live
/// (every edge credited one fingerprint -- it stalled Rapture's expiry the same way). An
/// unlocated wielder simply pauses the clock; a missed pull-down lands late, never lost.
///
/// WHY runtime writes, not engine passives: the live test (2026-06-10) proved both Lifefont
/// (237) and Manafont (238) bits OR-hold perfectly on +0x9C, but the engine honors exactly
/// ONE movement passive and picked Lifefont -- only HP ticked on move. So the bit grant is
/// retired and the runtime does the font work itself.
///
/// MP SAFETY: the band +0x18/+0x1A (mp/maxMp) pair is PROVISIONAL -- never live-verified.
/// Every MP write is gated behind a per-battle layout validation (MpLayoutOk, pure + tested):
/// on the first field tick with >= 2 valid band units, every valid unit's mp/maxMp must read
/// sane or MP writes stay disabled for the battle (the HP half still fires). Each MP write is
/// re-read and logged SET/MISS. Position snapshots happen ONLY at turn edges; the first edge
/// of a battle (or after a re-equip) snapshots without firing. All writes guarded (Mem).
/// </summary>
internal sealed partial class SpiritualFont
{
    private const int WellspringId = 51;

    private static readonly LiveMemory Live = new();   // Wielder/Band reads ride IGameMemory; == Mem

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly List<int> _hands = new();
    private readonly CtTurns _ct = new();   // the wielder's OWN scheduler-CT turn clock
    private int _lastDone;             // completed turns already consumed off the clock
    private bool _posKnown;            // a turn-edge position snapshot exists
    private int _lastGx, _lastGy;      // the wielder's tile at their previous turn edge
    private bool _wasActive;
    private bool _mpChecked;           // per-battle MP layout verdict latched?
    private bool _mpOk;

    // The TurnTracker parameter is retained for wiring stability (Engine constructs every
    // subsystem identically) but deliberately unused: the edge is the wielder's own CT.
    public SpiritualFont(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, TurnTracker turns)
    {
        _meta = meta;
        _kills = kills;
    }

    public void ResetBattle()
    {
        _ct.Reset();
        _lastDone = 0;
        _posKnown = false;
        _wasActive = false;
        _mpChecked = false;
        _mpOk = false;
    }

    public void Tick(bool onField)
    {
        if (!onField) return;
        if (!_meta.TryGetValue(WellspringId, out var m) || m.Signature is null) return;
        if (!_mpChecked) ValidateMpLayout();
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
            _ct.Reset(); _lastDone = 0; _posKnown = false;   // unequip: re-baseline clock AND tile
            return;
        }

        long e = Wielder.Locate(Live, WellspringId, _hands, fp);
        if (e == 0) return;                                  // unlocated: the CT clock pauses
        _ct.Observe(Live.U8(e + Offsets.ACtTurn));            // own-CT pull-down = a completed turn
        bool edge = _ct.Completed > _lastDone;
        _lastDone = _ct.Completed;
        if (!edge) return;

        int gx = Live.U8(e + Offsets.AGx), gy = Live.U8(e + Offsets.AGy);
        bool fire = ShouldFire(_posKnown, _lastGx, _lastGy, gx, gy);
        _lastGx = gx; _lastGy = gy; _posKnown = true;                  // snapshot per turn edge
        if (!fire) return;
        Replenish(e, _ct.Completed);
    }

    /// <summary>One moved-turn restore: the HP half always (clamped, never revives), the MP half
    /// only for a LIVING wielder while this battle's layout validation passed (MpHalfAllowed --
    /// a corpse gains nothing) -- with a post-write SET/MISS read-back (the live verification
    /// signal for the provisional offsets).</summary>
    private void Replenish(long e, int turn)
    {
        int hp = Live.U16(e + Offsets.AHp), maxHp = Live.U16(e + Offsets.AMaxHp);
        int newHp = LifeSap.NewHp(hp, maxHp, LifeSap.HealAmount(maxHp, Tuning.FontHpPct));
        if (newHp != hp) LifeSap.WriteHp(e, newHp);
        Log.Info($"font: moved -> turn {turn} hp {hp} -> {newHp} (max {maxHp})");
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
