using System;
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
/// another unit's.
///
/// LW-193/LW-194 CONSENT MODEL (2026-08-17, replaces the old grab-and-restore design): an EMPTY
/// off-hand invites the twin; ANYTHING the player equipped there or in the shield slot
/// (RShield +0x1A, READ ONLY -- this mod never writes it) declines it, in every lane, including
/// the in-battle re-assert. The old design's raw roster writes ran a rival ledger against the
/// game's OWN inventory accounting: a player-equipped shield got DESTROYED with no refund once
/// the mod re-stamped a twin over the game's own "illegal state" cleanup, and a lawfully
/// unequipped twin got REFUNDED as a duplicate ([twin-grant-inventory-desync], PROVEN,
/// docs/LIVE_LEDGER.md). The fix routes every decision through GunSlingerPolicy's shield/off-hand
/// ownership check (see its class doc) instead of writing blind.
///
/// A world-map menu (party/equip/shop/save -- Offsets.MenuOpenFlag, read/debounced by
/// GunSlinger.MenuGate.cs) suppresses every out-of-battle twin-lane write, EXCEPT while the
/// player is looking at a BROWSE screen (the party overview root or Character Status page --
/// Offsets.PartyBrowseFlag, stable for 2 consecutive passes). OWNER-AC ROUND (2026-08-17,
/// supersedes the menu-open evaporation edge this partial used to own): the owner's acceptance
/// criteria require the twin to stay VISIBLE while the player browses Equip &amp; Abilities, so
/// the grant/re-assert is no longer suppressed away the instant a menu opens -- it re-stamps
/// within one pass of the player landing on Status or the party root (e.g. ESCing out of E&amp;A),
/// and simply holds whatever it last stamped while E&amp;A itself is open (nothing erases it
/// anymore). Safety argument for dropping evaporation: the DESTRUCTIVE lane
/// ([twin-grant-inventory-desync]) needed the mod re-stamping OVER the game's own normalize (the
/// write-vs-write wrestle); menu write-suppression alone (no re-stamping) already stops that,
/// so the game's own clears win uncontested and no impossible weapon+shield state can be built.
/// The duplication defense evaporation used to provide is now GunSlinger.Reconcile.cs's
/// watch-only detector: with the twin visible during a lawful equip action, the game can still
/// "return" it as a phantom sack refund, and per the review's ruling that lane is DETECTED AND
/// LOGGED, not auto-corrected (the owner accepted interim phantom inflation over an armed write
/// that could destroy real property on a false positive).
/// The menu gate is deliberately world-map-scoped: it reads 0 through the WHOLE formation/
/// battle-load flow, because dual-fire is welded at battle CONSTRUCTION, not held live -- a
/// battle built with an empty roster off-hand fires only ONE shot even after the twin is stamped
/// back in mid-battle ([twin-dualfire-construction-bound]). The in-battle re-assert's real job is
/// therefore keeping the twin in the ROSTER for the battle-end gear commit and the NEXT
/// construction, not enabling the CURRENT battle's dual-fire -- this replaces the pre-2026-08-17
/// doc claim that the re-assert alone made the twin "hold into combat"; that mechanism reading is
/// refuted by [twin-dualfire-construction-bound]. The underlying observation (LIVE-VERIFIED
/// 2026-07-04: the twin fires twice in battle) still holds, it was just never the re-assert doing
/// it. The battle-end TWILIGHT hold (GunSlingerPolicy.IsTwilight) is untouched by this round.
/// NOT an ISignature: nothing to reset per battle (the snapshot store is cross-session; the
/// reconcile's own last-pass memory clears on an inBattle transition instead -- see
/// GunSlinger.Reconcile.cs, review blocker 5).
///
/// Memory access: all reads/writes go through IGameMemory (RPM/WPM-backed in production).
/// Writable is pre-checked before every W16/W8. No raw pointer derefs. RShield feeds every
/// consent check in this file but is READ ONLY -- never written; the reconcile's sack reads are
/// ALSO read-only this round (watch-only, zero writes to InventoryCountBase).
/// </summary>
internal sealed partial class GunSlinger
{
    /// <summary>Dual Wield's roster picked-support ABILITY KEY (live support id 221 + 256).
    /// See GunSlingerPolicy's class doc for the low-byte-misread history (LW-168).</summary>
    internal const ushort DualWieldKey = 477;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly IGameMemory _mem;
    private readonly GunSlingerStore _store;
    private readonly HashSet<int> _twinIds;   // every gun-slinger-flagged id, cached at construction
    private readonly Action<string, string>? _recorder;   // flight tap: GunSlinger.Reconcile.cs's "twin-refund" record

