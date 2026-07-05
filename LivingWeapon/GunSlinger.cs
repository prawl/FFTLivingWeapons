using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Outrider Pistol (id 71) +3 "Gun Slinger": roster prep + hold.
/// When a unit has the gun-slinger pistol as their main-hand weapon and has earned tier 3,
/// writes a second pistol into the roster off-hand slot (ROffHand +0x18, u16) and
/// Dual Wield (support 221) into the roster support slot (RSupport +0x0A, u8) -- so the unit
/// dual-wields and Attack fires twice. Both slots are snapshot+restored when the unit switches
/// away from the pistol.
///
/// Runs every ~1 s: world map, formation, AND in battle (Engine, Barrage's precedent). It
/// originally ran only between battles and the twin did not hold into combat; the in-battle
/// re-assert (below) fixes that. LIVE-VERIFIED 2026-07-04: the twin fires twice in battle.
/// The <paramref name="inBattle"/> flag makes the in-battle pass RE-ASSERT-ONLY -- see PrepRoster.
/// NOT an ISignature: nothing to reset per battle (the snapshot store is cross-session).
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
    /// off-hand and support rules. Idempotent.
    /// <paramref name="inBattle"/> RE-ASSERT-ONLY GUARD (2026-07-04): when true (a live battle
    /// frame), only the Write re-assert action is honored -- SnapshotAndWrite and Restore are
    /// suppressed to Leave. A mid-battle roster read that flickered could otherwise snapshot
    /// garbage as the player's "original gear" (persisted to <see cref="GunSlingerStore"/>) or
    /// restore over a legitimately-injected twin. Fresh snapshot/restore happen only out of
    /// battle, where equipment legitimately changes; in battle we can only re-write a twin we
    /// already own (snap.HasOff), never touch the store or the player's real gear.
    /// </summary>
    public void PrepRoster(bool inBattle = false)
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
            dirty |= ApplyOffHand(b, offH, mainIsGS, snap, inBattle);
            dirty |= ApplySupport(b, supp, mainIsGS, snap, inBattle);
        }
        if (dirty) _store.Save();
    }

    private bool ApplyOffHand(long b, ushort off, bool mainIsGS, GunSlingerSnap snap, bool inBattle)
    {
        var action = GunSlingerPolicy.DesiredOffHand(mainIsGS, _twinId, off, snap);
        switch (action)
        {
            case GunSlingerOffAction.SnapshotAndWrite when !inBattle:
                snap.OrigOff = off;
                snap.HasOff  = true;
                WriteOffHand(b, (ushort)_twinId);
                ModLogger.Event(LogVerb.Signature, "A twin pistol is equipped in the wielder's off-hand; their original gear is remembered and returns when the pistol comes off.");
                return true;
            case GunSlingerOffAction.Write:
                WriteOffHand(b, (ushort)_twinId);
                // The re-assert IS the clobber instrument: this branch only runs when something
                // rewrote the slot out from under the hold -- the logged value names the culprit's
                // leftovers (an EMPTY sentinel = an equip screen normalized the "illegal" twin away).
                ModLogger.Debug(LogVerb.Signature, $"re-equipped the twin pistol; something overwrote the off-hand slot (read {off})");
                return false;   // snap unchanged, no persistence needed
            case GunSlingerOffAction.Restore when !inBattle:
                WriteOffHand(b, snap.OrigOff);
                snap.HasOff = false;
                ModLogger.Event(LogVerb.Signature, "The twin pistol is removed; the wielder's original off-hand gear is restored.");
                return true;
            default:   // Leave, or a SnapshotAndWrite/Restore suppressed in battle
                return false;
        }
    }

    private bool ApplySupport(long b, byte supp, bool mainIsGS, GunSlingerSnap snap, bool inBattle)
    {
        var action = GunSlingerPolicy.DesiredSupport(mainIsGS, supp, snap);
        switch (action)
        {
            case GunSlingerSuppAction.SnapshotAndWrite when !inBattle:
                snap.OrigSupp = supp;
                snap.HasSupp  = true;
                WriteSupport(b, DualWieldId);
                return true;
            case GunSlingerSuppAction.Write:
                WriteSupport(b, DualWieldId);
                ModLogger.Debug(LogVerb.Signature, $"re-equipped Dual Wield; something overwrote the support slot (read {supp})");
                return false;
            case GunSlingerSuppAction.Restore when !inBattle:
                WriteSupport(b, snap.OrigSupp);
                snap.HasSupp = false;
                return true;
            default:   // Leave, or a SnapshotAndWrite/Restore suppressed in battle
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
