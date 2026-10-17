using System;

namespace LivingWeapon;

/// <summary>
/// The battle-event timeline. KillTracker reads each slot's HP and grid position every 33ms tick; this diffs
/// the readings and emits one timestamped line per change (damage, heal, move), tagged with the latched
/// actor's weapons. The sink is injected so tests capture lines without a logger. Engine passes verbose=true
/// and ModLogger.LogDebug, so the ev. Timeline always lands in livingweapon.log (Debug writes the file
/// unconditionally) and never on the console (the VerboseLog knob is gone).
/// </summary>
internal sealed class BattleLog
{
    private readonly bool _verbose;
    private readonly Action<string> _sink;
    private readonly bool[] _seen = new bool[Offsets.BandSlots];
    private readonly int[] _hp = new int[Offsets.BandSlots];
    private readonly int[] _gx = new int[Offsets.BandSlots];
    private readonly int[] _gy = new int[Offsets.BandSlots];

    public BattleLog(bool verbose, Action<string>? sink = null)
    {
        _verbose = verbose;
        _sink = sink ?? (s => ModLogger.Debug(LogVerb.Trace, s));
    }

    /// <summary>Forget every baseline. Call on battle enter and exit.</summary>
    public void ResetBattle() => Array.Clear(_seen, 0, _seen.Length);

    /// <summary>One pre-formatted diagnostic line, verbose-gated like <see cref="Observe"/>. KillTracker's D4 AREC
    /// kill diagnostic routes here to stay decoupled from the credit path (KillTracker.CreditKill).</summary>
    public void KillDiag(string line)
    {
        if (!_verbose) return;
        _sink(line);
    }

    /// <summary>First sighting of a slot baselines silently; after that any HP or position change is one line.</summary>
    public void Observe(int slot, int hp, int maxHp, int gx, int gy, string actor)
    {
        if (!_verbose) return;
        if (!_seen[slot])
        {
            _seen[slot] = true;
            _hp[slot] = hp; _gx[slot] = gx; _gy[slot] = gy;
            return;
        }
        if (hp != _hp[slot])
        {
            int d = hp - _hp[slot];
            string tag = actor.Length > 0 ? $" (weapons: {actor})" : "";
            _sink($"event: {(d < 0 ? "damage" : "healing")} {Math.Abs(d)}; battle slot {slot} at ({gx},{gy}) hit points {_hp[slot]} -> {hp} of {maxHp}{tag}");
            _hp[slot] = hp;
        }
        if (gx != _gx[slot] || gy != _gy[slot])
        {
            _sink($"event: move; battle slot {slot} moved from ({_gx[slot]},{_gy[slot]}) to ({gx},{gy})");
            _gx[slot] = gx; _gy[slot] = gy;
        }
    }
}
