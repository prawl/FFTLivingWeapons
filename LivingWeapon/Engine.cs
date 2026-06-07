using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// The Living Weapon runtime. One background loop: in battle it counts kills and
/// applies stat growth; the per-weapon tally persists across sessions. Detection
/// and growth share the in-memory tally (no IPC). Display painting is layered on
/// top in a later phase.
/// </summary>
internal sealed class Engine
{
    private const int PollMs = 100;

    private readonly string _tallyPath;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly GrowthEngine _growth;
    private CancellationTokenSource? _cts;
    private bool _inBattle;

    public Engine(string modDir)
    {
        _tallyPath = Path.Combine(modDir, "kills.json");
        _kills = LoadTally(_tallyPath);
        var meta = MetaLoader.Load(modDir);
        _tracker = new KillTracker(_kills);
        _growth = new GrowthEngine(meta, _kills);
        Log.Info($"loaded {meta.Count} weapon metas; {Sum(_kills)} kills in tally.");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            Log.Info("runtime loop started.");
            while (!token.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception ex) { Log.Error("tick: " + ex.Message); }
                try { await Task.Delay(PollMs, token); } catch { }
            }
        }, token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private void Tick()
    {
        uint slot0 = Mem.U32(Offsets.Slot0);
        uint slot9 = Mem.U32(Offsets.Slot9);
        bool entering = slot0 == 0xFF && slot9 == 0xFFFFFFFF;
        bool nowIn = entering || (_inBattle && slot9 == 0xFFFFFFFF);

        if (!nowIn)
        {
            if (_inBattle)
            {
                _inBattle = false;
                _tracker.ResetBattle();
                _growth.ResetBattle();
                SaveTally();                 // flush on battle end
            }
            return;
        }
        _inBattle = true;

        bool changed = _tracker.Poll();
        _growth.Apply();
        if (changed) SaveTally();
    }

    // ---- tally persistence (atomic + .bak) ----
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
