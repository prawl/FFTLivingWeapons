using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-167 Living Poach -- the stateful executor. Consumes KillTracker's widened deed sink
/// (IDeedSink.RecordPoachDeed, delivered via DeedFanout.cs) and, on an eligible corpse, rolls a
/// carcass into the Poacher's Den store: LivingPoachPolicy.Decide is the pure gate/roll, this
/// class gathers the inputs it needs (weapon formula from meta, species from the victim's job,
/// the map lookup) and performs the guarded write + toast.
///
/// wasBasicAttack is a constructor-injected <see cref="Func{TResult}"/> so stage 4 (the action
/// discriminator, the tape-mining premise LW-167's plan is still gathering) can wire the real
/// signal later without touching this file: Engine.cs passes <c>() =&gt; false</c> for stage 2,
/// which keeps the whole feature structurally disarmed in production (LivingPoachPolicy.Decide's
/// AND-gate never passes) regardless of what killerHasPoach/the map/the roll say. killerHasPoach
/// IS the real read already (<see cref="ReadKillerHasPoach"/>): a per-weapon delegate so each call
/// re-locates that weapon's deployed main-hand wielder and reads its live Poach support bit,
/// rather than a snapshot taken once at construction.
///
/// PER-CORPSE DEDUPE: a dual-wielder's kill reports to this sink once per credited weapon (same
/// battle slot + victim nameId both times) -- only the FIRST eligible report (verdict != None)
/// consumes the corpse's one poach opportunity, win or lose the guarded write that follows.
/// Cleared every battle in <see cref="ResetBattle"/> (Engine calls this on both battle edges,
/// mirroring Reliquary's per-battle Marks ledger).
///
/// Every memory access is guarded: Readable before the current-count read, Writable before the
/// W8, never a raw deref (house rule). Never throws.
///
/// STAGE 3 (LW-167): a successful poach also attempts <see cref="CorpseDespawn.TryDespawn"/> --
/// vanilla's own Poach leaves no corpse behind, so a poached carcass shouldn't linger either. A
/// despawn refusal never rolls back the store write/toast above it (see the ctor doc's carcass-
/// stands note); the whole feature stays disarmed in production via wasBasicAttack regardless.
/// </summary>
internal sealed class LivingPoach
{
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly PoachMap _map;
    private readonly IGameMemory _mem;
    private readonly BannerToast _toast;
    private readonly Func<int, bool> _killerHasPoach;
    private readonly Func<bool> _wasBasicAttack;
    private readonly Func<int> _roll;

    private readonly HashSet<(int slot, ushort nameId)> _poachedThisBattle = new();

    public LivingPoach(Dictionary<int, WeaponMeta> meta, PoachMap map, IGameMemory mem, BannerToast toast,
        Func<int, bool> killerHasPoach, Func<bool> wasBasicAttack, Func<int>? roll = null)
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

    /// <summary>Clear the per-battle corpse dedupe latch. Engine calls this on both battle edges
    /// (the ResetBattleState convention every other deed-sink consumer follows).</summary>
    public void ResetBattle() => _poachedThisBattle.Clear();

    /// <summary>The credit-moment report (IDeedSink.RecordPoachDeed's production consumer, wired
    /// through DeedFanout.cs). Never throws.</summary>
    public void RecordPoachDeed(int weaponId, in VictimSnapshot victim, int slot, bool delayedOrCharged, bool viaFallback)
    {
        try
        {
            if (!victim.Has) return;
            var corpseKey = (slot, victim.NameId);
            if (_poachedThisBattle.Contains(corpseKey)) return;

            bool dormant = _meta.TryGetValue(weaponId, out var m) && Tuning.IsDormantPoachFormula(m.Formula);

            PoachCarcass carcass = default;
            bool speciesMapped = _map.IsLoaded && LivingPoachPolicy.JobInMonsterRange(victim.Job)
                && _map.TryGetSpecies(LivingPoachPolicy.SpeciesOf(victim.Job), out carcass);

            bool hasPoach = _killerHasPoach(weaponId);
            bool basicAttack = _wasBasicAttack();
            int roll = _roll();

            var verdict = LivingPoachPolicy.Decide(dormant, hasPoach, basicAttack, victim.Job, speciesMapped, roll);
            if (verdict == PoachVerdict.None) return;

            // One poach opportunity per corpse, win or lose the guarded write below.
            _poachedThisBattle.Add(corpseKey);

            int key = verdict == PoachVerdict.Common ? carcass.CommonKey : carcass.RareKey;
            string name = verdict == PoachVerdict.Common ? carcass.CommonName : carcass.RareName;
            if (WriteCarcass(weaponId, key, name))
            {
                // LW-167 stage 3: vanilla fidelity -- a real Poach leaves no corpse behind (no
                // crystal, no chest), so a poached carcass shouldn't either. A despawn refusal
                // never rolls back the poach above: the store write and toast already landed,
                // the corpse just stands (CorpseDespawn.cs logs its own refusal reason).
                CorpseDespawn.TryDespawn(_mem, slot, victim.NameId);
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

    /// <summary>The killer's real Poach read: locate <paramref name="weaponId"/>'s single deployed
    /// main-hand wielder (Wielder.ResolveDeployedMainHand -- the same locate Choir/Kobu/Bulwark
    /// use) and read its live combat-struct support bitfield for the Poach bit (id 215, combat
    /// +0x98 byte 2 mask 0x40 via Signatures.SupportBit -- the band-relative address is
    /// Offsets.ASupport + that same (byteOffset, mask), the identity Choir already relies on).
    /// Locate unavailable (zero or ambiguous deployed wielders) -&gt; false, FAIL CLOSED: a poach
    /// must never fire on a guess about who's holding the weapon.</summary>
    internal static bool ReadKillerHasPoach(IGameMemory mem, int weaponId)
    {
        long entry = Wielder.ResolveDeployedMainHand(mem, weaponId, out _);
        if (entry == 0) return false;
        if (!Signatures.SupportBit(Tuning.PoachSupportAbilityId, out int off, out byte mask)) return false;
        long addr = entry + Offsets.ASupport + off;
        if (!mem.Readable(addr, 1)) return false;
        return (mem.U8(addr) & mask) != 0;
    }
}
