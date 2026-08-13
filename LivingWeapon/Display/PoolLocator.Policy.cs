using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure byte[] pool-region signature test for PoolLocator (LW-37). No IGameMemory, no new
/// byte matching: CardScanner.FindKills is reused verbatim, the same forward/backward
/// attribution the paint path already trusts. A buffer "is pool" when it holds at least one
/// FULLY BAKED entry: a "Kills: " literal tied to its owner's flavor (CardScanner's
/// bidirectional attribution) AND the owner weapon's NAME within NameWindow of the hit. The
/// name gate is load-bearing (confirmed live 2026-07-08): the stable pool lays name -> keys ->
/// "Kills: " -> flavor, but a transient FText/widget render copy carries the flavor + Kills
/// with NO adjacent name. Without the name gate, ~26 lower-addressed render copies attribute
/// the same weapon ids as the pool and win PoolLocator's first-wins distinct-weapon tiebreak,
/// so the sweep is retired while the real pool never paints. Preferring a buffer that
/// attributes MULTIPLE distinct weapon ids is still the CALLER's job (PoolLocator), not this one.
/// </summary>
internal static class PoolLocatorPolicy
{
    /// <summary>How far from a "Kills: " hit the owner weapon's baked NAME must appear for the hit
    /// to count as a pool entry. The pool lays the name a few dozen bytes ahead of the Kills line;
    /// a transient render copy has no name at all. Confirmed live 2026-07-08: the descriptor region
    /// carries no name within 0x200 of its Kills line, the pool has it at ~0x40. 512 bytes clears
    /// both encodings' name+key preamble with margin while staying far tighter than any real
    /// inter-region distance, so a name is only ever matched inside the same baked entry.</summary>
    internal const int NameWindow = 512;

    /// <summary>One buffer window's scan result: whether it qualifies as a pool candidate at
    /// all (IsPool), how many distinct weapon ids it attributed (DistinctWeaponCount), and the
    /// raw hits (Hits), SlotPos/FlavorPos already computed by CardScanner.FindKills from
    /// pats.Kills(enc).Length and the meter width, never hardcoded here.</summary>
    internal readonly record struct ScanResult(bool IsPool, int DistinctWeaponCount,
                                                IReadOnlyList<CardScanner.KillsHit> Hits);

    /// <summary>Scan one buffer window. Mirrors CardScanner.FindKills' own lookback/searchable
    /// contract exactly, so a chunked region read (ChunkReader.ReadInRegion) plugs straight in
    /// without any translation. Only hits whose owner name sits within NameWindow count (baked
    /// pool entries); a name-less flavor+Kills hit is a transient render copy and is dropped.</summary>
    internal static ScanResult Scan(byte[] buf, int lookback, int searchable, CardPatterns pats)
    {
        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback, searchable, pats, hits);

        var ids = new HashSet<int>();
        var baked = new List<CardScanner.KillsHit>();
        var nameHits = new List<int>();
        foreach (var h in hits)
        {
            if (!pats.TryGet(h.Id, h.Enc, out var pat) || pat.Name.Length == 0) continue;
            int lo = h.SlotPos - NameWindow; if (lo < 0) lo = 0;
            int hi = h.SlotPos + NameWindow; if (hi > buf.Length) hi = buf.Length;
            nameHits.Clear();
            ByteScan.FindAll(buf, pat.Name, lo, hi, nameHits);
            if (nameHits.Count == 0) continue;   // flavor + Kills but NO adjacent name: a transient render copy, not the baked pool
            baked.Add(h);
            ids.Add(h.Id);
        }

        return new ScanResult(baked.Count > 0, ids.Count, baked);
    }

    /// <summary>SlotAddr (buffer-relative): the meter body's first byte, right after the
    /// "Kills: " literal.</summary>
    internal static int SlotOffset(CardScanner.KillsHit hit) => hit.SlotPos;

    /// <summary>AnchorAddr (buffer-relative): the owner weapon's flavor text, wherever
    /// CardScanner's bidirectional search found it (before or after the Kills hit).</summary>
    internal static int AnchorOffset(CardScanner.KillsHit hit) => hit.FlavorPos;
}
