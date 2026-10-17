using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Seats the owned extended item ids into the game's two weapon order templates.
/// The templates are the party inventory and the unit equip picker. An id the table does not name is never listed.
/// The load routine restores the tables byte-for-byte from the save struct and the serializer writes
/// them back, so they are save state (copies cited on <see cref="Offsets.InventoryOrderTemplate"/>
/// and <see cref="Offsets.PickerOrderTemplate"/>) and older saves come back without the ids. The
/// seat runs in the load detour after the bag counts replay; the tick runs it only for a load edge
/// the detour missed, so a whole-table repair never races the game's own edits.
/// Once the relocation arms, the live tables sit on <see cref="ExtendedInventory.TemplateRegions"/>
/// and the Offsets constants hold only the save projection <see cref="TemplateSync"/> writes at each
/// save edge (first 140 chart words). <see cref="WeaponRegions"/> stays the vanilla pair, the
/// fallback when the relocation never armed; production seats through TemplateRegions.
/// Only owned ids are seated, as in vanilla, and seating is a no-op when the id is already there.
/// Words are little-endian u16 ids ending in 0x00FF, unlike the 0xFFFF that <see cref="OrderRebuildHook"/>
/// list repairs use. Descends from the hand poke that moved the end marker one word later.
/// </summary>
internal static partial class TemplateSeat
{
    /// <summary>The word both templates end on (the lists use 0xFFFF).</summary>
    public const ushort EndMarker = 0x00FF;

    /// <summary>One order template. Where it starts, how many u16 words the game gives it, and
    /// the words a log line calls it by.</summary>
    internal readonly record struct Region(long Addr, int CapacityWords, string Label);

    /// <summary>The two weapon templates, in seating order. A property building a fresh array, since
    /// Addr is rebased live and a static readonly would bake the image base at type-init.
    /// Compared against the live rcx in OrderRebuildHook.Seat.</summary>
    internal static Region[] WeaponRegions => new Region[]
    {
        new(Offsets.InventoryOrderTemplate, Offsets.InventoryOrderTemplateWords, "the inventory order template"),
        new(Offsets.PickerOrderTemplate, Offsets.PickerOrderTemplateWords, "the equip-picker order template"),
    };

    /// <summary>What one template needs. The start word index, the bytes to write (missing ids plus the
    /// moved end marker, or after a repair the whole table with <see cref="Repaired"/> saying what was
    /// healed), or a refusal to log. Nothing to do is <see cref="Bytes"/> null with no refusal.</summary>
    internal readonly record struct Seating(int WordIndex, byte[]? Bytes, string? Refusal, string? Repaired = null)
    {
        public bool Writes => Bytes != null && Bytes.Length > 0;
        public static Seating Nothing => new(0, null, null);
        public static Seating Refuse(string why) => new(0, null, why);
    }

    /// <summary>Pure. Read <paramref name="region"/> as u16 words up to its end marker and work out what
    /// to write so every id in <paramref name="ids"/> is listed. Missing ids are appended ascending, the
    /// marker moves on, and nothing is written past <paramref name="capacityWords"/>. Words compare on
    /// the game's 0x3FF id mask (menu lists carry flag bits above it), as the repair's dedupe does.</summary>
    /// <param name="firstStaleId">forwarded to <see cref="ScanTable"/>; a word whose masked id is
    /// at or past this bound is dropped as stale. Defaults to <see cref="int.MaxValue"/>; production
    /// passes <see cref="ExtendedCatalog.FirstExtendedId"/> plus the armed extended count.</param>
    public static Seating Plan(byte[] region, int capacityWords, IReadOnlyList<int> ids, int firstStaleId = int.MaxValue)
    {
        if (region == null) return Seating.Nothing;
        ids ??= Array.Empty<int>();
        int words = Math.Min(capacityWords, region.Length / 2);
        var scan = ScanTable(region, words, firstStaleId);
        if (scan.Refusal != null) return Seating.Refuse(scan.Refusal);

        // Round 8c. Listed-ness uses the 0x3FF mask like the repair's dedupe; the raw word would drop
        // a flagged double and re-add the id forever.
        var listed = new HashSet<int>();
        foreach (ushort w in scan.Body) listed.Add(w & 0x3FF);
        var missing = new List<int>();
        foreach (int id in ids)
            if (!listed.Contains(id & 0x3FF) && !missing.Contains(id)) missing.Add(id);
        missing.Sort();

        if (scan.Repaired == null)
        {
            if (missing.Count == 0) return Seating.Nothing;
            // The ids take marker..marker+missing.Count-1 and the moved marker the next word, which
            // must stay inside the region.
            if (scan.Marker + missing.Count > capacityWords - 1)
                return Seating.Refuse($"it already fills {scan.Marker} of its {capacityWords} words, so there is no room for {missing.Count} more id(s) and its end marker");
            return new Seating(scan.Marker, Encode(missing), null);
        }

        // Round 8b. A damaged table is rewritten from word 0 through the new marker in one write.
        if (scan.Body.Count + missing.Count > capacityWords - 1)
            return Seating.Refuse($"even after a repair it would hold {scan.Body.Count + missing.Count} ids, more than its {capacityWords} words allow with an end marker");
        var all = new List<int>(scan.Body.Count + missing.Count);
        foreach (ushort w in scan.Body) all.Add(w);
        all.AddRange(missing);
        return new Seating(0, Encode(all), null, scan.Repaired);
    }

