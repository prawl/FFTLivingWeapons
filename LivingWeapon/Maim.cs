using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Huntress's "Maim" signature: while a +3 Huntress is equipped and the wielder's action damages
/// an ENEMY, that enemy loses its reaction abilities (combat +0x94, band +0x78, 4 bytes held to
/// zero) for 3 of its turns, then the saved bits restore. PROVEN primitives (memory
/// reaction-suppression-cripple): hold-zero at +0x94 suppressed Counter through 5 hits; one-shot
/// restore brought it back. Re-hit refreshes the window. Allies are never latched.
///
/// DETECTION: mirrors Ricochet's HP-diff victim pattern -- per-tick HP drops on enemy band slots
/// during the +3 wielder's acted period. Enemy-side filter = static-array fingerprints.
/// LATCH: on first hit, read and save the 4-byte reaction field ONCE (never re-save while held --
/// the field is zeroed while held; re-reading it would restore zeros, losing the reaction).
/// HOLD: zero the field each tick while the latch is live.
/// EXPIRY: count the victim's turns off its CT (+0x25 = band +0x09, CharmLock's proven pattern);
/// after crippleTurns victim-turns the saved bytes are written back and the latch released.
/// BATTLE EXIT: restore all latches and clear (mirrors CharmLock / Ricochet).
/// All reads/writes are VirtualQuery-guarded.
/// </summary>
internal sealed partial class Maim
{
    private const int HuntressId = 89;
    private const int CtOff = 0x25;          // combat-struct CT; band-relative = 0x25 - 0x1C = 0x09
    private const int BandCtOff = 0x09;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly MaimState _state;
    private readonly RicochetState _hpState;   // HP-diff tracking (same pattern as Ricochet)
    private bool _wasActive;

    public Maim(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker)
    {
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
        _state = new MaimState();
        _hpState = new RicochetState(Offsets.BandSlots);
    }

    public void ResetBattle()
    {
        RestoreAll();   // restore any held reaction bytes before clearing
        _state.Clear();
        _wasActive = false;
        _hpState.ResetBattle();
    }

    public void Tick(bool onField)
    {
        if (!onField) { Drive(); return; }   // Drive holds zeros even when not on the active field
        if (!_meta.TryGetValue(HuntressId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierFor(_kills.TryGetValue(HuntressId, out int k) ? k : 0);
        bool active = IsActive(m.Signature, tier) && _tracker._lastPlayerWeapons.Contains(HuntressId)
                      && Mem.U8(Offsets.Acted) == 1;
        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info($"maim {(active ? "ACTIVE (Huntress acting)" : "inactive")}");
        }

        int crippleTurns = m.Signature.CrippleTurns;
        var enemyFps = active ? EnemyFingerprints() : null;

        // Scan band: observe HP diffs + update CT for held victims.
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
            int mhp = Mem.U16(addr + Offsets.AMaxHp), lvl = Mem.U8(addr + Offsets.ALevel);
            if (mhp < 1 || mhp >= 2000 || lvl < 1 || lvl > 99) continue;
            int br = Mem.U8(addr + Offsets.ABrave), fa = Mem.U8(addr + Offsets.AFaith);
            if (br < 1 || br > 100 || fa < 1 || fa > 100) continue;
            int hp = Mem.Readable(addr + Offsets.AHp, 2) ? Mem.U16(addr + Offsets.AHp) : 0;

            int dmg = _hpState.Observe(s, hp);

            // Update CT for held victims to count their turns.
            var fp = (mhp, lvl, br, fa);
            if (_state.IsHeld(fp))
            {
                int ct = Mem.U8(addr + BandCtOff);
                if (Maim.IsTurn(_state.LastCt(fp), ct)) _state.CountTurn(fp);
                _state.UpdateCt(fp, ct);
            }

            if (!active || dmg <= 0 || enemyFps is null) continue;
            bool enemy = enemyFps.Contains((mhp, lvl, br, fa));
            if (!ShouldLatch(enemy)) continue;

            if (!_state.IsHeld(fp))
            {
                // First hit: read the LIVE reaction before we zero it.
                uint saved = ReadReactionField(addr);
                _state.Latch(addr, fp, saved);
                Log.Info($"maim: latched slot {s} mhp {mhp} saved reaction=0x{saved:X8}");
            }
            else
            {
                // Re-hit: refresh the window (reset turn counter), keep saved bytes intact.
                _state.Refresh(fp);
                Log.Info($"maim: refreshed slot {s} mhp {mhp}");
            }
        }

        Drive();
        ExpireAll(crippleTurns);
    }

    /// <summary>Hold all active latches to zero each tick (beats engine re-assertion).</summary>
    private void Drive()
    {
        foreach (var fp in _state.Held)
        {
            long addr = _state.HeldAddr(fp);
            if (!Mem.Readable(addr + Offsets.AMaxHp, 2)) continue;   // copy moved/freed
            // Verify it's still the same unit (fingerprint match).
            if (Mem.U16(addr + Offsets.AMaxHp) != fp.mhp || Mem.U8(addr + Offsets.ALevel) != fp.lvl
                || Mem.U8(addr + Offsets.ABrave) != fp.br || Mem.U8(addr + Offsets.AFaith) != fp.fa) continue;
            HoldZero(addr);
        }
    }

    /// <summary>Check each held victim for expiry; restore + release the expired ones.</summary>
    private void ExpireAll(int crippleTurns)
    {
        var toRelease = new System.Collections.Generic.List<(int mhp, int lvl, int br, int fa)>();
        foreach (var fp in _state.Held)
            if (_state.IsExpired(fp, crippleTurns)) toRelease.Add(fp);
        foreach (var fp in toRelease)
        {
            long addr = _state.HeldAddr(fp);
            uint saved = _state.SavedReaction(fp).GetValueOrDefault();
            Restore(addr, saved);
            _state.Release(fp);
            Log.Info($"maim: expired mhp {fp.mhp} after {crippleTurns} turns; restored reaction=0x{saved:X8}");
        }
    }

    /// <summary>Restore all held victims unconditionally (battle exit).</summary>
    private void RestoreAll()
    {
        foreach (var fp in _state.Held)
        {
            uint saved = _state.SavedReaction(fp).GetValueOrDefault();
            Restore(_state.HeldAddr(fp), saved);
        }
    }

    /// <summary>Enemy fingerprints from the static array (mirrors EagleEye / Ricochet).</summary>
    private System.Collections.Generic.HashSet<(int mhp, int lvl, int br, int fa)> EnemyFingerprints()
    {
        var set = new System.Collections.Generic.HashSet<(int, int, int, int)>();
        for (int s = 0; s <= Offsets.EnemySlotMax; s++)
        {
            long slot = Offsets.ArrayReadBase + (long)s * Offsets.ArrayStride;
            if (!Mem.Readable(slot + Offsets.AMaxHp, 2)) continue;
            int mhp = Mem.U16(slot + Offsets.AMaxHp), lvl = Mem.U8(slot + Offsets.ALevel);
            int br = Mem.U8(slot + Offsets.ABrave), fa = Mem.U8(slot + Offsets.AFaith);
            if (mhp < 1 || mhp > 2000 || lvl < 1 || lvl > 99 || br > 100 || fa > 100) continue;
            set.Add((mhp, lvl, br, fa));
        }
        return set;
    }
}
