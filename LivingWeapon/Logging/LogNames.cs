using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Human-readable labels for weapon and job ids, used in log messages.
/// Weapon names come from the meta map loaded at startup; job names are hardcoded
/// from the verified PSX-wheel mapping (74..93 generic band, plus known specials).
/// All lookups degrade gracefully: an unknown id yields "weapon N" or "job N".
/// </summary>
internal static class LogNames
{
    private static IReadOnlyDictionary<int, WeaponMeta> _meta
        = new Dictionary<int, WeaponMeta>();

    // PSX wheel order: ids 74..93 generic jobs, plus the three verified specials.
    private static readonly Dictionary<int, string> Jobs = new()
    {
        { 74,  "Squire" },
        { 75,  "Chemist" },
        { 76,  "Knight" },
        { 77,  "Archer" },
        { 78,  "Monk" },
        { 79,  "White Mage" },
        { 80,  "Black Mage" },
        { 81,  "Time Mage" },
        { 82,  "Summoner" },
        { 83,  "Thief" },
        { 84,  "Orator" },
        { 85,  "Mystic" },
        { 86,  "Geomancer" },
        { 87,  "Dragoon" },
        { 88,  "Samurai" },
        { 89,  "Ninja" },
        { 90,  "Arithmetician" },
        { 91,  "Bard" },
        { 92,  "Dancer" },
        { 93,  "Mime" },
        { 43,  "Machinist" },
        { 96,  "Chocobo" },
        { 160, "Dark Knight" },
    };

    /// <summary>Called once at engine startup so weapon names are available for logging.</summary>
    public static void Init(IReadOnlyDictionary<int, WeaponMeta> meta)
    {
        _meta = meta;
    }

    /// <summary>Weapon display name: "Yoichi Bow" if known, "weapon 90" otherwise.</summary>
    public static string Weapon(int id)
    {
        if (_meta.TryGetValue(id, out var m) && m.Name.Length > 0)
            return m.Name;
        return "weapon " + id;
    }

    /// <summary>Job display name: "Thief" if known, "job 83" otherwise.</summary>
    public static string Job(int id)
    {
        return Jobs.TryGetValue(id, out var name) ? name : "job " + id;
    }
}
