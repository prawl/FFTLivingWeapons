using System;
using System.IO;
using System.Reflection;
using LivingWeapon.Configuration;
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
            Log.Info("Living Weapon starting up (running inside FFT_enhanced.exe).");

            // Load mod config fail-soft: any read failure falls back to the Tuning default (true).
            // The Reloaded launcher writes the user's edits to <Reloaded>/User/Mods/<ModId>/Config.json,
            // NOT to the deployed mod folder -- so we must read the user file when it exists (mirrors
            // FFTColorCustomizer.GetUserConfigPath), falling back to modDir/Config.json (the shipped
            // default) before the user has opened the config UI.
            bool treasureAlwaysOn = Tuning.TreasureAlwaysOn;   // documented default
            try
            {
                var configPath = ResolveConfigPath(modDir);
                var cfg        = Configurable<Config>.FromFile(configPath, "FFT Living Weapons Configuration");
                treasureAlwaysOn = cfg.TreasureAlwaysOn;
                Log.Info($"config: TreasureAlwaysOn={treasureAlwaysOn} (from {configPath})");
            }
            catch (Exception cfgEx)
            {
                Log.Error($"config load failed, using default TreasureAlwaysOn={treasureAlwaysOn}: {cfgEx.Message}");
            }

            _engine = new Engine(modDir, treasureAlwaysOn);
            _engine.Start();
        }
        catch (Exception ex)
        {
            try { Log.Error("startup failed -- Living Weapon will not run: " + ex); } catch { }
        }
    }

    /// <summary>The mod namespace -- the folder name under both Mods/ and User/Mods/.</summary>
    private const string ModId = "prawl.fft.livingweapons";

    /// <summary>
    /// The config the DLL should read. The Reloaded launcher saves user edits to
    /// &lt;Reloaded&gt;/User/Mods/&lt;ModId&gt;/Config.json (modDir is Mods/&lt;ModId&gt;, two
    /// levels under the Reloaded root). Prefer that file when it exists; otherwise fall back to
    /// the shipped default in modDir. Any path error returns the modDir path (FromFile is fail-soft).
    /// </summary>
    private static string ResolveConfigPath(string modDir)
    {
        try
        {
            var reloadedRoot = Directory.GetParent(modDir)?.Parent?.FullName;
            if (reloadedRoot != null)
            {
                var userConfig = Path.Combine(reloadedRoot, "User", "Mods", ModId, "Config.json");
                if (File.Exists(userConfig)) return userConfig;
            }
        }
        catch { /* fall through to the modDir default */ }
        return Path.Combine(modDir, "Config.json");
    }

    public void Suspend() { }
    public void Resume() { }
    public void Unload() => _engine?.Stop();
    public bool CanUnload() => false;
    public bool CanSuspend() => false;
    public Action Disposing { get; } = () => { };
}
