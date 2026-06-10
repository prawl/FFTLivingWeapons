using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Rod of Faith's "Rapture" signature: when the +3 wielder's HP drops strictly below
/// RaptureHpPct (30%) of max, Master Teleportation (Tuning.RaptureMoveId) is written and HELD
/// in their movement-ability field (band +0x80 == combat +0x9C) UNTIL THEY RECOVER -- then the
/// PREVIOUS movement bytes restore. The save-once/hold/restore discipline is Maim's; the HP
/// gate is ConditionMet's integer math (Rapture.Policy.cs). The original 3-turn cap is retired:
/// its clock never ticked live (both TurnTracker attribution and the band-CT read failed the
/// live test, 2026-06-10), while the recovery release was player-verified the same day --
/// "active while desperate" is the design now, and it needs no clock at all.
///
/// Releases that restore the player's movement: HP recovery to/above the threshold (the
/// emergency is over -- the next drop below it arms a fresh window), unequip / tier loss /
/// wielder ambiguity (grant verification), wielder death (3-tick streak), and battle exit.
/// An unlocated entry holds at the last known copy, SameUnit-guarded. Guarded writes only.
///
/// NOTE: Rapture.Tick is intentionally gated to onField ticks for its arm/recovery logic --
/// HP changes that trigger arm or release occur during active battlefield turns (battleMode
/// 2/3/4), not during the player's own command menus (battleMode 1/5). This is the deliberate
/// asymmetry with SpiritualFont, whose CT observation MUST span menu-time (inLive) because
/// the wielder's own CT high phase sits under battleMode 1/5 while the player selects actions.
/// </summary>
internal sealed partial class Rapture
{
    private const int RodOfFaithId = 58;
    private const int DeadNeeded = 3;   // consecutive hp==0 reads before the death release

    private static readonly LiveMemory Live = new();   // Wielder/Band reads ride IGameMemory; == Mem

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly List<int> _hands = new();
    private readonly RaptureState _state = new();
    private bool _wasActive;
    private int _deadStreak;

    // The TurnTracker parameter is retained for wiring stability (Engine constructs every
    // subsystem identically) but deliberately unused: the expiry clock is the wielder's own CT.
    public Rapture(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, TurnTracker turns)
    {
        _meta = meta;
        _kills = kills;
    }

    public void ResetBattle()
    {
        if (_state.Held) Restore("battle reset");
        _wasActive = false;
        _deadStreak = 0;
    }

    public void Tick(bool onField)
    {
        if (!_meta.TryGetValue(RodOfFaithId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierFor(_kills.TryGetValue(RodOfFaithId, out int k) ? k : 0);
        (int lvl, int br, int fa) fp = default;
        bool active = IsActive(m.Signature, tier) && Wielder.TryResolveMainHand(Live, RodOfFaithId, out fp, _hands);
        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info($"rapture {(active ? "ACTIVE -- Rod of Faith at +3 is wielded, emergency teleportation is armed" : "inactive")}");
        }
        if (!active)
        {
            // Unequip / tier loss / ambiguity: the player's movement comes back NOW.
            if (_state.Held) Restore("gate lost");
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
            if (_state.Held) Hold();   // unlocated: hold at the last known copy
            return;
        }

        int hp = Live.U16(e + Offsets.AHp);

        if (_state.Held)
        {
            if (hp == 0 && ++_deadStreak >= DeadNeeded) { Restore("wielder dead"); return; }
            if (hp > 0) _deadStreak = 0;
            if (HasRecovered(hp, Live.U16(e + Offsets.AMaxHp), Tuning.RaptureHpPct))
            {
                Restore("recovered above threshold");
                return;
            }
            Hold();
            return;
        }

        int maxHp = Live.U16(e + Offsets.AMaxHp);
        if (!ShouldArm(hp, maxHp, Tuning.RaptureHpPct)) return;

        // The saved bytes are the player's own movement pick; the grant image carries ONLY the
        // teleport bit (no other movement-bit writer exists -- Spiritual Font restores HP/MP via
        // runtime writes now, never the movement field).
        byte[]? saved = ReadField(e);
        byte[]? grant = FieldFor(Tuning.RaptureMoveId);
        if (saved is null || grant is null) return;
        _state.Arm(e, saved, baselineTurns: 0, fp, grant);
        _deadStreak = 0;
        WriteField(e, grant);
        // Once-per-window read-back: 243 is proven live (player teleported, 2026-06-10); SET/MISS
        // still logged as a sanity signal (if MISS, flip Tuning.RaptureMoveId to 242).
        string readback = ReadBackSet(Live, e, Tuning.RaptureMoveId) ? "SET" : "MISS";
        Log.Info($"rapture: wielder dropped below 30% HP ({hp}/{maxHp}) -- Master Teleportation (move id {Tuning.RaptureMoveId}) granted until they recover " +
                 $"(original movement saved as {saved[0]:X2} {saved[1]:X2} {saved[2]:X2}) {(readback == "SET" ? "(write verified)" : "(write did NOT stick)")}");
    }

    /// <summary>Re-write the armed grant image at the last located entry (beats engine
    /// re-assertion). SameUnit-guarded (Maim.Drive's discipline): band slots are fixed addresses
    /// and units migrate, so a mismatch skips the write -- the turn-expiry clock keeps counting.
    /// NOTE: Locate's identical-twin tie-break (units standing on tile (0,0), fix 2026-06-10)
    /// is sufficient here -- save/hold/restore is single-address by design.</summary>
    private void Hold()
    {
        if (_state.Addr == 0 || !SameUnit(Live, _state.Addr, _state.Fp)) return;
        if (_state.GrantField is { } grant) WriteField(_state.Addr, grant);
    }

    /// <summary>Write the saved movement bytes back and close the window. SameUnit-guarded: if a
    /// stranger now occupies the held address, its movement field is left alone (the wielder's
    /// own bytes re-assert from the fresh per-battle struct rebuild).</summary>
    private void Restore(string why)
    {
        bool same = _state.Addr != 0 && SameUnit(Live, _state.Addr, _state.Fp);
        if (_state.SavedField is { } saved && same) WriteField(_state.Addr, saved);
        Log.Info($"rapture: wielder recovered ({why}) -- {(same ? "normal movement restored" : "movement left unchanged (unit entry migrated)")}");
        _state.Release();
        _deadStreak = 0;
    }
}
