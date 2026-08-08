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
    /// <summary>A battle map is on screen (<see cref="BattleState.BattleDisplayed"/>) -- looser
    /// than InLive, survives the between-turn mode-0 lulls. The pre-gate module (TreasureMaster)
    /// gates its typed Tick on THIS, not InLive; its ISignature.Tick shim
    /// must delegate here (LW-145 fix 4: the shim wired InLive instead, a dormant wrong-gate
    /// trap the module never actually hit in production, since Engine ticks it pre-gate
    /// through its typed Tick directly and never through this interface today).</summary>
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
/// One weapon-signature module (Puppeteer, Plague, Barrage, ...). The engine drives every
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
