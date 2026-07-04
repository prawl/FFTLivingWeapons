using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Umbral Rod's "Life Sap" signature: when a kill is credited to a +3 Umbral Rod, the wielder
/// drinks the felled foe's vigor -- their HP is written UP by Tuning.LifeSapPct of their max,
/// clamped at full. The heal never revives (a 0-HP wielder is untouched; HP 0 -&gt; positive is
/// the engine's revival signal). TRIGGER: the per-weapon kill-tally diff (the ExtraTurn
/// freshKill pattern), so attribution rides the proven KillTracker credit -- no second death
/// detector. WIELDER: roster resolve + band walk with the twin filter (Wielder.cs). The HP
/// write lands on the authoritative band entry (the field Ricochet's chip writes), guarded.
/// </summary>
internal sealed partial class LifeSap : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick();
    private const int UmbralId = 56;

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly List<int> _hands = new();
    private int _lastCount = -1;
    private bool _wasActive;

    public LifeSap(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
    }

    public void ResetBattle()
    {
        _lastCount = -1;   // re-prime next battle (no phantom heal from a stale diff)
        _wasActive = false;
    }

    public void Tick()
    {
        if (!_meta.TryGetValue(UmbralId, out var m) || m.Signature is null) return;
        int count = _kills.TryGetValue(UmbralId, out int k) ? k : 0;
        (int lvl, int br, int fa) fp = default;
        bool active = IsActive(m.Signature, Tuning.TierFor(count))
                      && Wielder.TryResolveMainHand(_mem, UmbralId, out fp, _hands);
        if (active != _wasActive)
        {
            _wasActive = active;
            ModLogger.Log($"life-sap {(active ? "ACTIVE -- Umbral Rod at +3 is wielded, kills will restore HP" : "inactive")}");
        }
        if (!active) { _lastCount = count; return; }   // keep primed: an inactive-window kill never fires later

        bool fresh = Signatures.FreshKill(_lastCount, count);
        _lastCount = count;
        if (!fresh) return;

        long e = Wielder.Locate(_mem, UmbralId, _hands, fp);
        if (e == 0) { ModLogger.Log("life-sap: kill scored but the wielder could not be found in memory this tick -- heal skipped [locate miss]"); return; }
        int hp = _mem.U16(e + Offsets.AHp), maxHp = _mem.U16(e + Offsets.AMaxHp);
        int heal = HealAmount(maxHp, Tuning.LifeSapPct);
        int newHp = NewHp(hp, maxHp, heal);
        if (newHp == hp) { ModLogger.LogDebug($"life-sap: kill scored but wielder is already at full HP ({hp}/{maxHp}) -- no heal needed"); return; }
        WriteHp(_mem, e, newHp);
        ModLogger.Log($"life-sap: kill restored {newHp - hp} HP to the wielder (25% of max) -- HP {hp}->{newHp} (max {maxHp})");
    }
}
