using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// Tally persistence half of the Engine (atomic write + .bak), split from Engine.cs
/// to stay under the 200-line limit. The tally is the per-weapon kill count the whole
/// runtime keys on; losing it loses the player's progress, hence the paranoia.
/// </summary>
internal sealed partial class Engine
{
    private static Dictionary<int, int> LoadTally(string path)
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(p)) continue;
                var d = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(p));
                if (d == null) continue;
                var m = new Dictionary<int, int>(d.Count);
                foreach (var kv in d) if (int.TryParse(kv.Key, out int id)) m[id] = kv.Value;
                return m;
            }
            catch { }
        }
        return new Dictionary<int, int>();
    }

    private void SaveTally()
    {
        try
        {
            var outMap = new Dictionary<string, int>(_kills.Count);
            foreach (var kv in _kills) outMap[kv.Key.ToString()] = kv.Value;
            var tmp = _tallyPath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(outMap));
            if (File.Exists(_tallyPath)) File.Copy(_tallyPath, _tallyPath + ".bak", true);
            File.Move(tmp, _tallyPath, true);
        }
        catch (Exception ex) { Log.Error("save: " + ex.Message); }
    }

    private static int Sum(Dictionary<int, int> d)
    {
        int s = 0;
        foreach (var v in d.Values) s += v;
        return s;
    }
}
