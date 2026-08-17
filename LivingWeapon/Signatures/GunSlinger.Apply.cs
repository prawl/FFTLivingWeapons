namespace LivingWeapon;

/// <summary>
/// The write-dispatch half of GunSlinger's roster prep: given a GunSlingerPolicy decision for
/// one row, perform the actual guarded memory write (or none) and the matching plain-language
/// log line. Split out of GunSlinger.cs (PrepRoster's own orchestration -- the menu/browse gate
/// call, the reconcile wiring, the roster scan) once the owner-AC round's additions crossed that
/// file's 200-line budget. Every write here goes through WriteOffHand/WriteSupport, which are
/// themselves guarded (Writable pre-check before W16) -- no raw pointer derefs.
/// </summary>
internal sealed partial class GunSlinger
{
    private bool ApplyOffHand(long b, ushort off, ushort shield, bool mainIsGS, int mainH,
        GunSlingerSnap snap, bool menuOpen, bool inBattle, double secondsOffField)
    {
        // twin = the wielder's OWN main-hand weapon: each unit is twinned with what it actually
        // wields, never a module-global id (LW-171 -- several weapons can be flagged at once).
        var action = GunSlingerPolicy.DesiredOffHand(mainIsGS, mainH, off, shield, snap, menuOpen, inBattle, _twinIds, secondsOffField);
        switch (action)
        {
            case GunSlingerOffAction.SnapshotAndWrite:
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
            case GunSlingerOffAction.Restore:
                WriteOffHand(b, snap.OrigOff);
                snap.HasOff = false;
                ModLogger.Event(LogVerb.Signature, "The twin weapon is removed; the wielder's original off-hand gear is restored.");
                return true;
            case GunSlingerOffAction.StandDownNoWrite:
                snap.HasOff = false;
                ModLogger.Event(LogVerb.Signature, "The wielder's other hand is spoken for; the twin weapon stands down.");
                return true;
            case GunSlingerOffAction.WriteEmptyAndDrop:
                // A LEGACY real off-hand item (from before this fix shipped) can't rejoin a
                // shield-occupied hand -- restoring it would recreate the exact weapon+shield
                // "impossible state" the game itself zaps ([twin-grant-inventory-desync]). The id
                // is named in the log for support, since the mod cannot hand it back.
                ModLogger.Warn(LogVerb.Signature, $"A shield is equipped; the wielder's earlier off-hand item (id {snap.OrigOff}) could not return alongside it and was dropped. It cannot be recovered by this mod.");
                WriteOffHand(b, GunSlingerPolicy.EmptyOffHand);
                snap.HasOff = false;
                return true;
            default:   // Leave
                return false;
        }
    }

    private bool ApplySupport(long b, ushort supp, ushort off, ushort shield, bool mainIsGS,
        GunSlingerSnap snap, bool menuOpen, bool inBattle, double secondsOffField)
    {
        var action = GunSlingerPolicy.DesiredSupport(mainIsGS, supp, off, shield, snap, menuOpen, inBattle, _twinIds, secondsOffField);
        switch (action)
        {
            case GunSlingerSuppAction.SnapshotAndWrite:
                snap.OrigSupp = supp;
                snap.HasSupp  = true;
                WriteSupport(b, DualWieldKey);
                return true;
            case GunSlingerSuppAction.Write:
                WriteSupport(b, DualWieldKey);
                ModLogger.Debug(LogVerb.Signature, $"re-equipped Dual Wield; something overwrote the support slot (read {supp})");
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

    private void WriteSupport(long b, ushort value)
    {
        long addr = b + Offsets.RSupport;
        if (_mem.Writable(addr, 2)) _mem.W16(addr, value);
    }
}
