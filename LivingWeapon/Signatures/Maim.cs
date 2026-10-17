using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Huntress's "Maim" signature. While a +3 Huntress is equipped and the wielder's action damages
/// an enemy, that enemy loses its reaction abilities (combat +0x94, band +0x78, 4 bytes held to
/// zero) for 3 of its turns, then the saved bits restore. Hold-zero at +0x94 suppressed Counter
/// through 5 hits and a one-shot restore brought it back (memory reaction-suppression-cripple).
/// A re-hit refreshes the window; allies are never latched.
/// Victims are found by per-tick HP drops on enemy band slots during the wielder's acted period
/// (as in Ricochet), enemy side by static-array fingerprint. The reaction field is saved once on
/// first hit, since re-reading it while held would save zeros. Expiry counts the victim's turns off
/// its CT (+0x25 = band ACtSlam) at the stored HeldAddr; battle exit restores all latches.
/// Reads and writes are VirtualQuery-guarded.
/// </summary>
internal sealed partial class Maim : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);
    private const int HuntressId = 89;
    // Victim's live scheduler CT, band-entry-relative (== Offsets.ACtSlam == combat base+0x41).
    // Not band +0x09 (Offsets.ACtTurn): a 2026-06-17 probe showed it stays 0 for enemies, so the
    // latch never expired (the maim-never-unlatched bug).
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
        // Latching a new victim needs the wielder on field, but counting a held victim's turns,
        // holding its reaction at zero and expiring the latch run every in-battle tick. An enemy's
        // CT crosses 90 to below 70 during its own off-field turn (a maim outlasted 3+ victim turns,
        // 2026-06-17).
        bool active = onField && IsActive(m.Signature, tier)
                      && Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, HuntressId)
                      && _mem.U8(Offsets.Acted) == 1;
        if (ActivationEdge.Step(ref _wasActive, active))
        {
            ModLogger.Debug(LogVerb.Signature, $"maim window {(active ? "armed for this action; the next hit suppresses enemy reactions" : "closed")}");
        }

        int crippleTurns = m.Signature.CrippleTurns;

        // Observe HP diffs and latch new victims, on-field only. The hit lands during the on-field
        // attack animation, so baseline and detection must share that window.
        if (onField)
        {
            var enemyFps = active ? Band.EnemyFingerprints(_mem) : null;
            for (int s = 0; s < Offsets.BandSlots; s++)
            {
                // Shared sanity read/bounds extracted to Band.TryReadUnit (Band.Sanity.cs).
                if (!Band.TryReadUnit(_mem, s, out long addr, out var fp, out int hp)) continue;

                int dmg = _hpState.Observe(s, hp);

                if (!active || dmg <= 0 || enemyFps is null) continue;
                bool enemy = enemyFps.Contains(fp);
                if (!ShouldLatch(enemy)) continue;

                if (!_state.IsHeld(fp))
                {
                    // First hit. Save the live reaction before zeroing it and seed the turn counter
                    // with the victim's current CT. A failed read refuses the latch, since saving a
                    // fake 0 would later wipe a real reaction. There is nothing to retry because the
                    // HP-diff event is spent (Kobu's rearm-read refusal rolls back for a retry).
                    if (!TryReadReactionField(_mem, addr, out uint saved)) continue;
                    _state.Latch(addr, fp, saved, _mem.U8(addr + LiveCtOff));
                    ModLogger.EventWithTrace(LogVerb.Signature,
                        $"The struck enemy ({fp.mhp} maximum HP) loses its reaction abilities for {crippleTurns} of its turns.",
                        $"maim latch detail (saved reaction bits=0x{saved:X8})");
                }
                else
                {
                    // Re-hit. Refresh the window (reset turn counter), keep saved bytes intact.
                    _state.Refresh(fp);
                    ModLogger.Event(LogVerb.Signature, $"An already-maimed enemy ({fp.mhp} maximum HP) was hit again; its suppression window restarts.");
                }
            }
        }

        Drive();
        ExpireAll(crippleTurns);
    }

    /// <summary>Hold all latches at zero each tick. Also counts each held victim's turns off its CT
    /// at HeldAddr (not a band scan), so a twin band entry with a different CT cannot thrash the
    /// count.</summary>
    private void Drive()
    {
        foreach (var fp in _state.Held)
        {
            long addr = _state.HeldAddr(fp);
            // Copy moved or freed, or another unit sits at the held address (same-unit
            // write-safety check).
            if (!Band.SameUnitAtExact(_mem, addr, fp)) continue;
            // Count turns from HeldAddr, not a slot scan, so a frozen twin at another address
            // cannot thrash LastCt.
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
