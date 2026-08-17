using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Gun Slinger family: roster prep + hold for every weapon flagged `gunSlinger` in meta.json.
/// Outrider Pistol (id 71) "Gun Slinger" was the sole flagged weapon; Arbalest (id 79)
/// "Crossfire" (LW-171) is the second. When a unit's main-hand weapon is ANY flagged weapon
/// and the unit has earned that weapon's own tier, the mod writes a second copy of THAT SAME
/// weapon into the roster off-hand slot (ROffHand +0x18, u16) and Dual Wield (support Key 477,
/// RSupport +0x0A, u16 ability Key) into the roster support slot -- so the unit dual-wields and
/// Attack fires twice. Each wielder always receives a twin of their OWN main-hand weapon, never
/// another unit's. Both slots are snapshot+restored when the unit switches away from every
/// flagged weapon.
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
    /// <summary>Dual Wield's roster picked-support ABILITY KEY (live support id 221 + 256).
    /// See GunSlingerPolicy's class doc for the low-byte-misread history (LW-168).</summary>
    internal const ushort DualWieldKey = 477;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly IGameMemory _mem;
    private readonly GunSlingerStore _store;
    private readonly HashSet<int> _twinIds;   // every gun-slinger-flagged id, cached at construction

    public GunSlinger(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
                      string modDir, IGameMemory? mem = null)
    {
        _meta  = meta;
        _kills = kills;
        _mem   = mem ?? new LiveMemory();
        _store = new GunSlingerStore(modDir);
        _twinIds = ResolveTwinIds(meta);
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
        if (_twinIds.Count == 0) return;   // no GunSlinger weapon in meta

        bool dirty = false;
        for (int slot = 0; slot < Offsets.RosterSlots; slot++)
        {
            long b = Offsets.RosterBase + slot * Offsets.RosterStride;
            byte level = _mem.U8(b + Offsets.RLevel);
            if (level == 0) continue;   // empty slot

            ushort nameId = _mem.U16(b + Offsets.RNameId);
            ushort mainH  = _mem.U16(b + Offsets.RRHand);
            ushort offH   = _mem.U16(b + Offsets.ROffHand);
            ushort supp   = _mem.U16(b + Offsets.RSupport);

            // LW-252 decision-4 exception (a): GunSlingerStore.Get CREATES on read and keys
            // snapshots by nameId, so two unseeded rows (nameId 0 -- a Mem fail-safe transient,
            // or a genuinely stale read) would share ONE snapshot: whichever row's
            // SnapshotAndWrite ran first captures ITS original gear into the shared object, and
            // restoring it later cross-writes that gear into a DIFFERENT unit's save-persistent
            // roster row. Skipping is safe: never observed live (real saves' nameIds are all
            // seeded, per this session's probe) and self-healing (the next tick's read is a
            // fresh Mem sample, so a genuinely transient 0 clears itself without ever having
            // touched the store).
            if (nameId == 0) continue;

            int tier = Tuning.TierOf(_kills, mainH);
            bool mainIsGS = _twinIds.Contains(mainH)
                         && _meta.TryGetValue(mainH, out var m)
                         && (m.Signature?.GunSlinger ?? false)
                         && tier >= (m.Signature?.AtTier ?? 99);

            var snap = _store.Get(nameId);
            dirty |= ApplyOffHand(b, offH, mainIsGS, mainH, snap, inBattle);
            dirty |= ApplySupport(b, supp, mainIsGS, snap, inBattle);
        }
        if (dirty) _store.Save();
    }

    private bool ApplyOffHand(long b, ushort off, bool mainIsGS, int mainH, GunSlingerSnap snap, bool inBattle)
    {
        // twin = the wielder's OWN main-hand weapon: each unit is twinned with what it actually
        // wields, never a module-global id (LW-171 -- several weapons can be flagged at once).
        var action = GunSlingerPolicy.DesiredOffHand(mainIsGS, mainH, off, snap);
        switch (action)
        {
            case GunSlingerOffAction.SnapshotAndWrite when !inBattle:
                snap.OrigOff = off;
                snap.HasOff  = true;
                WriteOffHand(b, (ushort)mainH);
                ModLogger.Event(LogVerb.Signature, "A twin weapon is equipped in the wielder's off-hand; their original gear is remembered and returns when the weapon comes off.");
                return true;
            case GunSlingerOffAction.Write:
                WriteOffHand(b, (ushort)mainH);
                // The re-assert IS the clobber instrument: this branch only runs when something
                // rewrote the slot out from under the hold -- the logged value names the culprit's
                // leftovers (an EMPTY sentinel = an equip screen normalized the "illegal" twin away).
                ModLogger.Debug(LogVerb.Signature, $"re-equipped the twin weapon; something overwrote the off-hand slot (read {off})");
                return false;   // snap unchanged, no persistence needed
            case GunSlingerOffAction.Restore when !inBattle:
                WriteOffHand(b, snap.OrigOff);
                snap.HasOff = false;
                ModLogger.Event(LogVerb.Signature, "The twin weapon is removed; the wielder's original off-hand gear is restored.");
                return true;
            default:   // Leave, or a SnapshotAndWrite/Restore suppressed in battle
                return false;
        }
    }

    private bool ApplySupport(long b, ushort supp, bool mainIsGS, GunSlingerSnap snap, bool inBattle)
    {
        var action = GunSlingerPolicy.DesiredSupport(mainIsGS, supp, snap);
        switch (action)
        {
            case GunSlingerSuppAction.SnapshotAndWrite when !inBattle:
                snap.OrigSupp = supp;
                snap.HasSupp  = true;
                WriteSupport(b, DualWieldKey);
                return true;
            case GunSlingerSuppAction.Write:
                WriteSupport(b, DualWieldKey);
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

    private void WriteSupport(long b, ushort value)
    {
        long addr = b + Offsets.RSupport;
        if (_mem.Writable(addr, 2)) _mem.W16(addr, value);
    }

    private static HashSet<int> ResolveTwinIds(Dictionary<int, WeaponMeta> meta)
    {
        var ids = new HashSet<int>();
        foreach (var kv in meta)
            if (kv.Value.Signature?.GunSlinger == true) ids.Add(kv.Key);
        return ids;
    }
}
