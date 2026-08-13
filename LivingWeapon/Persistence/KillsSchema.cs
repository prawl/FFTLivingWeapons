using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace LivingWeapon;

/// <summary>
/// LW-51 Tier-1: kills.json's on-disk schema validator. Pure: no file I/O, just JSON text in,
/// a sanitized id -&gt; kill-count map out. Structurally invalid input (not a JSON OBJECT at all:
/// an array, a scalar, or unparseable garbage) is a hard failure the caller should treat exactly
/// like today's corrupt-file case (fall through to the .bak, then fresh). A well-formed object
/// with some bad ENTRIES is lenient instead: one malformed row is dropped and counted, never
/// failing the whole parse, so a single stray entry can never nuke an otherwise-good tally.
/// </summary>
internal static class KillsSchema
{
    /// <summary>Parses <paramref name="json"/> into a sanitized id -&gt; kill-count map. Returns
    /// false when the json does not parse to a JSON object (array/scalar/garbage):
    /// <paramref name="map"/> is then empty and <paramref name="dropped"/> is 0; the caller should
    /// treat this like a corrupt file. Returns true otherwise: an entry is kept only when its key
    /// parses to a non-negative int AND its value is a non-negative JSON integer; every other
    /// entry (non-numeric/negative key, negative or non-integer value) is dropped and counted in
    /// <paramref name="dropped"/>.</summary>
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
