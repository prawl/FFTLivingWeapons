using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Staff of the Magi's "Sanctuary" signature: while a +3 Staff of the Magi is held in the MAIN
/// HAND and its bearer is alive and on the field, every fallen ALLY's crystal counter (combat
/// base +0x07, band entry -0x15) is held at Tuning.SanctuaryHearts (3) each tick. The engine
/// steps this counter 3->2->1->0 once per the dead unit's scheduled turn; at 0 the unit
/// crystallizes (permanent loss). Sanctuary re-writes it to 3 every tick so the revive window
/// never closes. If the bearer dies, Sanctuary lifts -- the engine resumes counting.
///
/// PER-TICK PIN, NOT TURN-EDGE: unlike Renewal or Wyrmblood, this fires every tick (no
/// TurnTracker). The pin is idempotent; when the counter is already 3 no log fires (low spam).
///
/// BEARER-ALIVE GATE ("save the priest"): Wielder.Locate is called each tick to find the bearer.
/// If the bearer's HP == 0, <c>active</c> goes false and no writes land. The effect is restored
/// the tick the bearer's HP rises above 0 (a raise lands), with no per-battle state to clear.
///
/// DEAD-STREAK GUARD: before the counter of a fallen ally is first pinned, that ally must have
/// read fallen (HP==0 or Dead bit set) for DeadNeeded (3) consecutive ticks. A phantom band-load
/// transient can make a LIVE unit read HP==0 for one tick; without the guard, Sanctuary would
/// write 3 to the crystal counter of a unit that was never actually dead. The streak is per slot
/// and resets to 0 whenever the slot reads alive or invalid.
///
/// SILENT AND ENGINE-RENDERED: the DLL only holds the byte. The "3 hearts" animation that the
/// engine normally drains is visibly paused -- no floating numbers, no status icons. The effect
/// is natural within FFT's existing UI.
///
/// ALL WRITES ARE GUARDED: every W8 is preceded by Writable(addr, 1). An unwritable counter
/// address is a silent no-op -- never a raw pointer deref.
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

        if (active != _wasActive)
        {
            _wasActive = active;
            ModLogger.Log(active
                ? "sanctuary ACTIVE -- Staff of the Magi at +3 and its bearer lives; fallen allies are held from crystallizing"
                : "sanctuary inactive -- the bearer is down or unequipped; divine intervention lifted");
        }

        // Ally fingerprints: computed once per tick (only when active, to avoid static-array
        // sweep cost while inactive). Inactive ticks still maintain _deadStreak so the guard
        // stays fresh across brief bearer-flicker intervals where active flips false then back.
        var allyFps = active ? Band.AllyFingerprints(_mem) : null;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) { _deadStreak[s] = 0; continue; }

            int hp = _mem.U16(e + Offsets.AHp);
            bool deadBit = (_mem.U8(e + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0;
            bool fallen = hp == 0 || deadBit;

            if (!fallen) { _deadStreak[s] = 0; continue; }

            // Increment streak, capped at DeadNeeded to prevent overflow on very long corpse spans.
            if (_deadStreak[s] < DeadNeeded) _deadStreak[s]++;

            if (!active) continue;                         // streak accumulates but no write while inactive
            if (_deadStreak[s] < DeadNeeded) continue;    // phantom-load guard not yet satisfied

            // Ally filter: enemies are never protected (positive fingerprint match only).
            int mhp = _mem.U16(e + Offsets.AMaxHp);
            int lvl = _mem.U8(e + Offsets.ALevel);
            int br  = _mem.U8(e + Offsets.ABrave);
            int fa  = _mem.U8(e + Offsets.AFaith);
            if (allyFps is null || !allyFps.Contains((mhp, lvl, br, fa))) continue;

            long counterAddr = e + Offsets.ACrystalHearts;
            int cur = _mem.U8(counterAddr);
            if (cur != Tuning.SanctuaryHearts)
            {
                int gx = _mem.U8(e + Offsets.AGx);
                int gy = _mem.U8(e + Offsets.AGy);
                ModLogger.Log($"sanctuary: divine intervention -- held ally at ({gx},{gy}) at {cur}->{Tuning.SanctuaryHearts} hearts");
            }
            if (_mem.Writable(counterAddr, 1)) _mem.W8(counterAddr, Tuning.SanctuaryHearts);
        }
    }
}
