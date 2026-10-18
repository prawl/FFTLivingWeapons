using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The stateful half of the parts report. Engine hands it the thirteen door readings every tick. It logs one
/// report line whenever any door changes STATE (a reason only change is not an edge), or the single all armed line
/// once every part is on (its latch matters only once a door can re verify), and shows one box per closure wave, so a late failure is never hidden behind an earlier box.
/// </summary>
internal sealed class PartsBoard
{
    /// <summary>Ticks a wave stays open collecting closures before its box shows (about three seconds).</summary>
    internal const int SettleSteps = 90;

    /// <summary>The scanner reads this line as the latest parts state.</summary>
    internal const string AllArmedLine = "Living Weapons: all parts armed.";

    private static readonly Door[] AllDoors = (Door[])Enum.GetValues(typeof(Door));

    private readonly Action<string, string>? _notice;
    private readonly Action<string, string>? _recorder;
    private readonly DoorState[] _last = new DoorState[AllDoors.Length];
    private bool _seen;
    private bool _allArmed;
    private long _tick;
    private long _waveOpenedAt;

    public int BoxCount { get; private set; }
    public string LastLine { get; private set; } = "";
    public bool WaveOpen { get; private set; }

    public PartsBoard(Action<string, string>? notice, Action<string, string>? recorder)
    {
        _notice = notice;
        _recorder = recorder;
    }

    public void Observe(IReadOnlyDictionary<Door, DoorReading> readings, string hedge, string drillSentence, Func<string> footer)
    {
        _tick++;
        bool changed = !_seen;
        foreach (var door in AllDoors)
        {
            var now = readings[door];
            int i = (int)door;
            bool closedEdge = now.State == DoorState.Closed && (!_seen || _last[i] != DoorState.Closed);
            if (!_seen || _last[i] != now.State) changed = true;
            _last[i] = now.State;
            if (closedEdge && now.FailedCheck && !WaveOpen && PartsReport.Reported(door))
            {
                WaveOpen = true;
                _waveOpenedAt = _tick;
            }
        }
        _seen = true;

        if (changed) LogChange(readings);
        if (WaveOpen && _tick - _waveOpenedAt >= SettleSteps) ShowBox(readings, hedge, drillSentence, footer());
    }

    private void LogChange(IReadOnlyDictionary<Door, DoorReading> readings)
    {
        LastLine = PartsReport.ComposeLine(readings);
        bool allOn = true;
        foreach (Part part in PartsReport.Shipped) allOn &= PartsReport.IsOn(part, readings);
        if (!allOn) ModLogger.Event(LogVerb.Startup, LastLine);
        else if (!_allArmed) ModLogger.Event(LogVerb.Startup, AllArmedLine);
        _allArmed = allOn;
        _recorder?.Invoke("guard", $"parts report changed ({LastLine})");
    }

    private void ShowBox(IReadOnlyDictionary<Door, DoorReading> readings, string hedge, string drillSentence, string footer)
    {
        WaveOpen = false;
        BoxCount++;
        int off = 0;
        foreach (Part part in PartsReport.Shipped)
            if (PartsReport.Status(part, readings).State == DoorState.Closed) off++;
        _recorder?.Invoke("guard", $"parts box shown ({off} parts off)");
        _notice?.Invoke("FFT Living Weapons",
            PartsReport.ComposeBox(readings, drillSentence, hedge, footer));
    }
}
