namespace LivingWeapon;

/// <summary>
/// The world-map menu/browse read gate for the twin-weapon grant. Read-only. The old menu-open
/// evaporation write is gone (see GunSlinger.cs); the duplication defense is the watch-only reconcile
/// in GunSlinger.Reconcile.cs. Produces <c>menuOpen</c> for PrepRoster's call into GunSlingerPolicy rule 1.
///
/// Out-of-battle twin-lane writes are allowed only when <c>menuReadable &amp;&amp; (menuRaw == 0 ||
/// (browseReadable &amp;&amp; browseRaw == 1 &amp;&amp; browseStable))</c>. Any unreadable byte denies, and
/// <c>menuOpen</c> is the negation. In-battle lanes and the twilight hold are untouched, since rule 1
/// only suppresses when !inBattle.
/// </summary>
internal sealed partial class GunSlinger
{
    // Menu-byte debounce. A menu-open pulse must hold for 3 stable PrepRoster passes (~1s apiece; the
    // "gunslinger" tick phase runs every 30 ticks at 33ms) before suppression clears, so the town-exit
    // fade's 500ms double-pulse ([worldmap-menu-open-byte]) only delays a grant. The first read baselines
    // silently so single-call tests need no warm-up.
    private byte? _lastMenuRaw;
    private int _passesSinceFlip;

    // Browse-byte stability. The byte is unprobed on the Equip & Abilities sub-pickers, so a 1 is trusted
    // only after 2 consecutive passes, which covers a ~2s pop-in on the Status screen.
    private int _browseConsecutiveOnes;

    /// <summary>The gate output Policy's rule 1 consumes: true means SUPPRESS (deny) every
    /// out-of-battle twin-lane write this pass.</summary>
    private bool ComputeMenuOpen()
    {
        (bool menuReadable, bool menuDebouncedOpen) = ReadMenuDebounced();
        if (!menuReadable) return true;          // unreadable menu byte -- deny, regardless of browse
        if (!menuDebouncedOpen) return false;     // menu (debounced) closed -- always allowed, browse irrelevant

        // Menu is open (debounced): allowed only through a stable browse screen.
        (bool browseReadable, bool browseRawOne, bool browseStable) = ReadBrowseStable();
        bool writeAllowedViaBrowse = browseReadable && browseRawOne && browseStable;
        return !writeAllowedViaBrowse;
    }

    /// <summary>Reads Offsets.MenuOpenFlag with the 3-pass debounce. Readability is returned apart from
    /// the value. An unreadable pass leaves the debounce state untouched, and the caller denies on
    /// readability alone, so the returned open value is unused then.</summary>
    private (bool readable, bool debouncedOpen) ReadMenuDebounced()
    {
        if (!_mem.Readable(Offsets.MenuOpenFlag, 1)) return (false, true);
        byte raw = _mem.U8(Offsets.MenuOpenFlag);

        if (_lastMenuRaw == null)
        {
            // First read: baseline as settled so a fresh instance needs no warm-up passes.
            _lastMenuRaw = raw;
            _passesSinceFlip = 3;
        }
        else if (raw != _lastMenuRaw.Value)
        {
            // A flip starts the 3-pass suppression window.
            _lastMenuRaw = raw;
            _passesSinceFlip = 0;
        }
        else if (_passesSinceFlip < 3)
        {
            // Capped at 3, the only value the formula reads. An unbounded counter would eventually overflow
            // and go negative, which would keep the gate suppressed.
            _passesSinceFlip++;
        }

        return (true, raw == 1 || _passesSinceFlip < 3);
    }

    /// <summary>Reads Offsets.PartyBrowseFlag and counts consecutive passes reading 1, capped at 2. A 0
    /// or a failed read resets the count, so stability is never carried across a gap.</summary>
    private (bool readable, bool rawOne, bool stable) ReadBrowseStable()
    {
        if (!_mem.Readable(Offsets.PartyBrowseFlag, 1)) { _browseConsecutiveOnes = 0; return (false, false, false); }
        byte raw = _mem.U8(Offsets.PartyBrowseFlag);
        bool rawOne = raw == 1;
        _browseConsecutiveOnes = rawOne ? System.Math.Min(_browseConsecutiveOnes + 1, 2) : 0;
        return (true, rawOne, _browseConsecutiveOnes >= 2);
    }
}
