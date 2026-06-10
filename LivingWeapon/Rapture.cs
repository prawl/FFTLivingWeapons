using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Rod of Faith's "Rapture" signature: when the +3 wielder's HP drops below RaptureHpPct (30%)
/// of max, Master Teleportation (Tuning.RaptureMoveId) is written and HELD in their movement-
/// ability field (band +0x80 == combat +0x9C) for RaptureTurns (3) of their turns -- then the
/// PREVIOUS movement bytes restore. The save-once/hold/restore discipline is Maim's; the turn
/// window is TurnTracker's; the HP gate is ConditionMet's integer math (Rapture.Policy.cs).
///
/// Releases that restore the player's movement: window expiry (then HP must RECOVER above the
/// threshold before re-arming -- no perpetual teleport at low HP), unequip / tier loss / wielder
/// ambiguity (grant verification), wielder death (3-tick streak), and battle exit. An unlocated
/// entry skips writes but keeps the window (turn expiry still counts). All guarded writes.
/// </summary>
internal sealed partial class Rapture
{
    private const int RodOfFaithId = 58;
    private const int DeadNeeded = 3;   // consecutive hp==0 reads before the death release

    private static readonly LiveMemory Live = new();   // Wielder/Band reads ride IGameMemory; == Mem

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly TurnTracker _turns;
    private readonly List<int> _hands = new();
    private readonly RaptureState _state = new();
    private bool _wasActive;
    private bool _rearmReady = true;
    private int _deadStreak;

    public Rapture(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, TurnTracker turns)
    {
        _meta = meta;
        _kills = kills;
        _turns = turns;
    }

    public void ResetBattle()
    {
        if (_state.Held) Restore("battle reset");
        _rearmReady = true;
        _wasActive = false;
        _deadStreak = 0;
    }

    public void Tick(bool onField)
    {
        if (!_meta.TryGetValue(RodOfFaithId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierFor(_kills.TryGetValue(RodOfFaithId, out int k) ? k : 0);
        (int lvl, int br, int fa) fp = default;
        bool active = IsActive(m.Signature, tier) && Wielder.TryResolve(Live, RodOfFaithId, out fp, _hands);
        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info($"rapture {(active ? "ACTIVE (+3 Rod of Faith wielded)" : "inactive")}");
        }
        if (!active)
        {
            // Unequip / tier loss / ambiguity: the player's movement comes back NOW.
            if (_state.Held) { Restore("gate lost"); _rearmReady = true; }
            return;
        }
        if (!onField)
        {
            if (_state.Held) Hold();   // keep the image held through battleMode flickers (Maim's Drive)
            return;
        }

        long e = Wielder.Locate(Live, RodOfFaithId, _hands, fp);
        if (e != 0 && _state.Held) _state.Addr = e;   // entry relocated: retarget the restore
        if (e == 0)
        {
            if (_state.Held) Hold();   // unlocated: hold at the last known copy; expiry still counts
            return;
        }

        int hp = Live.U16(e + Offsets.AHp);
        int turns = _turns.Turns(fp.lvl, fp.br, fp.fa);

        if (_state.Held)
        {
            if (hp == 0 && ++_deadStreak >= DeadNeeded) { Restore("wielder dead"); return; }
            if (hp > 0) _deadStreak = 0;
            if (IsExpired(turns - _state.BaselineTurns, Tuning.RaptureTurns))
            {
                Restore($"window over ({Tuning.RaptureTurns} turns)");
                return;   // _rearmReady stays false: HP must recover above the threshold first
            }
            Hold();
            return;
        }

        int maxHp = Live.U16(e + Offsets.AMaxHp);
        bool below = IsBelow(hp, maxHp, Tuning.RaptureHpPct);
        _rearmReady = CanRearm(_rearmReady, below);
        if (!_rearmReady || !ShouldArm(hp, maxHp, Tuning.RaptureHpPct)) return;

        byte[]? saved = ReadField(e);
        byte[]? grant = FieldFor(Tuning.RaptureMoveId);
        if (saved is null || grant is null) return;
        _state.Arm(e, saved, turns);
        _rearmReady = false;
        _deadStreak = 0;
        WriteField(e, grant);
        Log.Info($"rapture: armed at hp {hp}/{maxHp} -- movement {Tuning.RaptureMoveId} held for " +
                 $"{Tuning.RaptureTurns} turns (saved {saved[0]:X2} {saved[1]:X2} {saved[2]:X2})");
    }

    /// <summary>Re-write the teleport image at the last located entry (beats engine re-assertion).</summary>
    private void Hold()
    {
        byte[]? grant = FieldFor(Tuning.RaptureMoveId);
        if (grant is not null && _state.Addr != 0) WriteField(_state.Addr, grant);
    }

    /// <summary>Write the saved movement bytes back and close the window.</summary>
    private void Restore(string why)
    {
        if (_state.SavedField is { } saved && _state.Addr != 0) WriteField(_state.Addr, saved);
        Log.Info($"rapture: released ({why}) -- movement restored");
        _state.Release();
        _deadStreak = 0;
    }
}
