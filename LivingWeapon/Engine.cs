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
    // Poll fast: at fast-forward a death's hp==0 window is brief, and a 100ms loop sailed
    // past it, missing kills. The kill scan is cheap; growth (heavier) runs every Nth tick.
    private const int PollMs = 33;
    private const int GrowthEveryNTicks = 3;   // ~100ms; stat-hold doesn't need 33ms
    private int _tick;

    private readonly string _tallyPath;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly GrowthEngine _growth;
    private readonly Display _display;
    private CancellationTokenSource? _cts;
    private bool _inBattle;
    private DateTime _lastField = DateTime.MinValue;   // last tick we were on the live battlefield
    private const double FieldSettleSeconds = 1.5;      // wait this long off-field before painting
    private bool _lastBattleStatus;                     // edge-detect entering the in-battle status card

    public Engine(string modDir)
    {
        _tallyPath = Path.Combine(modDir, "kills.json");
        _kills = LoadTally(_tallyPath);
        var meta = MetaLoader.Load(modDir);
        _tracker = new KillTracker(_kills, new LiveMemory());
        _growth = new GrowthEngine(meta, _kills);
        _display = new Display(meta, _kills);
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
        int battleMode = Mem.U8(Offsets.BattleMode);
        bool entering = slot0 == 0xFF && slot9 == 0xFFFFFFFF;
        bool nowIn = entering || (_inBattle && slot9 == 0xFFFFFFFF);
        // slot9 stays stuck on the world-map party menu, so it can't tell combat from a
        // menu. battleMode does: 2/3/4 = live battlefield, 0 = world map / menus.
        bool onField = nowIn && (battleMode == 2 || battleMode == 3 || battleMode == 4);
        if (onField) _lastField = DateTime.Now;

        // In-battle "Status" card (a paused, stable menu) -- paint the counter there too.
        // Open status card = paused submenu in the action-menu context (battleMode 3).
        // menuCursor is the card's own cursor once open (not 3), so don't gate on it.
        bool battleStatus = nowIn && battleMode == 3
                            && Mem.U8(Offsets.PauseFlag) == 1 && Mem.U8(Offsets.SubmenuFlag) == 1;
        if (battleStatus && !_lastBattleStatus) _display.Invalidate();   // re-find the card's fresh buffers
        _lastBattleStatus = battleStatus;

        if (!nowIn)
        {
            if (_inBattle)
            {
                _inBattle = false;
                _tracker.ResetBattle();
                _growth.ResetBattle();
                SaveTally();                 // flush on battle end
                _display.Invalidate();       // re-find the menu's freshly-allocated render copies
            }
            _display.Tick();   // out of battle (slot9 cleared): keep the equip card painted
            return;
        }
        _inBattle = true;

        bool changed = _tracker.Poll();      // every ~33ms tick so fast-forward deaths aren't missed
        if (_tick++ % GrowthEveryNTicks == 0) _growth.Apply();   // growth holds stats; ~100ms is plenty
        if (changed) SaveTally();

        // slot9 is still the battle sentinel, but once we've been OFF the live battlefield
        // for a beat (battleMode 0 = world-map party menu / post-battle), paint the card.
        // RPM/WPM make the scan/paint fail-safe, so doing this in a churny menu can't crash;
        // the settle window just avoids needless work during a mid-combat battleMode flicker.
        if (battleStatus || (!onField && (DateTime.Now - _lastField).TotalSeconds > FieldSettleSeconds))
            _display.Tick();
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
