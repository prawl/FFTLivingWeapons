using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Rod of Faith's Rapture signature. When the +3 wielder's HP drops below RaptureHpPct (30%) of
/// max, Master Teleportation (Tuning.RaptureMoveId) is written into the movement-ability field
/// (band +0x80, combat +0x9C) and held until they recover, then the previous movement bytes are
/// restored. Save/hold/restore follows Maim; the HP gate is in Rapture.Policy.cs. The 3-turn cap
/// was retired because its clock never ticked live (2026-06-10).
///
/// Releases. HP recovery, unequip / tier loss / wielder ambiguity, wielder death (3-tick streak),
/// and battle exit. An unlocated entry holds at the last known copy.
///
/// Tick only runs the arm/recovery logic on onField ticks, since the HP changes that matter land
/// during battleMode 2/3/4. SpiritualFont differs because its CT window sits under battleMode 1/5.
/// </summary>
internal sealed partial class Rapture : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);
    private const int RodOfFaithId = 58;
    private const int DeadNeeded = 3;   // consecutive hp==0 reads before the death release

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly List<int> _hands = new();
    private readonly RaptureState _state = new();
    private readonly ScopedLogger _slog;   // armed gate. A benched +3 rod must not narrate on console
    private bool _wasActive;
    private int _deadStreak;

    // The TurnTracker parameter is kept so Engine constructs every subsystem the same way; it is unused.
    public Rapture(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, TurnTracker turns, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _slog = ModLogger.For(LogVerb.Signature, () => Wielder.AnyDeployedMainHand(_mem, RodOfFaithId));
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
        int tier = Tuning.TierOf(_kills, RodOfFaithId);
        (int lvl, int br, int fa) fp = default;
        bool active = IsActive(m.Signature, tier) && Wielder.TryResolveMainHand(_mem, RodOfFaithId, out fp, _hands);
        if (ActivationEdge.Step(ref _wasActive, active))
        {
            _slog.Info(active
                ? "Rod of Faith at tier three is wielded on the field; emergency teleportation is ready."
                : "Emergency teleportation is no longer active.");
        }
        if (!active)
        {
            // Unequip / tier loss / ambiguity. The player's movement comes back NOW.
            if (_state.Held) Restore("gate lost");
            return;
        }
        if (!onField)
        {
            if (_state.Held) Hold();   // keep the image held through battleMode flickers (Maim's Drive)
            return;
        }

        long e = Wielder.Locate(_mem, RodOfFaithId, _hands, fp);
        if (e != 0 && _state.Held) _state.Addr = e;   // entry relocated. Retarget the restore
        if (e == 0)
        {
            if (_state.Held) Hold();   // unlocated. Hold at the last known copy
            return;
        }

        int hp = _mem.U16(e + Offsets.AHp);

        if (_state.Held)
        {
            if (hp == 0 && ++_deadStreak >= DeadNeeded) { Restore("wielder dead"); return; }
            if (hp > 0) _deadStreak = 0;
            if (HasRecovered(hp, _mem.U16(e + Offsets.AMaxHp), Tuning.RaptureHpPct))
            {
                Restore("recovered above threshold");
                return;
            }
            Hold();
            return;
        }

        int maxHp = _mem.U16(e + Offsets.AMaxHp);
        if (!ShouldArm(hp, maxHp, Tuning.RaptureHpPct)) return;

        // The grant image carries only the teleport bit, so the saved bytes are the player's own
        // movement pick. No other movement-bit writer exists (Spiritual Font writes HP/MP at runtime).
        byte[]? saved = ReadField(_mem, e);
        byte[]? grant = FieldFor(Tuning.RaptureMoveId);
        if (saved is null || grant is null) return;
        _state.Arm(e, saved, baselineTurns: 0, fp, grant);
        _deadStreak = 0;
        WriteField(_mem, e, grant);
        // Once-per-window read-back; 243 teleported the player (2026-06-10). If miss, flip
        // Tuning.RaptureMoveId to 242.
        string readback = ReadBackSet(_mem, e, Tuning.RaptureMoveId) ? "SET" : "MISS";
        ModLogger.EventWithTrace(LogVerb.Signature,
            $"The wielder dropped below thirty percent HP ({hp}/{maxHp}); Master Teleportation is theirs until they recover.",
            $"rapture grant detail (move id {Tuning.RaptureMoveId}, original movement saved as {saved[0]:X2} {saved[1]:X2} {saved[2]:X2}, readback={readback})");
        if (readback != "SET")
            ModLogger.Warn(LogVerb.Signature, "The teleportation write did not stick; the wielder's movement may be unchanged.");
    }

    /// <summary>Re-write the armed grant image at the last located entry. SameUnit-guarded (as in
    /// Maim.Drive) because band slots are fixed addresses and units migrate. Locate's twin
    /// tie-break (fix 2026-06-10) is enough for this single-address hold.</summary>
    private void Hold()
    {
        if (_state.Addr == 0 || !SameUnit(_mem, _state.Addr, _state.Fp)) return;
        if (_state.GrantField is { } grant) WriteField(_mem, _state.Addr, grant);
    }

    /// <summary>Write the saved movement bytes back and close the window. SameUnit-guarded. A
    /// stranger at the held address keeps its own movement field.</summary>
    private void Restore(string why)
    {
        bool same = _state.Addr != 0 && SameUnit(_mem, _state.Addr, _state.Fp);
        if (_state.SavedField is { } saved && same) WriteField(_mem, _state.Addr, saved);
        ModLogger.Event(LogVerb.Signature,
            $"The emergency ended ({WhyInFullWords(why)}); {(same ? "normal movement is back" : "movement was left unchanged (the wielder could not be re-found in memory)")}.");
        _state.Release();
        _deadStreak = 0;
    }

    /// <summary>The release reason codes (Restore's call sites) spelled as console prose.</summary>
    private static string WhyInFullWords(string why) => why switch
    {
        "recovered above threshold" => "the wielder recovered above the threshold",
        "gate lost" => "the weapon was unequipped or the tier was lost",
        "wielder dead" => "the wielder died",
        "battle reset" => "the battle ended",
        _ => why,
    };
}
