using System;
using System.IO;
using System.Reflection;
using LivingWeapon.Configuration;
using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

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

    public void Start(IModLoader? modLoader)
    {
        StartEngine();
        InjectReloadedHooks(modLoader);
    }

    /// <summary>IModV2 route: Reloaded-II 2.4.0 prefers StartEx when present; controllers
    /// (IReloadedHooks from reloaded.sharedlib.hooks) are only resolvable here, never in the
    /// constructor (mirrors FFTHandsFree.Mod). Fail-soft: without hooks the production
    /// banner-toast callout delivery (and the dev-only ShowSpike chase instrument) degrade;
    /// everything else runs.</summary>
    public void StartEx(IModLoaderV1 loaderApi, IModConfigV1 modConfig)
    {
        StartEngine();
        InjectReloadedHooks(loaderApi);
    }

    private void InjectReloadedHooks(IModLoaderV1? loader)
    {
        try
        {
            var hooksRef = loader?.GetController<IReloadedHooks>();
            if (hooksRef != null && hooksRef.TryGetTarget(out var hooks) && hooks != null)
            {
                _engine?.InjectHooks(hooks);
                ModLogger.Log("IReloadedHooks resolved and injected");
            }
            else
            {
                ModLogger.Log("IReloadedHooks controller not available -- is reloaded.sharedlib.hooks loaded?");
            }
        }
        catch (Exception ex)
        {
            ModLogger.LogError("IReloadedHooks injection failed -- " + ex.Message);
        }
    }

    private void StartEngine()
    {
        if (_started) return;
        _started = true;
        try
        {
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                            ?? Environment.CurrentDirectory;
            ModLogger.Init(modDir);
            ModLogger.Log("Living Weapon starting up (running inside FFT_enhanced.exe).");

            // Load mod config fail-soft: any read failure falls back to the Tuning default (true).
            // The Reloaded launcher writes the user's edits to <Reloaded>/User/Mods/<ModId>/Config.json,
            // NOT to the deployed mod folder -- so we must read the user file when it exists (mirrors
            // FFTColorCustomizer.GetUserConfigPath), falling back to modDir/Config.json (the shipped
            // default) before the user has opened the config UI.
            bool treasureAlwaysOn = Tuning.TreasureAlwaysOn;   // documented default
            bool bannerToasts     = Tuning.BannerToasts;       // documented default
            bool devSeedKills     = true;                      // documented default (dev builds only)
            bool verboseLog       = false;                     // documented default (Config.VerboseLog)
            string configPath     = Path.Combine(modDir, "Config.json");   // overwritten below on success
            try
            {
                configPath = ResolveConfigPath(modDir);
                var cfg    = Configurable<Config>.FromFile(configPath, "FFT Living Weapons Configuration");
                treasureAlwaysOn = cfg.TreasureAlwaysOn;
                bannerToasts     = cfg.BannerToasts;
                devSeedKills     = cfg.DevSeedKills;
                verboseLog       = cfg.VerboseLog;
            }
            catch (Exception cfgEx)
            {
                ModLogger.LogError($"config load failed, using defaults TreasureAlwaysOn={treasureAlwaysOn} BannerToasts={bannerToasts} DevSeedKills={devSeedKills} VerboseLog={verboseLog}: {cfgEx.Message}");
            }
            // Set the console threshold from whatever verboseLog resolved to -- DEFINED whether the
            // try above succeeded or hit the catch (never skipped), so a config-read failure can't
            // silently strand the console on whatever the lazily-created default logger picked.
            ModLogger.LogLevel = verboseLog ? LogLevel.Debug : LogLevel.Info;
            ModLogger.Log($"config: TreasureAlwaysOn={treasureAlwaysOn} BannerToasts={bannerToasts} DevSeedKills={devSeedKills} VerboseLog={verboseLog} LogLevel={ModLogger.LogLevel} (from {configPath})");

            _engine = new Engine(modDir, treasureAlwaysOn, bannerToasts, devSeedKills);
            _engine.Start();
        }
        catch (Exception ex)
        {
            try { ModLogger.LogError("startup failed -- Living Weapon will not run: " + ex); } catch { }
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
