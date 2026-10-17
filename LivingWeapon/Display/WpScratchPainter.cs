using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Paints boosted WP onto the equip card scratch byte, keyed by the mirror weapon on screen
/// (slot 0 keying painted Ramza's boost on every unit).</summary>
internal sealed class WpScratchPainter
{
    private readonly IGameMemory _mem;
    private readonly IReadOnlyDictionary<int, WeaponMeta> _meta;
    private readonly Func<int, int> _killsFor;

    public WpScratchPainter(IGameMemory mem, IReadOnlyDictionary<int, WeaponMeta> meta,
                            Func<int, int> killsFor)
    {
        _mem = mem;
        _meta = meta;
        _killsFor = killsFor;
    }

    /// <summary>Writes the boosted WP only when the scratch holds the natural or boosted value
    /// and they differ.</summary>
    public void Paint()
    {
        int mirrorId = _mem.U16(Offsets.MirrorWeapon);
        if (!_meta.TryGetValue(mirrorId, out var m)) return;
        if (!_mem.Readable(Offsets.WpScratch, 1)) return;

        int kills = _killsFor(mirrorId);
        int boosted = Math.Min(255, (int)Math.Round(m.Wp * (1.0 + Tuning.Factor[Tuning.TierFor(kills)])));
        int cur = _mem.U8(Offsets.WpScratch);

        if (cur != m.Wp && cur != boosted) return;  // not owned by this weapon
        if (boosted == cur) return;                  // already correct

        if (_mem.Writable(Offsets.WpScratch, 1))
            _mem.WriteBytes(Offsets.WpScratch, new[] { (byte)boosted });
    }
}
