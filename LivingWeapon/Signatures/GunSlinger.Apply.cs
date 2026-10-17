namespace LivingWeapon;

/// <summary>
/// Applies a GunSlingerPolicy decision for one roster row. The guarded write, if any, and its log
/// line. Split from GunSlinger.cs to stay under the 200-line trigger.
/// </summary>
internal sealed partial class GunSlinger
{
    private bool ApplyOffHand(long b, ushort off, ushort shield, bool mainIsGS, int mainH,
        GunSlingerSnap snap, bool menuOpen, bool inBattle, double secondsOffField)
    {
        // twin = the wielder's own main-hand weapon, so each unit twins with what it wields (LW-171:
        // several weapons can be flagged at once).
        var action = GunSlingerPolicy.DesiredOffHand(mainIsGS, mainH, off, shield, snap, menuOpen, inBattle, _twinIds, secondsOffField);
        switch (action)
        {
            case GunSlingerOffAction.SnapshotAndWrite:
                snap.OrigOff = off;
                snap.HasOff = true;
                WriteOffHand(b, (ushort)mainH);
                ModLogger.Event(LogVerb.Signature, "A twin weapon is equipped in the wielder's off-hand; their original gear is remembered and returns when the weapon comes off.");
                return true;
            case GunSlingerOffAction.Write:
                WriteOffHand(b, (ushort)mainH);
                // Reached only when something rewrote the slot; the logged value hints at who (EMPTY means an
                // equip screen normalized the twin away).
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
                // A legacy off-hand item can't rejoin a shield-occupied hand. Restoring it would recreate the
                // weapon+shield state the game zaps ([twin-grant-inventory-desync]). The id is logged for support.
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
                snap.HasSupp = true;
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
