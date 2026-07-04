using System;

namespace LivingWeapon;

/// <summary>
/// The battle-event timeline. KillTracker already reads every unit slot's HP and grid position
/// each 33ms tick; this diffs those readings and emits one timestamped line per change (damage,
/// heal, move), tagged with the latched actor's weapon(s). Makes "what the game did" vs "when we
/// saw it" comparable at tick granularity. Kills, turn edges, and grant transitions stay
/// always-on in their own subsystems. The sink is injected so tests capture lines without a logger.
///
/// LOGGING OVERHAUL NOTE: Engine now constructs this with verbose=true unconditionally and
/// sink=ModLogger.LogDebug -- the ev: timeline is ALWAYS captured to livingweapon.log (Debug tier
/// writes the file unconditionally), and reaches the console only when Config.VerboseLog is on.
/// This deliberately turns the timeline ON in every build (it used to be a DEV-only const); the
/// black-box evidence chain wants the data, the console volume knob keeps it quiet by default.
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
        _sink = sink ?? ModLogger.Log;
    }

    /// <summary>Forget every baseline. Call on battle enter and exit.</summary>
    public void ResetBattle() => Array.Clear(_seen, 0, _seen.Length);

    /// <summary>One pre-formatted diagnostic line (verbose-gated, same as <see cref="Observe"/>).
    /// KillTracker's D4 AREC kill diagnostic routes through here rather than owning its own sink,
    /// so it stays zero-coupled to the credit path -- see KillTracker.CreditKill.</summary>
    public void KillDiag(string line)
    {
        if (!_verbose) return;
        _sink(line);
    }

    /// <summary>One valid slot reading per tick. The first sighting of a slot baselines silently;
    /// after that, any HP or position change is one event line.</summary>
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
            string tag = actor.Length > 0 ? $" [w:{actor}]" : "";
            _sink($"ev: {(d < 0 ? "dmg" : "heal")} {Math.Abs(d)} -- battle slot {slot} at ({gx},{gy}) hp {_hp[slot]}->{hp}/{maxHp}{tag}");
            _hp[slot] = hp;
        }
        if (gx != _gx[slot] || gy != _gy[slot])
        {
            _sink($"ev: move -- battle slot {slot} ({_gx[slot]},{_gy[slot]})->({gx},{gy})");
            _gx[slot] = gx; _gy[slot] = gy;
        }
    }
}
