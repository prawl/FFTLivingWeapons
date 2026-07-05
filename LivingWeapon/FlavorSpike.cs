#if LWDEV
using System;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// DEV-ONLY spike (ShowSpike precedent, same #if LWDEV whole-file wrap): the P4 render probe
/// (docs/RELIQUARY_AC.md P4). Proves the equip card's flavor line renders from the very buffers
/// Display/CardSites already paint, by overwriting ONE weapon's cached flavor-anchor bytes with a
/// same-length ASCII test string, in BOTH text encodings the painter already handles (the 8-bit
/// bake and the UTF-16LE copy).
///
/// Key: F6 (one-shot arm). History: F7 was specced first but is LL-hook eaten per ShowSpike.cs's
/// key doc. F2 was tried next and looked dead 2026-07-05, but was EXONERATED the same day: the
/// Tick call sat on Engine's in-battle path only, so the key was never polled where the equip
/// card lives (out-of-battle menus) -- F2's true status is untested-through-a-reachable-site.
/// F6 kept per owner directive (known-live). DELIBERATE COLLISION: in battle, F6 also arms
/// ShowSpike's prompt-swap one-shot -- one press fires BOTH spikes. Accepted for this dev-only
/// probe; the stray test payload on the next facing prompt is harmless and self-identifying.
///
/// RENDER-ONLY, accepted cost: the target weapon's Kills sites are EXPECTED to die for the rest
/// of the session. A kills site's ownership anchor IS the flavor text itself (CardSites.AnchorIsLive
/// re-reads the anchor and byte-compares it against the BAKED pattern) -- once this probe
/// overwrites that text, every cached site for the weapon fails re-verification and gets evicted
/// by the next PruneDeadSites/PaintAll pass, freezing that weapon's Kills counter until the next
/// sweep re-discovers it (which it won't, since the anchor no longer matches the baked pattern
/// either). The Phase 1 painter change (three-way anchor: baked OR earned OR last-painted line,
/// RELIQUARY_AC.md Display section) is what fixes this properly; P4 only proves the render works
/// at all.
///
/// PASS: the target weapon's card shows the probe text in whichever encoding is on screen, across
/// open/close (>=5x) and scrolling (>=3 weapons) and one battle enter/exit, with no crash and no
/// other weapon's Kills slot disturbed. FAIL: the text never changes, or any OTHER weapon's slot
/// is corrupted or mis-sized.
/// </summary>
internal sealed class FlavorSpike
{
    private const int VkF6 = 0x75;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly IGameMemory _mem;
    private readonly CardSites _sites;
    private readonly CardPatterns _pats;
    private bool _f6Was;

    public FlavorSpike(IGameMemory mem, CardSites sites, CardPatterns pats)
    {
        _mem = mem;
        _sites = sites;
        _pats = pats;
    }

    /// <summary>Loop-thread tick: F6 edge-detect -> OneShot (ShowSpike's Pressed idiom).</summary>
    public void Tick()
    {
        if (Pressed(VkF6, ref _f6Was))
            OneShot();
    }

    private static bool Pressed(int vk, ref bool was)
    {
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        bool pressed = down && !was;
        was = down;
        return pressed;
    }

    /// <summary>Snapshot the cached sites, pick the lowest-id weapon with a kills site, and
    /// overwrite every one of its kills-site flavor anchors with a same-length probe string.
    /// Never throws.</summary>
    private void OneShot()
    {
        try
        {
            var snapshot = _sites.Snapshot();
            int target = FlavorProbeText.TargetWeapon(snapshot);
            if (target == 0)
            {
                ModLogger.Log("flavor-spike: no kills sites cached yet -- open the equip card so the sweep finds sites, then press F6 again");
                return;
            }

            int written = 0, skipped = 0;
            foreach (var site in snapshot)
            {
                if (!site.IsKills || site.Id != target) continue;

                if (!_pats.TryGet(site.Id, site.Enc, out var pat) || pat.Flavor.Length == 0)
                {
                    skipped++;
                    continue;
                }

                // Verify-before-overwrite: never touch a buffer that doesn't still hold the
                // baked flavor text we expect (a freed/reused UI buffer, or a site already
                // overwritten by an earlier press).
                if (!_mem.TryReadBytes(site.AnchorAddr, pat.Flavor.Length, out var cur) || !ByteEq(cur, pat.Flavor))
                {
                    ModLogger.Log($"flavor-spike: weapon {site.Id} enc {site.Enc} addr=0x{site.AnchorAddr:X} anchor did not verify against the baked pattern -- skipped");
                    skipped++;
                    continue;
                }

                string line = FlavorProbeText.Compose(FlavorProbeText.CharCount(pat.Flavor.Length, site.Enc));
                byte[] payload = ByteScan.Enc(line, site.Enc);
                if (payload.Length != pat.Flavor.Length)
                {
                    // DEFENSIVE: must never write a mis-sized payload -- would corrupt whatever
                    // buffer content follows the flavor line.
                    ModLogger.LogError($"flavor-spike: composed payload length {payload.Length} != flavor pattern length {pat.Flavor.Length} for weapon {site.Id} enc {site.Enc} -- skipped");
                    skipped++;
                    continue;
                }

                if (!_mem.Writable(site.AnchorAddr, payload.Length))
                {
                    skipped++;
                    continue;
                }

                _mem.WriteBytes(site.AnchorAddr, payload);
                written++;
                ModLogger.Log($"flavor-spike: wrote weapon {site.Id} enc {site.Enc} addr=0x{site.AnchorAddr:X} chars={line.Length}");
            }

            ModLogger.Log($"flavor-spike: {written} written / {skipped} skipped for weapon {target} -- this weapon's card sites will be evicted by the painter now -- expected for P4 (render-only probe)");
        }
        catch (Exception ex)
        {
            ModLogger.LogError("flavor-spike: OneShot failed -- " + ex.Message);
        }
    }

    private static bool ByteEq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
#endif
