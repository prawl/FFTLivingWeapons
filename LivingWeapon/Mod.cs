using System;
using System.IO;
using System.Reflection;
using Reloaded.Mod.Interfaces;

namespace LivingWeapon;

/// <summary>
/// Reloaded-II entry point. Runs in-process inside FFT_enhanced.exe. Reloaded
/// instantiates this type and the constructor fires, so we start the engine
/// there (Start is also wired, guarded, in case a host prefers it).
/// </summary>
public class Mod : IMod
{
    private Engine? _engine;
    private bool _started;

    public Mod() => StartEngine();

    public void Start(IModLoader? modLoader) => StartEngine();

    private void StartEngine()
    {
        if (_started) return;
        _started = true;
        try
        {
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                            ?? Environment.CurrentDirectory;
            Log.Init(modDir);
            Log.Info("starting (in-process).");
            _engine = new Engine(modDir);
            _engine.Start();
        }
        catch (Exception ex)
        {
            try { Log.Error("start failed: " + ex); } catch { }
        }
    }

    public void Suspend() { }
    public void Resume() { }
    public void Unload() => _engine?.Stop();
    public bool CanUnload() => false;
    public bool CanSuspend() => false;
    public Action Disposing { get; } = () => { };
}
