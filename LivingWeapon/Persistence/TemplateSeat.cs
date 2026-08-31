using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-351 fix round 5: put the extended item ids the player OWNS back into the game's two weapon
/// menu order templates.
///
/// WHY THIS EXISTS: the party inventory and the unit equip picker each build their weapon list
/// from a fixed table of item ids (<see cref="Offsets.InventoryOrderTemplate"/> and
/// <see cref="Offsets.PickerOrderTemplate"/>), and an id the table does not name is never listed.
/// Those tables are not rebuilt from the item data on a load: the load-apply routine restores
/// them byte-for-byte out of the save struct and the serializer writes them back, so they are
/// save state (the copies both ways are cited on the Offsets constants above). Every save the
/// owner has was written before an extended id ever seated, so every load restores tables without
/// it, and no amount of bag replaying can change that. The mod therefore seats the ids itself, in
/// the same load-detour moment that replays the bag counts, once the game's own restore has run.
///
/// LW-371 UPDATE: the paragraph above is the pre-relocation picture, still true when the
/// template relocation has not armed. Once it HAS armed, the templates the game actually works
/// on no longer live at the two Offsets constants above -- they live on the mod's own page
/// (<see cref="ExtendedInventory.TemplateRegions"/>), and <see cref="Offsets.InventoryOrderTemplate"/>
/// / <see cref="Offsets.PickerOrderTemplate"/> hold only the save-field projection
/// <see cref="TemplateSync"/> writes at each save edge (the first 140 chart words in page order,
/// extended ids included, D4) -- the save's staging area, not live game state. <see
/// cref="WeaponRegions"/> below stays the VANILLA pair unconditionally: it is what
/// <see cref="ExtendedInventory.TemplateRegions"/> falls back to when the relocation never armed,
/// and every caller here still reaches it by default, but production always seats through
/// <c>TemplateRegions</c> instead so a seat lands wherever the game is actually reading from.
///
/// This is the LW-346 hand-proven poke made systematic: that probe wrote the new id over the end
/// marker and put the marker one word later, and the owner watched both menus list the item.
///
/// SELF-PERPETUATING, STILL RUN EVERY TIME: because the serializer copies the template region
/// back into the save struct, the first save written after a seat carries the seated table, and
/// every later load restores it already seated. The seat still runs on every load: it is a no-op
/// when the id is already there. The load detour runs it on the game's own thread the moment the
/// load routine returns; the tick runs it only as the fallback for a load edge the detour did not
/// serve (round 8c), so a whole-table repair never races the game's own template edits.
///
/// VANILLA SEMANTICS ARE KEPT: only ids the player owns at least one of are seated, because a
/// template lists owned items. An extended item the player has none of stays out, exactly as a
/// vanilla item with an empty bag count does.
///
/// The word format is the game's: little-endian u16 item ids ending in a 0x00FF marker. NOTE the
/// menu LISTS <see cref="OrderRebuildHook"/> repairs end in 0xFFFF instead; the two terminators
/// are not interchangeable.
/// </summary>
internal static partial class TemplateSeat
{
    /// <summary>The word both templates end on (NOT the lists' 0xFFFF).</summary>
    public const ushort EndMarker = 0x00FF;

    /// <summary>One order template: where it starts, how many u16 words the game gives it, and
    /// the words a log line calls it by.</summary>
    internal readonly record struct Region(long Addr, int CapacityWords, string Label);

    /// <summary>The two weapon templates, in the order they are seated.</summary>
    internal static readonly Region[] WeaponRegions =
    {
        new(Offsets.InventoryOrderTemplate, Offsets.InventoryOrderTemplateWords, "the inventory order template"),
        new(Offsets.PickerOrderTemplate, Offsets.PickerOrderTemplateWords, "the equip-picker order template"),
    };

