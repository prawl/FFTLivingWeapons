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
                string name = _meta.TryGetValue(weaponId, out var m) ? m.Name : $"weapon {weaponId}";
                string title = VictimClass.MarkTitle(mark);
                _recorder?.Invoke("mark-earned",
                    $"weapon={weaponId} name=\"{name}\" archetype={(int)mark} title=\"{title}\"");
                // Event-key convention (BannerToast.Policy.MarkPayload's doc comment): Marks own
                // 1000 + archetype index, distinct from tiers (1..3) and milestones (negated).
                _toast?.Enqueue(weaponId, 1000 + (int)mark, BannerToast.MarkPayload(name, title));
            }
        }
        catch (Exception ex) { ModLogger.LogError("reliquary: RecordDeed failed -- " + ex.Message); }
    }

    /// <summary>A kill was credited with no captured victim snapshot -- log-only (mirrors the
    /// no-credit tap idiom, KillTracker.Corpses.cs:195): one DBG line + one flight record, never
    /// a deed. Never throws.</summary>
    public void DeedMiss(int slot)
    {
        try
        {
            ModLogger.LogDebug($"reliquary: kill credited at slot {slot} with no victim snapshot -- no deed recorded");
            _recorder?.Invoke("deed-miss", $"slot={slot}");
        }
        catch (Exception ex) { ModLogger.LogError("reliquary: DeedMiss failed -- " + ex.Message); }
    }
}