    /// <summary>The words as the game stores them, little-endian u16, followed by the end marker.</summary>
    private static byte[] Encode(List<int> wordsThenMarker)
    {
        var bytes = new byte[(wordsThenMarker.Count + 1) * 2];
        for (int i = 0; i < wordsThenMarker.Count; i++)
        {
            bytes[i * 2] = (byte)(wordsThenMarker[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((wordsThenMarker[i] >> 8) & 0xFF);
        }
        bytes[wordsThenMarker.Count * 2] = (byte)(EndMarker & 0xFF);
        bytes[wordsThenMarker.Count * 2 + 1] = (byte)(EndMarker >> 8);
        return bytes;
    }

    /// <summary>Seat <paramref name="ids"/> in both weapon templates through the guarded patcher. An
    /// unreadable template is skipped silently (the game's answer stands); a missing marker, no room or
    /// a refused write goes to <paramref name="onRefused"/>; a table healed on the way goes
    /// to <paramref name="onRepaired"/>. The detour passes no callbacks because it must not log on the
    /// game's own thread.</summary>
    /// <param name="regions">regions to seat into, <see cref="WeaponRegions"/> when null, else
    /// <see cref="ExtendedInventory.TemplateRegions"/>. Production passes the latter.</param>
    /// <param name="firstStaleId">forwarded to <see cref="Plan"/>, as there.</param>
    public static void Apply(ICodePatcher patcher, IReadOnlyList<int> ids,
        Action<string>? onRefused = null, Action<string>? onSeated = null, Action<string>? onRepaired = null,
        Region[]? regions = null, int firstStaleId = int.MaxValue)
    {
        // Round 8c. No early return on an empty list; a damaged table on a save with no extended items
        // still needs the repair (id-0 rows from a marker-less table crash).
        ids ??= Array.Empty<int>();
        foreach (var region in regions ?? WeaponRegions)
        {
            if (!patcher.TryRead(region.Addr, region.CapacityWords * 2, out var bytes)) continue;
            var seat = Plan(bytes, region.CapacityWords, ids, firstStaleId);
            if (seat.Refusal != null)
            {
                onRefused?.Invoke($"The new item(s) may not be listed in the menus: {region.Label} at 0x{region.Addr:X} could not take them ({seat.Refusal}).");
                continue;
            }
            if (!seat.Writes) continue;
            if (!patcher.TryWrite(region.Addr + (long)seat.WordIndex * 2, seat.Bytes!))
            {
                onRefused?.Invoke($"The new item(s) may not be listed in the menus: {region.Label} at 0x{region.Addr:X} refused the write.");
                continue;
            }
            if (seat.Repaired != null)
            {
                onRepaired?.Invoke($"{region.Label} at 0x{region.Addr:X} was repaired before seating: {seat.Repaired}.");
                continue;
            }
            onSeated?.Invoke($"{region.Label} now lists {(seat.Bytes!.Length / 2) - 1} new item id(s) the loaded save did not know about (0x{region.Addr:X}, from word {seat.WordIndex}).");
        }
    }
}
