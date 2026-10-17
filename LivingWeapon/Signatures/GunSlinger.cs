using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Gun Slinger family. Roster prep + hold for every weapon flagged `gunSlinger` in meta.json
/// (Outrider Pistol id 71, Arbalest id 79 "Crossfire" LW-171). When the main hand holds a flagged
/// weapon at its earned tier, a second copy of that same weapon goes into the roster off-hand slot
/// (ROffHand +0x18, u16) and Dual Wield (key 477, RSupport +0x0A) into the support slot, so Attack
/// fires twice. Consent (LW-193/LW-194, 2026-08-17): an empty off-hand invites the twin, anything
/// equipped there or in the shield slot (RShield +0x1A, read only) declines it, in every lane. Blind
/// roster writes used to fight the game's inventory accounting. A shield was destroyed with no refund
/// and an unequipped twin was refunded as a duplicate ([twin-grant-inventory-desync],
/// docs/LIVE_LEDGER.md). Decisions go through GunSlingerPolicy's ownership check.
///
/// A world-map menu (Offsets.MenuOpenFlag, GunSlinger.MenuGate.cs) suppresses out-of-battle writes
/// except on the party overview or Character Status page (Offsets.PartyBrowseFlag, stable for 2
/// passes), so the twin stays visible while browsing (2026-08-17 acceptance round) and re-stamps
/// within one pass. Phantom refunds from a lawful equip are detected and logged, not corrected
/// (GunSlinger.Reconcile.cs). Dual fire is welded at battle construction, so the in-battle re-assert
/// only keeps the twin in the roster for the battle-end commit and the next battle
/// ([twin-dualfire-construction-bound]). The battle-end twilight hold is GunSlingerPolicy.IsTwilight.
/// Not an ISignature. The snapshot store is cross-session and the reconcile clears on an inBattle flip.
/// All memory goes through IGameMemory with Writable checked before every write.
/// </summary>
internal sealed partial class GunSlinger
{
    /// <summary>Dual Wield's roster picked-support ability key (support id 221 + 256). See
    /// GunSlingerPolicy's class doc for the low-byte misread.</summary>
    internal const ushort DualWieldKey = 477;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly IGameMemory _mem;
    private readonly GunSlingerStore _store;
    private readonly HashSet<int> _twinIds;   // every gun-slinger-flagged id, cached at construction
    private readonly Action<string, string>? _recorder;   // flight tap. GunSlinger.Reconcile.cs's "twin-refund" record
    private readonly Func<long> _bagCountBase;   // LW-368 round 2. GunSlinger.Reconcile.cs's sack reads

    /// <summary><paramref name="recorder"/> is the flight-ring tap (Flight.Record in production, a fake
    /// in tests) for the reconcile's "twin-refund" record. <paramref name="bagCountBase"/> is a Func
    /// because Engine builds GunSlinger before <c>ExtendedInventory.BagCountBase</c> is known (LW-368
    /// round 2). It defaults to the vanilla block.</summary>
    public GunSlinger(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
                      string modDir, IGameMemory? mem = null, Action<string, string>? recorder = null,
                      Func<long>? bagCountBase = null)
    {
        _meta = meta;
        _kills = kills;
        _mem = mem ?? new LiveMemory();
        _store = new GunSlingerStore(modDir);
        _twinIds = ResolveTwinIds(meta);
        _recorder = recorder;
        _bagCountBase = bagCountBase ?? (() => Offsets.InventoryCountBase);
    }

    /// <summary>Test seam. Expose the store so integration tests can verify snapshot state.</summary>
    internal GunSlingerStore StoreForTest() => _store;

    /// <summary>
    /// Scan roster slots 0..RosterSlots-1. For each live slot, read the shield slot (read only) and
    /// apply the off-hand and support consent rules (see GunSlingerPolicy's decision table).
    /// Idempotent. <paramref name="inBattle"/> gates SnapshotAndWrite so a mid-battle roster flicker
    /// cannot seed a snapshot of gear that was never real. The menu/browse gate is in
    /// GunSlinger.MenuGate.cs and the watch-only refund reconcile in GunSlinger.Reconcile.cs.
    /// <paramref name="secondsOffField"/> is Engine's seconds since the battlefield last read on-field,
    /// passed to GunSlingerPolicy's twilight guard (TwilightHoldSeconds) so the re-assert stops during
    /// battle-end teardown (a re-stamp there was refunded as a mint; tape lw193_watch_*.log).
    /// </summary>
    public void PrepRoster(bool inBattle = false, double secondsOffField = 0.0)
    {
        if (_twinIds.Count == 0) return;   // no GunSlinger weapon in meta

        bool menuOpen = ComputeMenuOpen();
        Dictionary<int, byte>? currentSack = ReconcilePrepare(inBattle);   // handles the inBattle-flip lifecycle clear too

        bool dirty = false;
        var passRows = new List<(int slot, ushort nameId, ushort mainH, ushort offH, GunSlingerSnap snap)>();
        for (int slot = 0; slot < Offsets.RosterSlots; slot++)
        {
            long b = Offsets.RosterBase + slot * Offsets.RosterStride;
            byte level = _mem.U8(b + Offsets.RLevel);
            if (level == 0) { ReconcileForgetRow(slot); continue; }   // empty slot

            ushort nameId = _mem.U16(b + Offsets.RNameId);
            ushort mainH = _mem.U16(b + Offsets.RRHand);
            ushort offH = _mem.U16(b + Offsets.ROffHand);
            ushort supp = _mem.U16(b + Offsets.RSupport);

            // GunSlingerStore.Get creates on read and keys by nameId, so two unseeded rows
            // (nameId 0) would share one snapshot and a restore could cross-write gear into another
            // unit's roster row. Skipping is safe. Never seen on a real save, and the next tick re-reads.
            if (nameId == 0) { ReconcileForgetRow(slot); continue; }

            var snap = _store.Get(nameId);

            int tier = Tuning.TierOf(_kills, mainH);
            bool mainIsGS = _twinIds.Contains(mainH)
                         && _meta.TryGetValue(mainH, out var m)
                         && (m.Signature?.GunSlinger ?? false)
                         && tier >= (m.Signature?.AtTier ?? 99);
            ushort shield = _mem.U16(b + Offsets.RShield);   // READ only -- feeds consent, never written

            dirty |= ApplyOffHand(b, offH, shield, mainIsGS, mainH, snap, menuOpen, inBattle, secondsOffField);
            dirty |= ApplySupport(b, supp, offH, shield, mainIsGS, snap, menuOpen, inBattle, secondsOffField);

            passRows.Add((slot, nameId, mainH, offH, snap));
        }
        if (dirty) _store.Save();

        // Watch-only reconcile. Compares this pass's facts with last pass's. Zero writes
        // (see GunSlinger.Reconcile.cs).
        foreach (var row in passRows)
            ReconcileRow(row.slot, row.nameId, row.mainH, row.offH, row.snap, currentSack, passRows, inBattle);
        ReconcileCommitSack(currentSack, inBattle);
    }

    private static HashSet<int> ResolveTwinIds(Dictionary<int, WeaponMeta> meta)
    {
        var ids = new HashSet<int>();
        foreach (var kv in meta)
            if (kv.Value.Signature?.GunSlinger == true) ids.Add(kv.Key);
        return ids;
    }
}
