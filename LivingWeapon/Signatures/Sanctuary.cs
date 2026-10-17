using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Staff of the Magi's Sanctuary signature. The engine steps a fallen unit's crystal counter
/// (combat base +0x07, band entry -0x15) 3->2->1->0 and crystallizes it at 0. While a +3 Staff of the
/// Magi is in the main hand and its bearer is alive on the field, this holds every fallen ally's counter
/// at Tuning.SanctuaryHearts (3) each tick. If the bearer's HP is 0 nothing is written. An ally must read
/// fallen for DeadNeeded (3) consecutive ticks first, because a phantom band-load can read a live unit as
/// HP 0 for one tick. Unlike Renewal this pins every tick (no TurnTracker); writes are guarded by Writable.
/// </summary>
internal sealed partial class Sanctuary : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);
    private const int MagiStaffId = 66;
    private const int DeadNeeded = 3;   // consecutive fallen ticks before the counter pin begins (phantom-load guard)

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly List<int> _hands = new();
    private bool _wasActive;
    private readonly int[] _deadStreak = new int[Offsets.BandSlots];

    public Sanctuary(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
    }

    public void ResetBattle()
    {
        _wasActive = false;
        Array.Clear(_deadStreak, 0, _deadStreak.Length);
    }

    public void Tick(bool onField)
    {
        if (!onField) return;
        if (!_meta.TryGetValue(MagiStaffId, out var m) || m.Signature is null) return;

        int tier = Tuning.TierOf(_kills, MagiStaffId);

        // Resolve the single roster slot holding id 66 in main hand; ambiguity (two wielders) = inactive.
        bool resolved = Wielder.TryResolveMainHand(_mem, MagiStaffId, out var fp, _hands);
        long bearer = resolved ? Wielder.Locate(_mem, MagiStaffId, _hands, fp) : 0;
        bool bearerAlive = bearer != 0 && _mem.U16(bearer + Offsets.AHp) > 0;
        bool active = IsActive(m.Signature, tier) && bearerAlive;

        if (ActivationEdge.Step(ref _wasActive, active))
        {
            ModLogger.Event(LogVerb.Signature, active
                ? "Staff of the Magi at tier three is armed and its bearer lives; fallen allies are held from crystallizing."
                : "The Staff of the Magi's bearer is down or unequipped; divine intervention ends.");
        }

        // Ally fingerprints are computed only when active, to skip the static-array sweep. Inactive ticks still
        // update _deadStreak so it stays fresh across brief bearer flicker.
        var allyFps = active ? Band.AllyFingerprints(_mem) : null;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) { _deadStreak[s] = 0; continue; }

            int hp = _mem.U16(e + Offsets.AHp);
            bool deadBit = (_mem.U8(e + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0;
            bool fallen = hp == 0 || deadBit;

            if (!fallen) { _deadStreak[s] = 0; continue; }

            // Streak capped at DeadNeeded to avoid overflow.
            if (_deadStreak[s] < DeadNeeded) _deadStreak[s]++;

            if (!active) continue;                         // streak accumulates but no write while inactive
            if (_deadStreak[s] < DeadNeeded) continue;    // phantom-load guard not yet satisfied

            // Allies only (positive fingerprint match).
            int mhp = _mem.U16(e + Offsets.AMaxHp);
            int lvl = _mem.U8(e + Offsets.ALevel);
            int br = _mem.U8(e + Offsets.ABrave);
            int fa = _mem.U8(e + Offsets.AFaith);
            if (allyFps is null || !allyFps.Contains((mhp, lvl, br, fa))) continue;

            long counterAddr = e + Offsets.ACrystalHearts;
            int cur = _mem.U8(counterAddr);
            if (cur != Tuning.SanctuaryHearts)
            {
                int gx = _mem.U8(e + Offsets.AGx);
                int gy = _mem.U8(e + Offsets.AGy);
                ModLogger.Event(LogVerb.Signature, $"Divine intervention holds the fallen ally at tile ({gx},{gy}) at {Tuning.SanctuaryHearts} hearts (the counter had fallen to {cur}).");
            }
            if (_mem.Writable(counterAddr, 1)) _mem.W8(counterAddr, Tuning.SanctuaryHearts);
        }
    }
}
