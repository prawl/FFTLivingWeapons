using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Umbral Rod's Spiritual Font signature, which restores HP and MP as the wielder changes tile.
/// It moved from Wellspring Rod (id 51, tier 1) to Umbral Rod (id 56, tier 3) because tier 1 weapons carry no signatures.
/// When the wielder's tile changes and holds for 3 ticks (MoveWatch, SpiritualFont.Policy.cs), it restores Tuning.FontHpPct
/// of max HP and Tuning.FontMpPct of max MP, at most once per 90 ticks (about 3 s). Position polling is used because four
/// engine-flag triggers failed in play. The global TurnTracker struct at 0x14077D2A0 follows the cursor, band +0x25 and
/// +0x09 CT reads never ticked reliably, and the KillTracker actor latch missed the second action because the Acted flag at
/// 0x14077CA8C pulses unreliably. The MP offsets (band +0x18/+0x1A) were checked on screen 2026-06-10, and MP writes stay
/// off for a battle unless MpLayoutOk passes on the first field tick with 2 or more valid units.
/// </summary>
internal sealed partial class SpiritualFont : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField, ctx.InLive);
    private const int UmbralId = 56;

    private readonly IGameMemory _mem;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly List<int> _hands = new();
    private readonly List<long> _locateAllBuf = new();   // reused buffer for LocateAll calls
    private readonly MoveWatch _watch = new();
    private readonly ScopedLogger _slog;   // armed gate. A benched +3 rod stays quiet
    private int _moveCount;                // moves fired this battle (for logging)
    private bool _wasActive;
    private int _pulseTicks;             // Tuning.FontDevPulse clock
    private bool _wasLocated;            // logs located-state flips once
    private bool _mpChecked;             // MP layout verdict latched
    private bool _mpOk;

    public SpiritualFont(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _slog = ModLogger.For(LogVerb.Signature, () => Wielder.AnyDeployedMainHand(_mem, UmbralId));
        // tracker is unused; the parameter stays so Engine.cs wiring does not change.
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
        int tier = Tuning.TierOf(_kills, UmbralId);
        (int lvl, int br, int fa) fp = default;
        bool active = IsActive(m.Signature, tier) && Wielder.TryResolveMainHand(_mem, UmbralId, out fp, _hands);
        if (ActivationEdge.Step(ref _wasActive, active))
        {
            _slog.Info(active
                ? "Umbral Rod at tier three is wielded on the field; moving restores the wielder's HP and MP."
                : "The spiritual font is no longer active.");
        }
        if (!active)
        {
            _watch.Reset();   // unequipped. Re-baseline
            return;
        }

        // Only validate once a rod wielder is confirmed, so battles without the rod never sample the band.
        if (onField && !_mpChecked) ValidateMpLayout();

        // Dev pulse. Force a replenish every 300 ticks (about 10 s) to check the band addressing and
        // writes on screen without movement. Checked 2026-06-10.
        if (Tuning.FontDevPulse && ++_pulseTicks >= 300)
        {
            _pulseTicks = 0;
            _locateAllBuf.Clear();
            Wielder.LocateAll(_mem, UmbralId, _hands, fp, _locateAllBuf);
            ModLogger.Debug(LogVerb.Signature, $"font development pulse {(_locateAllBuf.Count == 0 ? "could not locate the wielder" : "forcing a replenish to verify the writes")}");
            if (_locateAllBuf.Count > 0)
            {
                long pe = _locateAllBuf[0];   // move 0. Not a MoveWatch fire
                ReplenishAll(_locateAllBuf, 0, _mem.U8(pe + Offsets.AGx), _mem.U8(pe + Offsets.AGy));
            }
            else Wielder.DumpCandidates(_mem, _hands, fp);   // names the rejecting predicate
        }

        _locateAllBuf.Clear();
        Wielder.LocateAll(_mem, UmbralId, _hands, fp, _locateAllBuf);
        bool nowLocated = _locateAllBuf.Count > 0;
        if (nowLocated != _wasLocated)
        {
            _wasLocated = nowLocated;
            ModLogger.Debug(LogVerb.Signature, $"font wielder {(_wasLocated ? "found; tracking its position" : "not found this tick; skipping")}");
        }
        if (_locateAllBuf.Count == 0) return;   // MoveWatch keeps its baseline

        long e = _locateAllBuf[0];   // first entry; live and frozen copies share the tile
        int gx = _mem.U8(e + Offsets.AGx), gy = _mem.U8(e + Offsets.AGy);
        bool fire = _watch.Observe(gx, gy);
        if (_watch.IsFresh) ModLogger.Debug(LogVerb.Signature, $"font is watching the wielder's position for movement (currently at tile ({gx},{gy}))");
        if (!fire) return;

        _moveCount++;
        ReplenishAll(_locateAllBuf, _moveCount, gx, gy);
    }

    /// <summary>Restores HP and MP on every located twin; amounts come from the first entry and the
    /// MP read-back checks only that one. HP is clamped and never revives, MP needs a living
    /// wielder and a passed layout check (MpHalfAllowed). Logs one console line per replenish.</summary>
    private void ReplenishAll(List<long> entries, int move, int gx, int gy)
    {
        long first = entries[0];
        int hp = _mem.U16(first + Offsets.AHp), maxHp = _mem.U16(first + Offsets.AMaxHp);
        int newHp = BandHeal.NewHp(hp, maxHp, BandHeal.HealAmount(maxHp, Tuning.FontHpPct));
        foreach (long ent in entries) if (newHp != hp) BandHeal.WriteHp(_mem, ent, newHp);
        int mpRestored = 0;
        if (MpHalfAllowed(hp, _mpOk))   // no MP write when the layout is unchecked or the wielder is dead
        {
            int mp = _mem.U16(first + Offsets.AMp), maxMp = _mem.U16(first + Offsets.AMaxMp);
            int newMp = NewMp(mp, maxMp, BandHeal.HealAmount(maxMp, Tuning.FontMpPct));
            if (newMp == mp) ModLogger.Debug(LogVerb.Signature, $"font MP already full ({mp}/{maxMp}); no MP restore needed");
            else
            {
                foreach (long ent in entries) WriteMp(_mem, ent, newMp);
                mpRestored = newMp - mp;
                bool landed = _mem.U16(first + Offsets.AMp) == newMp;   // read-back on first entry only
                if (!landed) ModLogger.Warn(LogVerb.Signature, "The MP restore write did not stick.");
            }
        }
        ModLogger.EventWithTrace(LogVerb.Signature,
            $"The wielder moved (move {move}) and was replenished {newHp - hp} HP and {mpRestored} MP (HP {hp} to {newHp} of {maxHp}).",
            $"font move detail (tile ({gx},{gy}), {entries.Count} copies updated)");
    }

    /// <summary>Samples mp/maxMp of every valid band unit and latches the MpLayoutOk verdict once
    /// 2 or more units are visible. On failure MP writes stay off for the battle; HP is unaffected.</summary>
    private void ValidateMpLayout()
    {
        var samples = new List<(int mp, int maxMp)>();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long ent = Band.Entry(s);
            if (!Band.IsValid(_mem, ent)) continue;
            samples.Add((_mem.U16(ent + Offsets.AMp), _mem.U16(ent + Offsets.AMaxMp)));
        }
        if (samples.Count < 2) return;   // band not populated yet; retry next tick
        _mpChecked = true;
        _mpOk = MpLayoutOk(samples);
        if (_mpOk) ModLogger.Event(LogVerb.Signature, $"MP restore is confirmed for this battle (checked {samples.Count} units); HP and MP will both be restored.");
        else ModLogger.Warn(LogVerb.Signature, "MP restore could not be confirmed this battle; only HP will be restored.");
    }
}
