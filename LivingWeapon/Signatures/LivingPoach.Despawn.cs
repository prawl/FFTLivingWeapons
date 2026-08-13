using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-167 -- the corpse-despawn RETRY lifecycle. Owner design decision (live pass, 2026-08-12): a
/// vanilla-poached corpse yields NEITHER crystal nor chest, so a TRANSIENT despawn refusal
/// (CorpseDespawn.cs's Transient bucket -- the corpse is still confirmed dead and ours; only the
/// current-actor/open-turn guard or the render-node resolve stood in the way) must never be left
/// standing as a one-shot miss the way LW-167 stage 3 shipped it: a corpse stranded that way can go
/// on to crystallize on its own scheduled turn, handing out BOTH the carcass (already in the store)
/// and a crystal -- more than vanilla ever gives for a single kill.
///
/// So a transient refusal is queued here (per-battle, keyed by band slot -- the same slot only
/// ever holds one corpse's despawn attempt at a time) and Engine.Tick retries it every tick
/// (Engine.cs's in-battle branch, beside the other per-tick subsystem calls -- LivingPoach is not
/// an ISignature) until CorpseDespawn.TryDespawn finally: succeeds: reads Permanent (no longer
/// ours -- CorpseDespawn's own staleness check is the sole judge, never re-derived here); or the
/// watchdog (<see cref="PendingTickCap"/>) gives up.
///
/// CRYSTAL PIN, MIRRORING SANCTUARY: while a corpse is pending, its crystal counter (band entry +
/// Offsets.ACrystalHearts, the SAME field and the SAME guarded-W8-of-3 pattern Sanctuary.cs pins
/// for a living ally) is pinned to Tuning.SanctuaryHearts every tick the retry itself reads
/// Transient -- which only happens when the corpse still reads dead-and-ours (a Transient read is
/// exactly a StillOurCorpse PASS followed by a later-step refusal; see CorpseDespawn's own class
/// doc), so no separate "still dead" re-check is needed here. This is what makes the retry window
/// vanilla-faithful: crystallization cannot complete while a despawn attempt is still pending, so
/// the outcome is always either "corpse despawns, no crystal" (matching vanilla Poach) or, if the
/// watchdog gives up, a corpse left standing exactly the way vanilla itself would leave one it
/// never got the chance to convert -- never the double-payout the owner caught live.
///
/// WATCHDOG: PendingTickCap (~900 ticks, ~30s at Engine's 33ms loop) bounds the wait. A corpse
/// that never resolves either way (e.g. a render node that never reappears) logs ONE Warn and is
/// dropped -- no further attempts, no further pin.
///
/// The credit-moment poach itself (store write + toast, LivingPoach.cs's WriteCarcass) is never
/// re-run here: RecordPoachDeed's own per-corpse dedupe latch (_poachedThisBattle) already
/// consumed the corpse's one poach opportunity before this queue is ever touched; Tick only ever
/// calls CorpseDespawn.TryDespawn and the crystal pin.
/// </summary>
internal sealed partial class LivingPoach
{
    /// <summary>~900 ticks at Engine's 33ms loop is ~30s -- long enough to outlast any ordinary
    /// current-actor/open-turn stall (a single unit's own turn), short enough that a truly stuck
    /// corpse doesn't pin its crystal counter for the rest of the battle.</summary>
    internal const int PendingTickCap = 900;

    private readonly struct PendingCorpse
    {
        internal readonly ushort NameId;
        internal readonly int Ticks;   // consecutive Transient retries already spent, since QueuePendingDespawn
        internal PendingCorpse(ushort nameId, int ticks) { NameId = nameId; Ticks = ticks; }
    }

    private readonly Dictionary<int, PendingCorpse> _pendingDespawns = new();

    /// <summary>Queue a transiently-refused despawn for retry. Called only from RecordPoachDeed,
    /// only when CorpseDespawn.TryDespawn's own reason read Transient -- a Permanent refusal must
    /// never reach here (the corpse is already provably not ours to retry).</summary>
    private void QueuePendingDespawn(int slot, ushort nameId) => _pendingDespawns[slot] = new PendingCorpse(nameId, 0);

    /// <summary>Engine's per-tick call (LivingPoach is not an ISignature; Engine.cs calls this
    /// directly beside its other per-tick subsystem calls, in-battle gated the same way its
    /// neighbors are). Retries every pending corpse's despawn and pins its crystal counter
    /// meanwhile; never throws (mirrors every other per-tick subsystem's fail-closed contract).</summary>
    public void Tick()
    {
        if (_pendingDespawns.Count == 0) return;

        // Snapshot the keys: the loop body may remove entries mid-iteration (despawned, dropped),
        // which a live Dictionary enumerator does not tolerate.
        foreach (int slot in new List<int>(_pendingDespawns.Keys))
        {
            try { TickOne(slot); }
            catch (Exception ex)
            {
                ModLogger.Error(LogVerb.Signature, "Living Poach's despawn retry tick failed: " + ex.Message);
                _pendingDespawns.Remove(slot);
            }
        }
    }

    private void TickOne(int slot)
    {
        var pending = _pendingDespawns[slot];
        bool despawned = CorpseDespawn.TryDespawn(_mem, slot, pending.NameId, out var refusal);
        if (despawned) { _pendingDespawns.Remove(slot); return; }

        if (refusal == DespawnRefusal.Permanent)
        {
            // No longer our corpse (revived, slot reused, or already chest-converted) --
            // CorpseDespawn's own staleness check already logged the specific reason.
            _pendingDespawns.Remove(slot);
            return;
        }

        int ticks = pending.Ticks + 1;
        if (ticks >= PendingTickCap)
        {
            ModLogger.Warn(LogVerb.Signature, $"Living Poach's corpse despawn retry for band slot {slot} gave up after {PendingTickCap} ticks (~30s); the poached carcass and the corpse both stand.");
            _pendingDespawns.Remove(slot);
            return;
        }

        PinCrystal(slot);
        _pendingDespawns[slot] = new PendingCorpse(pending.NameId, ticks);
    }

    /// <summary>Mirrors Sanctuary.cs's own pin exactly: the crystal counter lives at band entry +
    /// Offsets.ACrystalHearts (Sanctuary's own doc: "band entry -0x15"), guarded W8 of
    /// Tuning.SanctuaryHearts (3). An unwritable counter is a silent no-op -- never a raw pointer
    /// deref, never a throw.</summary>
    private void PinCrystal(int slot)
    {
        long addr = Band.Entry(slot) + Offsets.ACrystalHearts;
        if (_mem.Writable(addr, 1)) _mem.W8(addr, Tuning.SanctuaryHearts);
    }
}