    /// <summary>What one template needs: the word index the write starts at, the bytes to write
    /// there (the missing ids followed by the moved end marker, or, after a repair, the whole
    /// table from word 0 through the new marker with <see cref="Repaired"/> saying what was
    /// healed), or a refusal to be logged. Nothing to do is <see cref="Bytes"/> null with no
    /// refusal.</summary>
    internal readonly record struct Seating(int WordIndex, byte[]? Bytes, string? Refusal, string? Repaired = null)
    {
        public bool Writes => Bytes != null && Bytes.Length > 0;
        public static Seating Nothing => new(0, null, null);
        public static Seating Refuse(string why) => new(0, null, why);
    }

    /// <summary>Pure: read <paramref name="region"/> as u16 words up to its end marker and work
    /// out what has to be written so every id in <paramref name="ids"/> is listed ahead of that
    /// marker. Missing ids are appended in ascending id order, the marker moves that many words
    /// on, and nothing is ever written past <paramref name="capacityWords"/>. Words are compared
    /// on the game's 0x3FF id mask (the menu LISTS carry flag bits above it; these tables do not
    /// today, and a flagged copy still names the same item), the same rule the repair applies
    /// when it collapses doubles.</summary>
    public static Seating Plan(byte[] region, int capacityWords, IReadOnlyList<int> ids)
    {
        if (region == null) return Seating.Nothing;
        ids ??= Array.Empty<int>();
        int words = Math.Min(capacityWords, region.Length / 2);
        var scan = ScanTable(region, words);
        if (scan.Refusal != null) return Seating.Refuse(scan.Refusal);

        // Round 8c: listed-ness is judged on the game's own 0x3FF id mask, the same rule the
        // repair's dedupe uses; judging it on the raw word would drop a flagged double and then
        // re-add the id, forever, one write per pass.
        var listed = new HashSet<int>();
        foreach (ushort w in scan.Body) listed.Add(w & 0x3FF);
        var missing = new List<int>();
        foreach (int id in ids)
            if (!listed.Contains(id & 0x3FF) && !missing.Contains(id)) missing.Add(id);
        missing.Sort();

        if (scan.Repaired == null)
        {
            if (missing.Count == 0) return Seating.Nothing;
            // The ids take marker..marker+missing.Count-1 and the moved marker takes the word
            // after, so the last word touched must still be inside the region.
            if (scan.Marker + missing.Count > capacityWords - 1)
                return Seating.Refuse($"it already fills {scan.Marker} of its {capacityWords} words, so there is no room for {missing.Count} more id(s) and its end marker");
            return new Seating(scan.Marker, Encode(missing), null);
        }

        // Round 8b: a damaged table is rewritten from word 0 through the new marker in one write.
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

    /// <summary>Seat <paramref name="ids"/> in both weapon templates through the guarded patcher.
    /// A template that cannot be read is left alone silently (the order-rebuild hook's posture: a
    /// failed read means the game's answer stands); a template with no end marker, no room, or a
    /// refused write reports through <paramref name="onRefused"/>; a table healed on the way
    /// (round 8b) reports through <paramref name="onRepaired"/>. The detour path passes no
    /// logging callback, because it must not log on the game's own thread.</summary>
    /// <param name="regions">LW-371: the regions to seat into -- <see cref="WeaponRegions"/> (the
    /// vanilla pair) when null or omitted, else <see cref="ExtendedInventory.TemplateRegions"/>
    /// (the relocated page's regions once armed). Every production caller passes the latter; the
    /// default keeps every pre-existing test and caller compiling unchanged.</param>
    public static void Apply(ICodePatcher patcher, IReadOnlyList<int> ids,
        Action<string>? onRefused = null, Action<string>? onSeated = null, Action<string>? onRepaired = null,
        Region[]? regions = null)
    {
        // Round 8c: no early return on an empty owned list. The seat half is then a no-op, but
        // the repair half must still run: a damaged table on a save whose player owns no
        // extended item at all is exactly the crash shape (id-0 rows from a marker-less table).
        ids ??= Array.Empty<int>();
        foreach (var region in regions ?? WeaponRegions)
        {
            if (!patcher.TryRead(region.Addr, region.CapacityWords * 2, out var bytes)) continue;
            var seat = Plan(bytes, region.CapacityWords, ids);
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
