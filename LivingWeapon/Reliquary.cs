using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Reliquary Phase 1's deed-recording contract (docs/RELIQUARY_AC.md) -- the seam
/// KillTracker.CreditKill calls into at the credit edge (KillTracker.cs). Kept as an interface
/// (rather than a concrete Reliquary reference) so KillTracker's tests can inject a fake and
/// assert calls without constructing a real LegendStore/BannerToast pair.
/// </summary>
internal interface IDeedSink
{
    /// <summary>Record one credited kill's deed on a weapon.</summary>
    void RecordDeed(int weaponId, in VictimSnapshot victim);

    /// <summary>A kill was credited but no victim snapshot was ever captured for this slot (the
    /// missing-snapshot failure mode, docs/RELIQUARY_AC.md's Capture checklist) -- log-only, no
    /// deed recorded, tally still increments exactly as before Reliquary existed.</summary>
    void DeedMiss(int slot);
}

/// <summary>
/// The Reliquary composition seam: LegendStore (persistence) + BannerToast (Mark announce,
/// wired in Phase 1 stage 3) + meta (weapon names for toast wording) + an optional flight
/// recorder tap, wired together at KillTracker's credit edge via <see cref="IDeedSink"/>. Never
/// throws -- a Reliquary failure must never take down kill crediting or the tally. No IO of its
/// own: LegendStore.SaveIfDirty runs only at Engine's own save sites (battle-exit + on-change).
/// </summary>
internal sealed class Reliquary : IDeedSink
{
    private readonly LegendStore _store;
    private readonly BannerToast? _toast;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Action<string, string>? _recorder;
    // Marks earned THIS battle, in earn order -- feeds the battle-end match-report summary
    // (logging facelift stage 3). Engine reads it at the exit edge (before ResetBattle wipes it).
    private readonly List<(int weaponId, VictimClass.Archetype mark)> _battleMarks = new();

    /// <summary>Marks earned this battle (earn order). Cleared by <see cref="ResetBattle"/>.</summary>
    public IReadOnlyList<(int weaponId, VictimClass.Archetype mark)> BattleMarks => _battleMarks;

    /// <summary>Clear the per-battle Marks ledger. Engine calls this from ResetBattleState
    /// (both battle edges), AFTER the exit edge's summary composed from it.</summary>
    public void ResetBattle() => _battleMarks.Clear();

    /// <param name="toast">Null when BannerToasts is disabled (decision 11): Engine passes
    /// `_bannerToastsEnabled ? _toast : null` so a disabled config leaves the Mark-announce path
    /// fully inert (the deed is still recorded in <paramref name="store"/> regardless).</param>
    public Reliquary(LegendStore store, BannerToast? toast, Dictionary<int, WeaponMeta> meta,
                      Action<string, string>? recorder = null)
    {
        _store = store;
        _toast = toast;
        _meta = meta;
        _recorder = recorder;
    }

    /// <summary>Record the deed in the store, then flight-tap + toast-announce every
    /// newly-earned Mark (toast is null when disabled -- decision 11). Never throws.</summary>
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
                // The console match-report line the audit found MISSING: an earned Mark used to
                // surface only via flight tap + toast.
                ModLogger.Event(LogVerb.Mark, $"{name} earns the Mark of the {title}.");
                _recorder?.Invoke("mark-earned",
                    $"weapon={weaponId} name=\"{name}\" archetype={(int)mark} title=\"{title}\"");
                // Event-key convention (BannerToast.Policy.MarkPayload's doc comment): Marks own
                // 1000 + archetype index, distinct from tiers (1..3) and milestones (negated).
                _toast?.Enqueue(weaponId, 1000 + (int)mark, BannerToast.MarkPayload(name, title));
            }
        }
        catch (Exception ex) { ModLogger.Error(LogVerb.Mark, "Recording the kill's deed failed: " + ex.Message); }
    }

    /// <summary>A kill was credited with no captured victim snapshot -- log-only (mirrors the
    /// no-credit tap idiom, KillTracker.Corpses.cs:195): one Warning (a permanent deed was lost
    /// while the tally still counted -- degraded but coping, and armed by construction since it
    /// only fires on credited kills) + one flight record, never a deed. Never throws.</summary>
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
