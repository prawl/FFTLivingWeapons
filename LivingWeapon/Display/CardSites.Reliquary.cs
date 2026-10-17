namespace LivingWeapon;

/// <summary>

/// Reliquary repaint-through, split out of CardSites.cs to get it back under

/// the 200-line trigger. It is the EarnedAnchors story-line rotation,

/// separate from the verify/paint code; the only caller is PaintSiteWithResult.

/// </summary>

internal sealed partial class CardSites
{
    /// <summary>If a current earned line is registered for this weapon and the on-screen anchor

    /// bytes differ, overwrite them (same length, Writable-gated). Lets a site that verified

    /// against the previous anchor converge on its next paint. Never touches LegendStore.</summary>

    private void SyncFlavorToCurrent(Site s)
    {
        byte[]? current = _anchors!.CurrentFor(s.Id, s.Enc);
        if (current == null || current.Length == 0) return;
        if (!_mem.TryReadBytes(s.AnchorAddr, current.Length, out var cur)) return;
        if (ByteEq(cur, current)) return;   // skip-if-equal -- already showing the current line
        if (!_mem.Writable(s.AnchorAddr, current.Length)) return;
        _mem.WriteBytes(s.AnchorAddr, current);
        ModLogger.Debug(LogVerb.Display, $"repainted the story line to the current composed text (weapon {s.Id}, encoding {s.Enc})");
    }
}
