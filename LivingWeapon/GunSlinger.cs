using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Blaster (id 76) +3 "Gun Slinger": out-of-battle roster prep.
/// When a unit has the Blaster as their main-hand weapon and has earned tier 3,
/// writes a second Blaster into the roster off-hand slot (ROffHand +0x18, u16) and
/// Dual Wield (support 221) into the roster support slot (RSupport +0x0A, u8).
/// Both slots are snapshot+restored when the unit switches away from the Blaster.
///
/// Runs only between battles (called from Engine's !nowIn branch, throttled to ~1 s).
/// NOT an ISignature: it has no in-battle work and no ResetBattle.
///
/// Memory access: all reads/writes go through IGameMemory (RPM/WPM-backed in production).
/// Writable is pre-checked before every W16/W8. No raw pointer derefs.
/// </summary>
internal sealed class GunSlinger
{
    private const byte DualWieldId = 221;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly IGameMemory _mem;
    private readonly GunSlingerStore _store;
    private readonly int _twinId;   // the gun-slinger-flagged id, cached at construction

    public GunSlinger(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
                      string modDir, IGameMemory? mem = null)
    {
        _meta  = meta;
        _kills = kills;
        _mem   = mem ?? new LiveMemory();
        _store = new GunSlingerStore(modDir);
        _twinId = ResolveTwinId(meta);
    }

    /// <summary>Test seam: expose the store so integration tests can verify snapshot state.</summary>
    internal GunSlingerStore StoreForTest() => _store;

    /// <summary>
    /// Scan roster slots 0..RosterSlots-1. For each live slot, apply the gun-slinger
    /// off-hand and support rules. Called out of battle only; idempotent.
    /// </summary>
    public void PrepRoster()
    {
        if (_twinId == 0) return;   // no GunSlinger weapon in meta

        bool dirty = false;
        for (int slot = 0; slot < Offsets.RosterSlots; slot++)
        {
            long b = Offsets.RosterBase + slot * Offsets.RosterStride;
            byte level = _mem.U8(b + Offsets.RLevel);
            if (level == 0) continue;   // empty slot

            ushort nameId = _mem.U16(b + Offsets.RNameId);
            ushort mainH  = _mem.U16(b + Offsets.RRHand);
            ushort offH   = _mem.U16(b + Offsets.ROffHand);
            byte   supp   = _mem.U8(b + Offsets.RSupport);

            int tier = Tuning.TierOf(_kills, mainH);
            bool mainIsGS = mainH == _twinId
                         && _meta.TryGetValue(mainH, out var m)
                         && (m.Signature?.GunSlinger ?? false)
                         && tier >= (m.Signature?.AtTier ?? 99);

            var snap = _store.Get(nameId);
            dirty |= ApplyOffHand(b, offH, mainIsGS, snap);
            dirty |= ApplySupport(b, supp, mainIsGS, snap);
        }
        if (dirty) _store.Save();
    }

    private bool ApplyOffHand(long b, ushort off, bool mainIsGS, GunSlingerSnap snap)
    {
        var action = GunSlingerPolicy.DesiredOffHand(mainIsGS, _twinId, off, snap);
        switch (action)
        {
            case GunSlingerOffAction.SnapshotAndWrite:
                snap.OrigOff = off;
                snap.HasOff  = true;
                WriteOffHand(b, (ushort)_twinId);
                return true;
            case GunSlingerOffAction.Write:
                WriteOffHand(b, (ushort)_twinId);
                return false;   // snap unchanged, no persistence needed
            case GunSlingerOffAction.Restore:
                WriteOffHand(b, snap.OrigOff);
                snap.HasOff = false;
                return true;
            default:   // Leave
                return false;
        }
    }

    private bool ApplySupport(long b, byte supp, bool mainIsGS, GunSlingerSnap snap)
    {
        var action = GunSlingerPolicy.DesiredSupport(mainIsGS, supp, snap);
        switch (action)
        {
            case GunSlingerSuppAction.SnapshotAndWrite:
                snap.OrigSupp = supp;
                snap.HasSupp  = true;
                WriteSupport(b, DualWieldId);
                return true;
            case GunSlingerSuppAction.Write:
                WriteSupport(b, DualWieldId);
                return false;
            case GunSlingerSuppAction.Restore:
                WriteSupport(b, snap.OrigSupp);
                snap.HasSupp = false;
                return true;
            default:   // Leave
                return false;
        }
    }

    private void WriteOffHand(long b, ushort value)
    {
        long addr = b + Offsets.ROffHand;
        if (_mem.Writable(addr, 2)) _mem.W16(addr, value);
    }

    private void WriteSupport(long b, byte value)
    {
        long addr = b + Offsets.RSupport;
        if (_mem.Writable(addr, 1)) _mem.W8(addr, value);
    }

    private static int ResolveTwinId(Dictionary<int, WeaponMeta> meta)
    {
        foreach (var kv in meta)
            if (kv.Value.Signature?.GunSlinger == true) return kv.Key;
        return 0;
    }
}
