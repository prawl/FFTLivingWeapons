using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Paints the boosted WP number onto the equip card's scratch byte. Keyed by the mirror
/// weapon (the card currently on screen), NOT roster slot 0 -- roster-slot-0 keying painted
/// Ramza's boost while viewing any other unit. The ownership check (the scratch must
/// currently hold the natural or already-boosted value) is what keeps a stale or reused
/// byte from being stamped with another weapon's number.
/// </summary>
internal sealed class WpScratchPainter
{
    private readonly IGameMemory _mem;
    private readonly IReadOnlyDictionary<int, WeaponMeta> _meta;
    private readonly Func<int, int> _killsFor;

    public WpScratchPainter(IGameMemory mem, IReadOnlyDictionary<int, WeaponMeta> meta,
                            Func<int, int> killsFor)
    {
        _mem      = mem;
        _meta     = meta;
        _killsFor = killsFor;
    }

    /// <summary>Write the boosted WP onto the equip card's scratch byte, guarded: only when
    /// the scratch currently holds the natural or already-boosted value (owned by this weapon),
    /// and only when natural != boosted (no pointless write on a tier-0 weapon).</summary>
    public void Paint()
    {
        int mirrorId = _mem.U16(Offsets.MirrorWeapon);
        if (!_meta.TryGetValue(mirrorId, out var m)) return;
        if (!_mem.Readable(Offsets.WpScratch, 1)) return;

        int kills   = _killsFor(mirrorId);
        int boosted = Math.Min(255, (int)Math.Round(m.Wp * (1.0 + Tuning.Factor[Tuning.TierFor(kills)])));
        int cur     = _mem.U8(Offsets.WpScratch);

        if (cur != m.Wp && cur != boosted) return;  // not owned by this weapon
        if (boosted == cur) return;                  // already correct

        if (_mem.Writable(Offsets.WpScratch, 1))
            _mem.WriteBytes(Offsets.WpScratch, new[] { (byte)boosted });
    }
}
