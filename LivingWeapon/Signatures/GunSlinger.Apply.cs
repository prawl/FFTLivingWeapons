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
        // twin = the wielder's own main-hand weapon, so each unit twins with what it wields
        // (several weapons can be flagged at once).
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
                // Reached only when something rewrote the slot; the logged value hints at who (empty means an
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
                ModLogger.Event(LogVerb.Signature, StandDownLine(off, shield, mainH, snap.HasSupp));
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

    /// <summary>The stand down line in the player's voice, one shape per reason. A player who filled the
    /// hand themselves still dual wields on the game's own rule, so the old wording read as refusing something
    /// this mod never controlled. ApplySupport hands a lent Dual Wield back the same pass, so the tail can say so.</summary>
    internal static string StandDownLine(ushort off, ushort shield, int mainH, bool lentDualWield)
    {
        string tail = $"(detail: off hand {off}, shield {shield}, main hand item {mainH})";
        string lent = lentDualWield ? "the Dual Wield this mod lent them goes back too" : "this mod lent them no Dual Wield";
        if (!GunSlingerPolicy.IsEmptyShield(shield))
            return $"A shield is equipped, so the twin weapon trick stays out of the way for this unit; {lent} {tail}";
        if (GunSlingerPolicy.IsEmptyOff(off))
            return $"The twin weapon has left the off hand and nothing of yours was there, so the twin weapon trick lets go of this unit; {lent} {tail}";
        string swing = lentDualWield
            ? "the Dual Wield this mod lent them goes back too, so whether they swing twice is now the game's call"
            : "the Dual Wield they carry is your own pick, so the game's own dual wield still swings both";
        return $"You already carry a second weapon in that off hand, so the twin weapon trick stays out of the way; {swing} {tail}";
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
