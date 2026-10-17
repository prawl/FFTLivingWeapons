using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Living Poach executor. Consumes KillTracker's deed sink (IDeedSink.RecordPoachDeed, via
/// DeedFanout.cs) and rolls a carcass into the Poacher's Den store; LivingPoachPolicy.Decide is the pure roll.
/// Since 2026-08-12 Engine wires the per-weapon delegates to <see cref="ReadWasBasicAttack"/>
/// and <see cref="ReadKillerHasPoach"/> (LIVE_LEDGER). <see cref="RecordPoachDeed"/> refuses viaFallback and
/// delayedOrCharged credits and allows one poach per (slot, nameId), cleared in <see cref="ResetBattle"/>.
/// A poach also tries <see cref="CorpseDespawn.TryDespawn"/>; transient refusals retry via LivingPoach.Despawn.cs.
/// </summary>
internal sealed partial class LivingPoach
{
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly PoachMap _map;
    private readonly IGameMemory _mem;
    private readonly BannerToast _toast;
    private readonly Func<int, bool> _killerHasPoach;
    private readonly Func<int, bool> _wasBasicAttack;
    private readonly Func<int> _roll;

    private readonly HashSet<(int slot, ushort nameId)> _poachedThisBattle = new();

    public LivingPoach(Dictionary<int, WeaponMeta> meta, PoachMap map, IGameMemory mem, BannerToast toast,
        Func<int, bool> killerHasPoach, Func<int, bool> wasBasicAttack, Func<int>? roll = null)
    {
        _meta = meta;
        _map = map;
        _mem = mem;
        _toast = toast;
        _killerHasPoach = killerHasPoach;
        _wasBasicAttack = wasBasicAttack;
        var rng = new Random();
        _roll = roll ?? (() => rng.Next(0, 256));   // 0..255 inclusive, IRoll's live impl
    }

    /// <summary>Clear the per-battle corpse dedupe and the pending-despawn queue on both
    /// battle edges; pending corpses stop being retried.</summary>
    public void ResetBattle()
    {
        _poachedThisBattle.Clear();
        _pendingDespawns.Clear();
    }

    /// <summary>Credit-moment report (IDeedSink.RecordPoachDeed consumer, wired through DeedFanout.cs).</summary>
    public void RecordPoachDeed(int weaponId, in VictimSnapshot victim, int slot, bool delayedOrCharged, bool viaFallback)
    {
        try
        {
            if (!victim.Has) return;
            var corpseKey = (slot, victim.NameId);
            if (_poachedThisBattle.Contains(corpseKey)) return;

            // Fallback and delayed/charged credits are never a confirmed ordinary blow; refuse before the discriminator.
            if (viaFallback)
            {
                ModLogger.Debug(LogVerb.Signature, $"Living Poach refused weapon {weaponId}'s credit at battle slot {slot}: the kill's actor was a fallback attribution guess, not a confirmed basic Attack.");
                return;
            }
            if (delayedOrCharged)
            {
                ModLogger.Debug(LogVerb.Signature, $"Living Poach refused weapon {weaponId}'s credit at battle slot {slot}: the kill landed via a delayed or charged action (Jump/spellcast), not an ordinary blow.");
                return;
            }

            bool dormant = _meta.TryGetValue(weaponId, out var m) && Tuning.IsDormantPoachFormula(m.Formula);

            // Map membership by job id is the whole monster gate (see PoachMap.cs).
            PoachCarcass carcass = default;
            bool jobMapped = _map.IsLoaded && _map.TryGetJob(victim.Job, out carcass);

            bool hasPoach = _killerHasPoach(weaponId);
            bool basicAttack = _wasBasicAttack(weaponId);
            int roll = _roll();

            var verdict = LivingPoachPolicy.Decide(dormant, hasPoach, basicAttack, victim.Job, jobMapped, roll);
            if (verdict == PoachVerdict.None) return;

            // One poach opportunity per corpse, whether or not the write below succeeds.
            _poachedThisBattle.Add(corpseKey);

            int key = verdict == PoachVerdict.Common ? carcass.CommonKey : carcass.RareKey;
            string name = verdict == PoachVerdict.Common ? carcass.CommonName : carcass.RareName;
            if (WriteCarcass(weaponId, key, name))
            {
                // A real Poach leaves no corpse, so despawn this one. A refusal never rolls
                // back the store write. A transient refusal retries via LivingPoach.Despawn.cs's Tick; a
                // permanent one (no longer our corpse) is not queued, CorpseDespawn.cs already logged it.
                if (!CorpseDespawn.TryDespawn(_mem, slot, victim.NameId, out var refusal) && refusal == DespawnRefusal.Transient)
                {
                    QueuePendingDespawn(slot, victim.NameId);
                }
            }
        }
        catch (Exception ex) { ModLogger.Error(LogVerb.Signature, "Living Poach's credit-moment handling failed: " + ex.Message); }
    }

    private bool WriteCarcass(int weaponId, int key, string name)
    {
        long addr = Offsets.PoachStoreBase + (key - 1);
        if (!_mem.Readable(addr, 1))
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach could not read the Den store for key {key}; the poach is skipped.");
            return false;
        }
        int current = _mem.U8(addr);
        if (current >= 255)
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's Den store for key {key} is already full (255); the poach is skipped.");
            return false;
        }
        if (!_mem.Writable(addr, 1))
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach could not write the Den store for key {key}; the poach is skipped.");
            return false;
        }
        _mem.W8(addr, (byte)(current + 1));

        string displayName = LivingPoachPolicy.StripIconMarkup(name);
        _toast.Enqueue(weaponId, Tuning.PoachToastKey, displayName);
        ModLogger.Event(LogVerb.Signature, $"The weapon's spirit claims the carcass: {displayName} added to the Poacher's Den.");
        return true;
    }
}
