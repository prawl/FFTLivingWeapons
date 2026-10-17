using System;
using System.IO;
using System.Reflection;
using LivingWeapon.Configuration;
using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace LivingWeapon;

/// <summary>
/// Reloaded-II entry point, running in-process inside FFT_enhanced.exe. The constructor starts the engine;
/// Start is also wired, guarded, in case a host prefers it.
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

    /// <summary>IModV2 route, preferred by Reloaded-II 2.4.0. IReloadedHooks (from reloaded.sharedlib.hooks) only resolves here,
    /// not in the constructor. Without hooks the banner-toast delivery degrades and everything else runs.</summary>
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
                ModLogger.Event(LogVerb.Startup, "Connected to the game's rendering hooks; toast pop-ups can be delivered.");
            }
            else
            {
                // Toasts die, the mod runs.
                ModLogger.Warn(LogVerb.Startup, "The game-hooks helper mod (reloaded.sharedlib.hooks) is not loaded; toast pop-ups will not be delivered.");
                _engine?.InjectHooks(null);   // lets the extended inventory log why it stays off
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Startup, "Failed to connect to the game's rendering hooks; toast pop-ups will not be delivered: " + ex.Message);
            try { _engine?.InjectHooks(null); } catch { }
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
            Flight.Init(modDir);   // the black-box event ring, must exist before anything can Record
            // The Denuvo-free build is ASLR-relocated. Resolve the live image base before the Engine
            // and its 33ms thread exist; every pinned address rebases through it, so nothing above this line may touch Offsets.
            if (ImageBase.TryResolveLive(out long liveBase) && ImageBase.Set(liveBase))
                ModLogger.Event(LogVerb.Startup, $"Game image base 0x{ImageBase.Base:X} (preferred 0x{ImageBase.Preferred:X}, delta 0x{ImageBase.Delta:X}); every pinned address is rebased onto it.");
            else
                ModLogger.Warn(LogVerb.Startup, $"Could not read the game image base; using the preferred base 0x{ImageBase.Preferred:X}. If the game loaded elsewhere the startup guard will never arm.");
            // Launch header L1 (logging facelift). Version comes from the deployed ModConfig.json (fail-soft), flavor from the
            // compiled Tuning.BuildFlavor, not build_flavor.txt.
            ModLogger.Event(LogVerb.Startup,
                $"Living Weapons version {ModInfo.ReadVersion(modDir)} ({Tuning.BuildFlavor} build) is starting inside fft_enhanced.exe.");

            // Config load is fail-soft and falls back to the Tuning default. Reloaded writes user edits to
            // <Reloaded>/User/Mods/<ModId>/Config.json, so that file is preferred over modDir/Config.json (as in
            // FFTColorCustomizer.GetUserConfigPath).
            // Treasure Master's removal (2026-08-14) took every player-facing setting, so nothing is read out of the
            // config. It is still loaded so a corrupt file warns and the path is logged. Toasts, dev seeding (LWDEV) and
            // console verbosity keep their compiled Tuning defaults.
            string configPath = Path.Combine(modDir, "Config.json");   // overwritten below on success
            try
            {
                configPath = ResolveConfigPath(modDir);
                _ = Configurable<Config>.FromFile(configPath, "FFT Living Weapons Configuration");
            }
            catch (Exception cfgEx)
            {
                // Warn rather than Error, so a config typo does not use up the launch's one FlushOnce flight archive.
                ModLogger.Warn(LogVerb.Config,
                    $"Your settings could not be read; using defaults: {cfgEx.Message}");
            }
            // Console at Info; the log file takes every line regardless (docs/LOGGING.md). Set outside the try so a config
            // failure cannot leave the console on the lazily created default logger.
            ModLogger.LogLevel = LogLevel.Info;
            // Build-stamp drill, dev builds only. Set the environment variable LW_FORCE_FINGERPRINT_MISMATCH to 1, or drop a
            // marker file of that name in the mod dir (env variables do not reach fft_enhanced here), before launching a
            // DEV build to force the build stamp to read unknown (report line and armed-edge warning). The config knob was
            // removed 2026-07-07 so players cannot trigger it.
            // LW_FORCE_SENSING_MISMATCH (same two lanes) does the same for the sensing lane that gates the new-game detector.
            // LW_FORCE_GUARD_UNREADABLE blinds the startup guard, LW_FORCE_ROSTER_MISMATCH forces the roster landmark wrong and LW_FORCE_BAND_MISMATCH the band door.
#if LWDEV
            bool devForceFingerprintMismatch = DrillTrigger.DrillRequested(modDir);
            bool devForceSensingMismatch = DrillTrigger.DrillRequested(modDir, DrillTrigger.SensingFlagName);
            bool devForceGuardUnreadable = DrillTrigger.DrillRequested(modDir, DrillTrigger.UnreadableFlagName);
            bool devForceRosterMismatch = DrillTrigger.DrillRequested(modDir, DrillTrigger.RosterFlagName);
            bool devForceBandMismatch = DrillTrigger.DrillRequested(modDir, DrillTrigger.BandFlagName);
#else
            const bool devForceFingerprintMismatch = false;
            const bool devForceSensingMismatch = false;
            const bool devForceGuardUnreadable = false;
            const bool devForceRosterMismatch = false;
            const bool devForceBandMismatch = false;
#endif
            ModLogger.Event(LogVerb.Config,
                $"Configuration loaded: LogLevel={ModLogger.LogLevel} (from {configPath})");

            // Born disarmed, before the Engine and its FastHold thread exist. Only LaunchGuard's Armed edge (Engine.Tick,
            // onArmed callback) sets this back to true.
            Mem.WritesEnabled = false;
            // bannerToasts and devSeedKills are no longer config-driven; the Engine ctor uses its Tuning defaults.
            _engine = new Engine(modDir, devForceFingerprintMismatch: devForceFingerprintMismatch,
                devForceSensingMismatch: devForceSensingMismatch, devForceGuardUnreadable: devForceGuardUnreadable,
                devForceRosterMismatch: devForceRosterMismatch,
                devForceBandMismatch: devForceBandMismatch);
            _engine.Start();
        }
        catch (Exception ex)
        {
            try { ModLogger.Error(LogVerb.Startup, "Startup failed; Living Weapons will not run: " + ex); } catch { }
        }
    }

    /// <summary>The mod namespace (folder name under Mods/ and User/Mods/). Internal because SaveLocation reuses it.</summary>
    internal const string ModId = "prawl.fft.livingweapons";

    /// <summary>
    /// The config path to read. Reloaded saves user edits to &lt;Reloaded&gt;/User/Mods/&lt;ModId&gt;/Config.json (modDir is
    /// Mods/&lt;ModId&gt;, two levels under the root); use that when it exists, else the shipped default in modDir.
    /// Any path error returns the modDir path.
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
