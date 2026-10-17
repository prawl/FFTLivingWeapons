using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Deed-recording seam for Reliquary Phase 1 (docs/RELIQUARY_AC.md), called by KillTracker.CreditKill
/// at the credit edge (KillTracker.cs). An interface so KillTracker tests can inject a fake.
/// </summary>
internal interface IDeedSink
{
    /// <summary>Record one credited kill's deed on a weapon.</summary>
    void RecordDeed(int weaponId, in VictimSnapshot victim);

    /// <summary>A kill was credited but no victim snapshot was captured for this slot (docs/RELIQUARY_AC.md
    /// Capture checklist). Log-only; the tally still increments.</summary>
    void DeedMiss(int slot);

    /// <summary>LW-167 Living Poach: the credit-moment report of <see cref="RecordDeed"/> plus the victim's band slot
    /// (per-corpse dedupe key with nameId) and the kill's shape (delayed/charged credit, or turn-queue fallback),
    /// which feed stage 4's action discriminator. Default no-op so existing implementers compile unchanged.
    /// KillTracker.CreditKill calls it alongside RecordDeed; DeedFanout.cs is the only production override,
    /// forwarding to LivingPoach.</summary>
    void RecordPoachDeed(int weaponId, in VictimSnapshot victim, int slot, bool delayedOrCharged, bool viaFallback) { }
}

/// <summary>
/// Composes LegendStore (persistence), BannerToast (Mark announce), meta (weapon names for toast wording)
/// and an optional flight recorder tap at KillTracker's credit edge via <see cref="IDeedSink"/>.
/// Never throws, so a failure cannot take down kill crediting. No IO of its own; LegendStore.SaveIfDirty
/// runs at Engine's save sites.
/// </summary>
internal sealed class Reliquary : IDeedSink
{
    private readonly LegendStore _store;
    private readonly BannerToast? _toast;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Action<string, string>? _recorder;
    // Marks earned this battle in earn order, for the battle-end match-report summary (logging facelift stage 3).
    // Engine reads it at the exit edge, before ResetBattle.
    private readonly List<(int weaponId, VictimClass.Archetype mark)> _battleMarks = new();

    /// <summary>Marks earned this battle (earn order). Cleared by <see cref="ResetBattle"/>.</summary>
    public IReadOnlyList<(int weaponId, VictimClass.Archetype mark)> BattleMarks => _battleMarks;

    /// <summary>Clear the per-battle Marks ledger. Engine calls this from ResetBattleState after the exit edge's summary is composed.</summary>
    public void ResetBattle() => _battleMarks.Clear();

    /// <param name="toast">Null when BannerToasts is disabled (decision 11); the deed is still recorded in <paramref name="store"/>.</param>
    public Reliquary(LegendStore store, BannerToast? toast, Dictionary<int, WeaponMeta> meta,
                      Action<string, string>? recorder = null)
    {
        _store = store;
        _toast = toast;
        _meta = meta;
        _recorder = recorder;
    }

    /// <summary>Record the deed, then flight-tap and toast-announce each newly earned Mark (toast is null when disabled, decision 11). Never throws.</summary>
    public void RecordDeed(int weaponId, in VictimSnapshot victim)
    {
        try
        {
            var earned = _store.RecordDeed(weaponId, victim);
            foreach (var mark in earned)
            {
                _battleMarks.Add((weaponId, mark));   // the battle-end summary's Marks ledger
                string name = _meta.TryGetValue(weaponId, out var m) ? m.Name : $"weapon {weaponId}";
                string title = VictimClass.MarkTitle(mark);
                // Console match-report line for each earned Mark.
                ModLogger.Event(LogVerb.Mark, $"{name} earns the Mark of the {title}.");
                _recorder?.Invoke("mark-earned",
                    $"weapon={weaponId} name=\"{name}\" archetype={(int)mark} title=\"{title}\"");
                // Event key: Marks use 1000 + archetype index (BannerToast.Policy.MarkPayload), distinct from tiers (1..3) and milestones (negated).
                _toast?.Enqueue(weaponId, 1000 + (int)mark, BannerToast.MarkPayload(name, title));
            }
        }
        catch (Exception ex) { ModLogger.Error(LogVerb.Mark, "Recording the kill's deed failed: " + ex.Message); }
    }

    /// <summary>A kill was credited with no captured victim snapshot. Log-only like the no-credit tap
    /// (KillTracker.Corpses.cs:195): one Warning and one flight record, no deed (the tally still counted). Never throws.</summary>
    public void DeedMiss(int slot)
    {
        try
        {
            ModLogger.Warn(LogVerb.Mark, $"A kill was credited at battle slot {slot} but no victim snapshot was captured; the deed is lost.");
            _recorder?.Invoke("deed-miss", $"slot={slot}");
        }
        catch (Exception ex) { ModLogger.Error(LogVerb.Mark, "Recording a missed deed failed: " + ex.Message); }
    }
}
