namespace LivingWeapon;

/// <summary>
/// LW-193 owner-AC round: the world-map menu/browse read gate for the twin-weapon grant --
/// read-only (the menu-open evaporation write this partial used to own was REMOVED this round;
/// see GunSlinger.cs's class doc for why -- the twin now stays visibly stamped through every
/// menu, including Equip &amp; Abilities, and the duplication defense moved to the watch-only
/// reconcile in GunSlinger.Reconcile.cs). This file computes ONE output, <c>menuOpen</c>, that
/// GunSlinger.cs's PrepRoster feeds into GunSlingerPolicy's rule 1.
///
/// The gate (review blocker 4's corrected expression -- readability is a SEPARATE input, never
/// folded into the raw byte): out-of-battle twin-lane writes are allowed only when
/// <c>menuReadable &amp;&amp; (menuRaw == 0 || (browseReadable &amp;&amp; browseRaw == 1 &amp;&amp;
/// browseStable))</c>. Any unreadable byte denies -- including the double fail-safe case (menu
/// byte unreadable denies even when the browse byte happens to read 1). <c>menuOpen</c> is the
/// gate's negation. In-battle lanes and the twilight hold (GunSlingerPolicy.IsTwilight) are
/// untouched by any of this -- Policy's rule 1 already scopes the suppression to !inBattle.
/// </summary>
internal sealed partial class GunSlinger
{
    // Menu-byte debounce state (UNCHANGED mechanism from the pre-AC round): a menu-open pulse
    // must survive 3 stable PrepRoster passes (~1s cadence, Engine's "gunslinger" tick phase
    // runs every 30 ticks @ 33ms) before the SUPPRESSED state clears, so a single-pass flicker
    // (the town-exit fade's observed 500ms double-pulse, [worldmap-menu-open-byte]) only DELAYS
    // a grant rather than letting a spurious blip through. _lastMenuRaw starts unset so the
    // FIRST ever read baselines silently with no artificial suppression (every single-call test
    // in the suite relies on this cold-start behavior; mirrors Engine.cs's own "first sighting
    // baselines silently" convention for _lastMode).
    private byte? _lastMenuRaw;
    private int _passesSinceFlip;

    // Browse-byte stability state (new this round, review's debounce ruling): the byte is
    // UNPROBED on E&A's own sub-pickers, so a lone 1-reading is trusted only after it holds for
    // 2 CONSECUTIVE passes (guards against a max ~2s pop-in on the Status screen reading through
    // as a false grant-trigger before the game's own UI has settled).
    private int _browseConsecutiveOnes;

    /// <summary>The gate output Policy's rule 1 consumes: true means SUPPRESS (deny) every
    /// out-of-battle twin-lane write this pass.</summary>
    private bool ComputeMenuOpen()
    {
        (bool menuReadable, bool menuDebouncedOpen) = ReadMenuDebounced();
        if (!menuReadable) return true;          // unreadable menu byte -- deny, regardless of browse
        if (!menuDebouncedOpen) return false;     // menu (debounced) closed -- always allowed, browse irrelevant

        // Menu is open (debounced): allowed ONLY through a stable browse screen.
        (bool browseReadable, bool browseRawOne, bool browseStable) = ReadBrowseStable();
        bool writeAllowedViaBrowse = browseReadable && browseRawOne && browseStable;
        return !writeAllowedViaBrowse;
    }

    /// <summary>Reads Offsets.MenuOpenFlag and runs the SAME 3-pass debounce as before, but
    /// returns readability as an explicit separate output instead of folding an unreadable read
    /// into the raw value -- review blocker 4's restructuring requirement. On an unreadable pass
    /// the debounce state is left untouched (frozen, not perturbed by a guess); the returned
    /// debouncedOpen value is never consulted by the caller in that case (it denies on
    /// readability alone), so its exact value here is immaterial.</summary>
    private (bool readable, bool debouncedOpen) ReadMenuDebounced()
    {
        if (!_mem.Readable(Offsets.MenuOpenFlag, 1)) return (false, true);
        byte raw = _mem.U8(Offsets.MenuOpenFlag);

        if (_lastMenuRaw == null)
        {
            // Cold start (no prior observation at all): baseline SETTLED, not flipped -- a fresh
            // GunSlinger instance must not need three warm-up passes before its first real
            // grant/restore can land, and every single-call test in the suite relies on this.
            _lastMenuRaw = raw;
            _passesSinceFlip = 3;
        }
        else if (raw != _lastMenuRaw.Value)
        {
            // A REAL flip starts the 3-pass suppression window.
            _lastMenuRaw = raw;
            _passesSinceFlip = 0;
        }
        else if (_passesSinceFlip < 3)
        {
            // Capped, not just "large": an unbounded counter that kept incrementing every pass
            // for the rest of the session would eventually overflow int and wrap negative, which
            // would satisfy "< 3" forever and jam the gate suppressed. 3 is all the formula below
            // ever consults, so growth stops there.
            _passesSinceFlip++;
        }

        return (true, raw == 1 || _passesSinceFlip < 3);
    }

    /// <summary>Reads Offsets.PartyBrowseFlag and tracks how many CONSECUTIVE passes it has read
    /// 1. Any 0 reading, or a failed read, resets the counter to 0 -- stability must be
    /// re-established fresh, never carried across a gap. Returns readability, the raw 1-vs-not
    /// reading, and whether it has now held for 2 consecutive passes (the review's debounce
    /// ruling).</summary>
    private (bool readable, bool rawOne, bool stable) ReadBrowseStable()
    {
        if (!_mem.Readable(Offsets.PartyBrowseFlag, 1)) { _browseConsecutiveOnes = 0; return (false, false, false); }
        byte raw = _mem.U8(Offsets.PartyBrowseFlag);
        bool rawOne = raw == 1;
        _browseConsecutiveOnes = rawOne ? System.Math.Min(_browseConsecutiveOnes + 1, 2) : 0;
        return (true, rawOne, _browseConsecutiveOnes >= 2);
    }
}
