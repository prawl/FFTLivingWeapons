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
/// EXPIRY: count the victim's turns off its CT (+0x25 = band ACtSlam, CharmLock's proven pattern),
/// read from the stored HeldAddr each tick in Drive (never from a band scan -- avoids CT thrash when
/// a frozen twin shares the fingerprint); after crippleTurns victim-turns the saved bytes restore.
/// BATTLE EXIT: restore all latches and clear (mirrors CharmLock / Ricochet).
/// All reads/writes are VirtualQuery-guarded.
/// </summary>
internal sealed partial class Maim : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);
    private const int HuntressId = 89;
    // The victim's LIVE scheduler charge-time, band-entry-relative. Counting a unit's completed turns
    // reads THIS byte (== Offsets.ACtSlam, == combat base+0x41 -- the one CharmLock reads cleanly for
    // enemy turns). NOT band +0x09 (Offsets.ACtTurn): a live probe (2026-06-17) proved +0x09 stays
    // flat 0 for enemies and never crosses the turn threshold, so the old read never counted a turn
    // and the latch never expired (the maim-never-unlatched bug).
    private const int LiveCtOff = 0x25;

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly MaimState _state;
    private readonly RicochetState _hpState;   // HP-diff tracking (same pattern as Ricochet)
    private bool _wasActive;

    public Maim(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker,
                IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
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
        if (!_meta.TryGetValue(HuntressId, out var m) || m.Signature is null) { Drive(); return; }
        int tier = Tuning.TierOf(_kills, HuntressId);
        // Latching a NEW victim is a your-turn (on-field) event -- the wielder must be the acting unit.
        // But counting a held victim's turns, holding its reaction to zero, and expiring the latch all
        // run EVERY in-battle tick: an enemy's CT crosses 90->below-70 during ITS OWN turn, which is an
        // enemy-turn (off-field) frame. The old `if (!onField) return` gate never sampled that edge,
        // so the latch never expired (proven live 2026-06-17 -- a maim outlasted 3+ victim turns).
        bool active = onField && IsActive(m.Signature, tier)
                      && Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, HuntressId)
                      && _mem.U8(Offsets.Acted) == 1;
        if (active != _wasActive)
        {
            _wasActive = active;
            ModLogger.Debug(LogVerb.Signature, $"maim window {(active ? "armed for this action; the next hit suppresses enemy reactions" : "closed")}");
        }

        int crippleTurns = m.Signature.CrippleTurns;

        // Scan band: observe HP diffs and latch new victims. Runs ONLY on-field: the wielder's hit
        // lands during the on-field attack animation, so Observe must baseline and detect the drop
        // in the same on-field window. Running Observe off-field would eat the HP delta during the
        // animation, leaving nothing to detect when the acted window reopens.
        if (onField)
        {
            var enemyFps = active ? Band.EnemyFingerprints(_mem) : null;
            for (int s = 0; s < Offsets.BandSlots; s++)
            {
                long addr = Band.Entry(s);
                if (!_mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
                int mhp = _mem.U16(addr + Offsets.AMaxHp), lvl = _mem.U8(addr + Offsets.ALevel);
                if (mhp < 1 || mhp >= 2000 || lvl < 1 || lvl > 99) continue;
                int br = _mem.U8(addr + Offsets.ABrave), fa = _mem.U8(addr + Offsets.AFaith);
                if (br < 1 || br > 100 || fa < 1 || fa > 100) continue;
                int hp = _mem.Readable(addr + Offsets.AHp, 2) ? _mem.U16(addr + Offsets.AHp) : 0;

                int dmg = _hpState.Observe(s, hp);

                if (!active || dmg <= 0 || enemyFps is null) continue;
                var fp = (mhp, lvl, br, fa);
                bool enemy = enemyFps.Contains(fp);
                if (!ShouldLatch(enemy)) continue;

                if (!_state.IsHeld(fp))
                {
                    // First hit: read the LIVE reaction before we zero it, and seed the turn counter with
                    // the victim's CURRENT CT so the first sample is never mistaken for a completed turn.
                    uint saved = ReadReactionField(_mem, addr);
                    _state.Latch(addr, fp, saved, _mem.U8(addr + LiveCtOff));
                    ModLogger.EventWithTrace(LogVerb.Signature,
                        $"The struck enemy ({mhp} maximum HP) loses its reaction abilities for {crippleTurns} of its turns.",
                        $"maim latch detail (saved reaction bits=0x{saved:X8})");
                }
                else
                {
                    // Re-hit: refresh the window (reset turn counter), keep saved bytes intact.
                    _state.Refresh(fp);
                    ModLogger.Event(LogVerb.Signature, $"An already-maimed enemy ({mhp} maximum HP) was hit again; its suppression window restarts.");
                }
            }
        }

        Drive();
        ExpireAll(crippleTurns);
    }

    /// <summary>Hold all active latches to zero each tick (beats engine re-assertion). Also counts
    /// each held victim's turns off its own CT at the stored address (read from HeldAddr, never from
    /// a band scan, so a duplicate/twin band entry with a different CT cannot thrash the count).</summary>
    private void Drive()
    {
        foreach (var fp in _state.Held)
        {
            long addr = _state.HeldAddr(fp);
            if (!_mem.Readable(addr + Offsets.AMaxHp, 2)) continue;   // copy moved/freed
            // Verify it's still the same unit (fingerprint match).
            if (_mem.U16(addr + Offsets.AMaxHp) != fp.mhp || _mem.U8(addr + Offsets.ALevel) != fp.lvl
                || _mem.U8(addr + Offsets.ABrave) != fp.br || _mem.U8(addr + Offsets.AFaith) != fp.fa) continue;
            // Count the victim's own turns off its live charge-time at the stored address. Reading
            // from HeldAddr (not a slot scan) means a frozen twin at a different address cannot
            // thrash LastCt and produce spurious turn counts.
            int ct = _mem.U8(addr + LiveCtOff);
            if (CtTurns.IsTurn(_state.LastCt(fp), ct)) _state.CountTurn(fp);
            _state.UpdateCt(fp, ct);
            HoldZero(_mem, addr);
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
            Restore(_mem, addr, saved);
            _state.Release(fp);
            ModLogger.EventWithTrace(LogVerb.Signature,
                $"Suppression ended on the enemy ({fp.mhp} maximum HP) after {crippleTurns} of its turns; its reaction abilities are restored.",
                $"maim release detail (restored reaction bits=0x{saved:X8})");
        }
    }

    /// <summary>Restore all held victims unconditionally (battle exit).</summary>
    private void RestoreAll()
    {
        foreach (var fp in _state.Held)
        {
            uint saved = _state.SavedReaction(fp).GetValueOrDefault();
            Restore(_mem, _state.HeldAddr(fp), saved);
        }
    }

}