    /// <summary><paramref name="recorder"/> is the flight-ring tap (production: Flight.Record,
    /// null-object-safe; tests inject a fake to observe the reconcile's "twin-refund" record --
    /// see GunSlinger.Reconcile.cs). Mirrors the Puppeteer/ActorRegister injected-recorder idiom
    /// used elsewhere in this codebase.</summary>
    public GunSlinger(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
                      string modDir, IGameMemory? mem = null, Action<string, string>? recorder = null)
    {
        _meta  = meta;
        _kills = kills;
        _mem   = mem ?? new LiveMemory();
        _store = new GunSlingerStore(modDir);
        _twinIds = ResolveTwinIds(meta);
        _recorder = recorder;
    }

    /// <summary>Test seam: expose the store so integration tests can verify snapshot state.</summary>
    internal GunSlingerStore StoreForTest() => _store;

    /// <summary>
    /// Scan roster slots 0..RosterSlots-1. For each live slot, read the shield slot (READ ONLY)
    /// and apply the gun-slinger off-hand and support consent rules (see GunSlingerPolicy's
    /// decision table). Idempotent. <paramref name="inBattle"/> still gates SnapshotAndWrite (a
    /// mid-battle roster flicker must never seed a snapshot of gear that was never real) but no
    /// longer blanket-suppresses Restore -- consent (the shield slot, the off-hand's own
    /// contents) is what decides that now, not whether a battle happens to be live. See this
    /// class's doc for the menu/browse read gate layered on top (GunSlinger.MenuGate.cs) and the
    /// watch-only refund reconcile that runs after every pass (GunSlinger.Reconcile.cs).
    /// <paramref name="secondsOffField"/> (default 0.0, "on field") is Engine's own clock --
    /// seconds since the battlefield last read on-field -- threaded straight into
    /// GunSlingerPolicy's twilight guard (TwilightHoldSeconds) so the in-battle re-assert stops
    /// re-stamping during the battle-end teardown window (owner live pass, tape
    /// lw193_watch_*.log: a re-stamp there got refunded by the post-battle gear reconcile as a
    /// mint). This method owns no clock of its own; the value is opaque plumbing here.
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
            if (nameId == 0) { ReconcileForgetRow(slot); continue; }

            var snap = _store.Get(nameId);

            int tier = Tuning.TierOf(_kills, mainH);
            bool mainIsGS = _twinIds.Contains(mainH)
                         && _meta.TryGetValue(mainH, out var m)
                         && (m.Signature?.GunSlinger ?? false)
                         && tier >= (m.Signature?.AtTier ?? 99);
            ushort shield = _mem.U16(b + Offsets.RShield);   // READ ONLY -- feeds consent, never written

            dirty |= ApplyOffHand(b, offH, shield, mainIsGS, mainH, snap, menuOpen, inBattle, secondsOffField);
            dirty |= ApplySupport(b, supp, offH, shield, mainIsGS, snap, menuOpen, inBattle, secondsOffField);

            passRows.Add((slot, nameId, mainH, offH, snap));
        }
        if (dirty) _store.Save();

        // Watch-only reconcile: compares THIS pass's raw facts against what got captured at the
        // end of last pass, using the just-built passRows for cross-row context. Zero writes;
        // see GunSlinger.Reconcile.cs's class doc (review blocker 5's lifecycle, ruling 3).
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
