using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace LivingWeapon;

/// <summary>
/// Validates kills.json. Takes JSON text and returns a sanitized id to kill-count map.
/// Input that is not a JSON object is a hard failure, treated like a corrupt file (fall back to .bak, then fresh).
/// Bad entries inside a valid object are dropped and counted, so one stray row cannot wipe the tally.
/// </summary>
internal static class KillsSchema
{
    /// <summary>Returns false when <paramref name="json"/> is not a JSON object (map empty, dropped 0).
    /// Otherwise keeps entries with a non-negative int key and a non-negative integer value,
    /// and counts the rest in <paramref name="dropped"/>.</summary>
    public static bool TryParse(string json, out Dictionary<int, int> map, out int dropped)
    {
        map = new Dictionary<int, int>();
        dropped = 0;

        JObject obj;
        try
        {
            var token = JToken.Parse(json);
            if (token.Type != JTokenType.Object) return false;
            obj = (JObject)token;
        }
        catch { return false; }

        foreach (var prop in obj.Properties())
        {
            if (!int.TryParse(prop.Name, out int id) || id < 0) { dropped++; continue; }
            if (prop.Value.Type != JTokenType.Integer) { dropped++; continue; }
            long value = prop.Value.Value<long>();
            if (value < 0 || value > int.MaxValue) { dropped++; continue; }
            map[id] = (int)value;
        }
        return true;
    }
}
