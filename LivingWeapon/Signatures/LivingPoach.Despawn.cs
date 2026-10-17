using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Corpse-despawn retry (live pass 2026-08-12). A vanilla-poached corpse yields neither crystal
/// nor chest, so a transient refusal from CorpseDespawn.cs is queued here per band slot and retried by
/// Engine.Tick (LivingPoach is not an ISignature) until TryDespawn succeeds, reads Permanent, or
/// <see cref="PendingTickCap"/> gives up with one Warn. While pending, the crystal counter is pinned
/// like Sanctuary.cs so the corpse cannot crystallize and pay out a second time. The poach write itself
/// is deduped by _poachedThisBattle and never re-run here.
/// </summary>
internal sealed partial class LivingPoach
{
    /// <summary>~900 ticks at the 33ms loop is ~30s, longer than any single-turn stall.</summary>
    internal const int PendingTickCap = 900;

    private readonly struct PendingCorpse
    {
        internal readonly ushort NameId;
        internal readonly int Ticks;   // consecutive Transient retries already spent, since QueuePendingDespawn
        internal PendingCorpse(ushort nameId, int ticks) { NameId = nameId; Ticks = ticks; }
    }

    private readonly Dictionary<int, PendingCorpse> _pendingDespawns = new();

    /// <summary>Queue a transiently refused despawn for retry. Permanent refusals never come here.</summary>
    private void QueuePendingDespawn(int slot, ushort nameId) => _pendingDespawns[slot] = new PendingCorpse(nameId, 0);

    /// <summary>Engine's per-tick call. Retries pending despawns and pins their crystal counters; never throws.</summary>
    public void Tick()
    {
        if (_pendingDespawns.Count == 0) return;

        // Snapshot the keys; the body removes entries mid-iteration.
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
            // No longer our corpse; CorpseDespawn already logged why.
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

    /// <summary>Same pin as Sanctuary.cs, a guarded W8 of Tuning.SanctuaryHearts at band entry + Offsets.ACrystalHearts.
    /// An unwritable counter is a no-op.</summary>
    private void PinCrystal(int slot)
    {
        long addr = Band.Entry(slot) + Offsets.ACrystalHearts;
        if (_mem.Writable(addr, 1)) _mem.W8(addr, Tuning.SanctuaryHearts);
    }
}
