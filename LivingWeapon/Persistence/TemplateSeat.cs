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
/// This is the LW-346 hand-proven poke made systematic: that probe wrote the new id over the end
/// marker and put the marker one word later, and the owner watched both menus list the item.
///
/// SELF-PERPETUATING, STILL RUN EVERY TIME: because the serializer copies the template region
/// back into the save struct, the first save written after a seat carries the seated table, and
/// every later load restores it already seated. The seat still runs on every load: it is a no-op
/// when the id is already there, which is also what makes the detour and the tick able to run it
/// one after the other.
///
/// VANILLA SEMANTICS ARE KEPT: only ids the player owns at least one of are seated, because a
/// template lists owned items. An extended item the player has none of stays out, exactly as a
/// vanilla item with an empty bag count does.
///
/// The word format is the game's: little-endian u16 item ids ending in a 0x00FF marker. NOTE the
/// menu LISTS <see cref="OrderRebuildHook"/> repairs end in 0xFFFF instead; the two terminators
/// are not interchangeable.
/// </summary>
internal static class TemplateSeat
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
    /// there (the missing ids followed by the moved end marker), or a refusal to be logged.
    /// Nothing to do is <see cref="Bytes"/> null with no refusal.</summary>
    internal readonly record struct Seating(int WordIndex, byte[]? Bytes, string? Refusal)
    {
        public bool Writes => Bytes != null && Bytes.Length > 0;
        public static Seating Nothing => new(0, null, null);
        public static Seating Refuse(string why) => new(0, null, why);
    }

    /// <summary>Pure: read <paramref name="region"/> as u16 words up to its end marker and work
    /// out what has to be written so every id in <paramref name="ids"/> is listed ahead of that
    /// marker. Missing ids are appended in ascending id order, the marker moves that many words
    /// on, and nothing is ever written past <paramref name="capacityWords"/>. Words are compared
    /// as the game stores them, plain ids with no flag bits (the menu LISTS carry flags, these
    /// tables do not), so a junk word can never be mistaken for an id already seated.</summary>
    public static Seating Plan(byte[] region, int capacityWords, IReadOnlyList<int> ids)
    {
        if (region == null || ids == null || ids.Count == 0) return Seating.Nothing;
        int words = Math.Min(capacityWords, region.Length / 2);
        int marker = -1;
        var listed = new HashSet<ushort>();
        for (int i = 0; i < words; i++)
        {
            ushort w = (ushort)(region[i * 2] | (region[i * 2 + 1] << 8));
            if (w == EndMarker) { marker = i; break; }
            listed.Add(w);
        }
        if (marker < 0)
            return Seating.Refuse($"no {EndMarker:X4} end marker in its first {words} words, so where the list ends is unknown");

        var missing = new List<int>();
        foreach (int id in ids)
            if (!listed.Contains((ushort)id) && !missing.Contains(id)) missing.Add(id);
        if (missing.Count == 0) return Seating.Nothing;
        missing.Sort();

        // The ids take marker..marker+missing.Count-1 and the moved marker takes the word after,
        // so the last word touched must still be inside the region.
        if (marker + missing.Count > capacityWords - 1)
            return Seating.Refuse($"it already fills {marker} of its {capacityWords} words, so there is no room for {missing.Count} more id(s) and its end marker");

        var bytes = new byte[(missing.Count + 1) * 2];
        for (int i = 0; i < missing.Count; i++)
        {
            bytes[i * 2] = (byte)(missing[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((missing[i] >> 8) & 0xFF);
        }
        bytes[missing.Count * 2] = (byte)(EndMarker & 0xFF);
        bytes[missing.Count * 2 + 1] = (byte)(EndMarker >> 8);
        return new Seating(marker, bytes, null);
    }

    /// <summary>Seat <paramref name="ids"/> in both weapon templates through the guarded patcher.
    /// A template that cannot be read is left alone silently (the order-rebuild hook's posture: a
    /// failed read means the game's answer stands); a template with no end marker, no room, or a
    /// refused write reports through <paramref name="onRefused"/>. The detour path passes neither
    /// callback, because it must not log on the game's own thread.</summary>
    public static void Apply(ICodePatcher patcher, IReadOnlyList<int> ids,
        Action<string>? onRefused = null, Action<string>? onSeated = null)
    {
        if (ids == null || ids.Count == 0) return;
        foreach (var region in WeaponRegions)
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
            onSeated?.Invoke($"{region.Label} now lists {(seat.Bytes!.Length / 2) - 1} new item id(s) the loaded save did not know about (0x{region.Addr:X}, from word {seat.WordIndex}).");
        }
    }
}
