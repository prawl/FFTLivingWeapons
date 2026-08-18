namespace LivingWeapon;

/// <summary>
/// CardSites' Reliquary-specific repaint-through (LW-257 commit 1's line-count response: split
/// out once CardSites.cs's own growth was measured to have left it further over the 200-line
/// trigger than the arc found it, not just crossed it -- see CardSites.cs's class doc for the
/// arithmetic). SyncFlavorToCurrent has exactly one caller (PaintSiteWithResult, CardSites.cs)
/// and is entirely about the EarnedAnchors story-line rotation (Reliquary decision 2), a concern
/// orthogonal to the verify/paint machinery CardSites.cs and CardSites.Verify.cs share -- a real
/// seam, not an artificial slice of one state machine across files.
/// </summary>
internal sealed partial class CardSites
{
    /// <summary>Reliquary decision 2's repaint-through: if a CURRENT earned line is registered
    /// for this site's weapon and the on-screen anchor bytes don't already match it, overwrite
    /// them (exact length, Writable-gated, skip-if-equal). This is what lets a site that verified
    /// via the PREVIOUS anchor (a stale-but-known line) converge to the current story on its very
    /// next paint, instead of freezing at whatever text it happened to verify against. No store
    /// writes here -- painting never touches LegendStore.</summary>
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
