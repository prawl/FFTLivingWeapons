using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Venombolt's "Plague" signature: while a +3 Venombolt is equipped and the wielder's action
/// poisons an enemy (poison bit edges false->true during the acted window), that enemy's poison
/// NEVER expires and ticks harder. Mechanism PROVEN live (memory poison-status-bytes): holding
/// bit +0x48/0x80 survived a two-healer battle at 1.75x rate.
///
/// DETECTION: per-band-slot poison baseline is updated EVERY tick (so pre-existing poison is
/// excluded -- a unit first seen poisoned has no edge). A victim latches when its poison-bit
/// RISING EDGE and the wielder's acted window land within Tuning.PlagueGraceMs of each other,
/// in either order: the engine applies poison during attack resolution, which can precede the
/// observed window or trail it, so exact overlap missed real procs live (2026-06-10).
/// Enemies identified by static-array fingerprint.
/// HOLD: each tick re-OR the poison bit and re-pin the timer to 36 whenever it reads below
/// init -- defeats both natural expiry and healer cures. Fingerprint mismatch drops the latch.
/// AUGMENT: on each victim turn edge (CT >= 90 then &lt; 70, reading band +0x09), apply
/// mhp*3/32 extra damage to +0x14 as a single 2-byte write, floored at 1 (the augment
/// never kills; engine owns lethal).
/// RELEASE: ResetBattle clears all latches; roster no longer holds 80 drops all latches;
/// fingerprint mismatch on read drops that specific latch.
/// LIVE GATE: when !inLive, no writes and no new latching (latches persist in memory;
/// ResetBattle still clears them at the debounced exit).
/// All reads/writes VirtualQuery-guarded.
/// </summary>
internal sealed partial class Plague : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField, ctx.InLive);
    private const int VenomboltId = 80;

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly PlagueState _state;
    private readonly PlagueBaseline _baseline;
    private readonly System.Func<long> _nowMs;
    private long _lastActiveMs = PlagueBaseline.NoEdge;   // when the acted window was last open
    private bool _wasActive;

    public Plague(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker,
                  System.Func<long>? nowMs = null, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
        _state = new PlagueState();
        _baseline = new PlagueBaseline();
        _nowMs = nowMs ?? (() => System.Environment.TickCount64);
    }

    public void ResetBattle()
    {
        _state.Clear();
        _baseline.ClearAll();
        _lastActiveMs = PlagueBaseline.NoEdge;
        _wasActive = false;
    }

    public void Tick(bool onField, bool inLive)
    {
        if (!onField) { Drive(inLive); return; }
        if (!_meta.TryGetValue(VenomboltId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierOf(_kills, VenomboltId);
        bool active = Signatures.Earned(m.Signature, tier) && IsEquipped()
                      && Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, VenomboltId)
                      && _mem.U8(Offsets.Acted) == 1;
        long now = _nowMs();
        if (active) _lastActiveMs = now;
        bool windowRecent = WithinGrace(_lastActiveMs, now, Tuning.PlagueGraceMs);
        if (active != _wasActive)
        {
            _wasActive = active;
            ModLogger.Log($"plague: {(active ? "window open -- poison landing on the wielder's target will never fade" : "window closed")}");
        }

        // Release all latches immediately when Venombolt is unequipped (A4 unequip release).
        if (inLive && !IsEquipped()) { _state.Clear(); }

        // Fingerprints are needed for as long as a grace-window latch is still possible.
        var enemyFps = windowRecent ? EnemyFingerprints() : null;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!_mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
            int mhp = _mem.U16(addr + Offsets.AMaxHp), lvl = _mem.U8(addr + Offsets.ALevel);
            if (mhp < 1 || mhp > 2000 || lvl < 1 || lvl > 99) continue;
            int br = _mem.U8(addr + Offsets.ABrave), fa = _mem.U8(addr + Offsets.AFaith);
            if (br < 1 || br > 100 || fa < 1 || fa > 100) continue;

            var fp = (mhp, lvl, br, fa);
            bool poisoned = _mem.Readable(addr + Offsets.APoison, 1)
                            && (_mem.U8(addr + Offsets.APoison) & Offsets.APoisonBit) != 0;

            // A1: update baseline EVERY tick, BEFORE the latch decision (stamps edge times).
            _baseline.Update(addr, fp, poisoned, now);

            if (_state.IsHeldAt(addr))
            {
                // Verify the slot still holds the same unit; drop on fingerprint mismatch.
                if (!_state.FpAt(addr).Equals(fp)) { _state.ReleaseAt(addr); continue; }

                int ct = _mem.Readable(addr + Offsets.ACtTurn, 1) ? _mem.U8(addr + Offsets.ACtTurn) : 0;
                if (CtTurns.IsTurn(_state.LastCtAt(addr), ct)) ApplyAugment(_mem, addr, fp);
                _state.UpdateCtAt(addr, ct);
            }

            if (!inLive || enemyFps is null) continue;
            bool enemy = enemyFps.Contains(fp);

            // Latch when the poison edge and the acted window overlap within the grace,
            // in either order (the engine sets the bit during attack resolution, which can
            // precede or trail the observed window).
            if (ShouldLatchNow(enemy, _state.IsHeldAt(addr), _baseline.LastEdgeMs(addr, fp),
                               _lastActiveMs, now, Tuning.PlagueGraceMs))
            {
                int seedCt = _mem.Readable(addr + Offsets.ACtTurn, 1) ? _mem.U8(addr + Offsets.ACtTurn) : 0;
                _state.Latch(addr, fp, seedCt);
                ModLogger.Log($"plague: latched enemy ({mhp} max HP, lv {lvl}) -- poison will never fade and ticks harder");
            }
        }

        Drive(inLive);
    }

    /// <summary>Hold all latched victims each tick: re-OR poison bit, re-pin timer.
    /// When <paramref name="inLive"/> is false, skips writes (A3).</summary>
    private void Drive(bool inLive)
    {
        var toDrop = new List<long>();
        foreach (long addr in _state.HeldAddrs)
        {
            var fp = _state.FpAt(addr);
            DriveOne(_mem, addr, fp, _state, inLive);
            // Drop the latch if the fingerprint no longer matches (unit left / battle reset).
            if (!_mem.Readable(addr + Offsets.AMaxHp, 2)
                || _mem.U16(addr + Offsets.AMaxHp) != fp.mhp
                || _mem.U8(addr + Offsets.ALevel)  != fp.lvl
                || _mem.U8(addr + Offsets.ABrave)  != fp.br
                || _mem.U8(addr + Offsets.AFaith)  != fp.fa)
                toDrop.Add(addr);
        }
        foreach (long addr in toDrop) _state.ReleaseAt(addr);
    }

    /// <summary>Enemy fingerprints from the static array (mirrors Maim / EagleEye).
    /// NOT Band.EnemyFingerprints: this scan's mhp bound (IsValidEnemyMhp, &lt;= 1999) is
    /// deliberately quarantined from the inclusive-2000 bound the other modules share.</summary>
    private System.Collections.Generic.HashSet<(int mhp, int lvl, int br, int fa)> EnemyFingerprints()
    {
        var set = new System.Collections.Generic.HashSet<(int, int, int, int)>();
        for (int s = 0; s <= Offsets.EnemySlotMax; s++)
        {
            long slot = Offsets.ArrayReadBase + (long)s * Offsets.ArrayStride;
            if (!_mem.Readable(slot + Offsets.AMaxHp, 2)) continue;
            int mhp = _mem.U16(slot + Offsets.AMaxHp), lvl = _mem.U8(slot + Offsets.ALevel);
            int br = _mem.U8(slot + Offsets.ABrave), fa = _mem.U8(slot + Offsets.AFaith);
            if (!IsValidEnemyMhp(mhp) || lvl < 1 || lvl > 99 || br > 100 || fa > 100) continue;
            set.Add((mhp, lvl, br, fa));
        }
        return set;
    }

    /// <summary>True when any roster slot holds Venombolt (id 80) in either hand.</summary>
    private bool IsEquipped()
    {
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            if (!_mem.Readable(rb + Offsets.RNameId, 2)) continue;
            if (_mem.U16(rb + Offsets.RRHand) == VenomboltId || _mem.U16(rb + Offsets.ROffHand) == VenomboltId)
                return true;
        }
        return false;
    }
}
