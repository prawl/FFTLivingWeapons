using System;

namespace LivingWeapon;

/// <summary>
/// The per-tick frame facts a signature module may key on, computed once by the engine
/// (one read of the sentinels per tick, shared by every module).
/// </summary>
internal readonly struct TickContext
{
    /// <summary>The engine tick's wall-clock instant (timeouts, grace windows).</summary>
    public DateTime Now { get; }
    /// <summary>On the live battlefield (<see cref="BattleState.OnField"/>).</summary>
    public bool OnField { get; }
    /// <summary>A genuine in-battle frame (<see cref="BattleState.InLiveBattle"/>) --
    /// the gate for every module that writes battle memory.</summary>
    public bool InLive { get; }

    public TickContext(DateTime now, bool onField, bool inLive)
    {
        Now = now;
        OnField = onField;
        InLive = inLive;
    }
}

/// <summary>
/// One weapon-signature module (Charm Lock, Plague, Barrage, ...). The engine drives every
/// module identically -- Tick each in-battle frame in a fixed order, ResetBattle on the
/// debounced battle edges -- so adding a signature is one constructor line plus one array
/// entry, not four hand-maintained call sites. Modules self-select the context facts they
/// need; each keeps its richer typed Tick for tests and implements this by delegation.
/// </summary>
internal interface ISignature
{
    /// <summary>Clear per-battle state. Called on the debounced battle exit edge.</summary>
    void ResetBattle();

    /// <summary>One engine tick (~33ms) while a battle is live.</summary>
    void Tick(in TickContext ctx);
}
