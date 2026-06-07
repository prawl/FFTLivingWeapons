using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// The Living Weapon runtime. TWO background loops: detection+growth (in battle, 100ms)
/// and the display (always, 150ms) -- kept separate so the display's memory scan (which
/// can take ~a second) never delays kill detection. They share the kill tally via an
/// immutable snapshot the battle loop publishes: a volatile reference swap, so the display
/// reads a consistent copy with no locks.
/// </summary>
internal sealed class Engine
{
    private const int PollMs = 100;
    private const int DisplayMs = 150;

    private readonly string _tallyPath;
    private readonly Dictionary<int, int> _kills;          // live, owned by the battle loop
    private volatile Dictionary<int, int> _killsView;      // immutable snapshot for the display loop
    private readonly KillTracker _tracker;
    private readonly GrowthEngine _growth;
    private readonly Display _display;
    private CancellationTokenSource? _cts;
    private bool _inBattle;

    public Engine(string modDir)
    {
        _tallyPath = Path.Combine(modDir, "kills.json");
        _kills = LoadTally(_tallyPath);
        _killsView = new Dictionary<int, int>(_kills);
        var meta = MetaLoader.Load(modDir);
        _tracker = new KillTracker(_kills);
        _growth = new GrowthEngine(meta, _kills);
        _display = new Display(meta, () => _killsView);
        Log.Info($"loaded {meta.Count} weapon metas; {Sum(_kills)} kills in tally.");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Run(token, BattleTick, PollMs, "battle");
        Run(token, _display.Tick, DisplayMs, "display");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private void Run(CancellationToken token, Action tick, int ms, string name)
    {
        Task.Run(async () =>
        {
            Log.Info($"{name} loop started.");
            while (!token.IsCancellationRequested)
            {
                try { tick(); }
                catch (Exception ex) { Log.Error($"{name}: {ex.Message}"); }
                try { await Task.Delay(ms, token); } catch { }
            }
        }, token);
    }

    private void BattleTick()
    {
        uint slot0 = Mem.U32(Offsets.Slot0);
        uint slot9 = Mem.U32(Offsets.Slot9);
        bool entering = slot0 == 0xFF && slot9 == 0xFFFFFFFF;
        bool nowIn = entering || (_inBattle && slot9 == 0xFFFFFFFF);

        if (nowIn)
        {
            _inBattle = true;
            bool changed = _tracker.Poll();
            _growth.Apply();
            if (changed) { SaveTally(); _killsView = new Dictionary<int, int>(_kills); }   // publish to the display
        }
        else if (_inBattle)
        {
            _inBattle = false;
            _tracker.ResetBattle();
            _growth.ResetBattle();
            SaveTally();                     // flush on battle end
        }
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
