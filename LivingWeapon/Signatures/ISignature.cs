using System;

namespace LivingWeapon;

/// <summary>Per-tick frame facts for signature modules, computed once by the engine from one read of the sentinels.</summary>
internal readonly struct TickContext
{
    /// <summary>The engine tick's wall-clock instant (timeouts, grace windows).</summary>
    public DateTime Now { get; }
    /// <summary>On the live battlefield (<see cref="BattleState.OnField"/>).</summary>
    public bool OnField { get; }
    /// <summary>A genuine in-battle frame (<see cref="BattleState.InLiveBattle"/>); gates every module that writes battle memory.</summary>
    public bool InLive { get; }
    /// <summary>A battle map is on screen (<see cref="BattleState.BattleDisplayed"/>); looser than InLive, it survives
    /// between-turn mode-0 lulls. Pre-gate modules gate their typed Tick on this, and their ISignature.Tick shim must
    /// delegate here (LW-145 fix 4: the shim once wired InLive, a dormant trap since Engine calls the typed Tick).</summary>
    public bool BattleDisplayed { get; }

    public TickContext(DateTime now, bool onField, bool inLive, bool battleDisplayed)
    {
        Now = now;
        OnField = onField;
        InLive = inLive;
        BattleDisplayed = battleDisplayed;
    }
}

/// <summary>
/// One weapon-signature module (Puppeteer, Plague, Barrage, ...). The engine ticks each in order every in-battle
/// frame and calls ResetBattle on the debounced battle edges, so adding a signature is one constructor line
/// plus one array entry. Each module keeps a richer typed Tick for tests and implements this by delegation.
/// </summary>
internal interface ISignature
{
    /// <summary>Clear per-battle state, on the debounced battle exit edge.</summary>
    void ResetBattle();

    /// <summary>One engine tick (~33ms) while a battle is live.</summary>
    void Tick(in TickContext ctx);
}
