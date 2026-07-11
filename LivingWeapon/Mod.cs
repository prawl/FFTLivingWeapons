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
    /// banner-toast callout delivery degrades; everything else runs.</summary>
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
                // Degraded but coping (the audit's Warning promotion): toasts die, the mod runs.
                ModLogger.Warn(LogVerb.Startup, "The game-hooks helper mod (reloaded.sharedlib.hooks) is not loaded; toast pop-ups will not be delivered.");
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Startup, "Failed to connect to the game's rendering hooks; toast pop-ups will not be delivered: " + ex.Message);
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
            Flight.Init(modDir);   // the black-box event ring -- must exist before anything else can Record
            // Launch header L1 (logging facelift): version from the deployed ModConfig.json
            // (fail-soft), flavor from the compiled Tuning.BuildFlavor const (the binary's own
            // truth, never build_flavor.txt).
            ModLogger.Event(LogVerb.Startup,
                $"Living Weapons version {ModInfo.ReadVersion(modDir)} ({Tuning.BuildFlavor} build) is starting inside fft_enhanced.exe.");

            // Load mod config fail-soft: any read failure falls back to the Tuning default (true).
            // The Reloaded launcher writes the user's edits to <Reloaded>/User/Mods/<ModId>/Config.json,
            // NOT to the deployed mod folder -- so we must read the user file when it exists (mirrors
            // FFTColorCustomizer.GetUserConfigPath), falling back to modDir/Config.json (the shipped
            // default) before the user has opened the config UI.
            // LW-52: TreasureAlwaysOn is the only player-facing toggle left. Toasts (always on),
            // dev-seeding (the LWDEV compile flag), and console verbosity (Info) are no longer
            // config-driven: the Engine ctor takes its Tuning defaults for the first two, and the
            // console is pinned to Info below (the log FILE always carries every line regardless).
            bool treasureAlwaysOn = Tuning.TreasureAlwaysOn;   // documented default
            string configPath     = Path.Combine(modDir, "Config.json");   // overwritten below on success
            try
            {
                configPath = ResolveConfigPath(modDir);
                var cfg    = Configurable<Config>.FromFile(configPath, "FFT Living Weapons Configuration");
                treasureAlwaysOn = cfg.TreasureAlwaysOn;
            }
            catch (Exception cfgEx)
            {
                // Warning, not Error (the audit's demotion): defaults cope, and a mere config
                // typo must not burn the launch's one FlushOnce flight archive.
                ModLogger.Warn(LogVerb.Config,
                    $"Your settings could not be read; using defaults (TreasureAlwaysOn={treasureAlwaysOn}): {cfgEx.Message}");
            }
            // Console at Info; the log FILE always carries every line unconditionally (docs/LOGGING.md).
            // DEFINED whether the try above succeeded or hit the catch (never skipped), so a
            // config-read failure can't strand the console on the lazily-created default logger.
            ModLogger.LogLevel = LogLevel.Info;
            // LW-50 stand-down drill, dev builds only: the config knob was removed 2026-07-07 so
            // players cannot trigger a stand-down from the launcher UI. Set the environment variable
            // LW_FORCE_FINGERPRINT_MISMATCH to 1 before launching a DEV build to force the mismatch.
#if LWDEV
            bool devForceFingerprintMismatch = Environment.GetEnvironmentVariable("LW_FORCE_FINGERPRINT_MISMATCH") == "1";
#else
            const bool devForceFingerprintMismatch = false;
#endif
            ModLogger.Event(LogVerb.Config,
                $"Configuration loaded: TreasureAlwaysOn={treasureAlwaysOn} LogLevel={ModLogger.LogLevel} (from {configPath})");

            // LW-50: born disarmed, before the Engine (and its FastHold background thread) exists.
            // Only LaunchGuard's Armed edge (inside Engine.Tick, via the guard's onArmed callback)
            // flips this back true.
            Mem.WritesEnabled = false;
            // LW-52: bannerToasts and devSeedKills are no longer config-driven; omitting them lets
            // the Engine ctor fall back to its Tuning defaults (toasts on; dev-seed under LWDEV).
            _engine = new Engine(modDir, treasureAlwaysOn, devForceFingerprintMismatch: devForceFingerprintMismatch);
            _engine.Start();
        }
        catch (Exception ex)
        {
            try { ModLogger.Error(LogVerb.Startup, "Startup failed; Living Weapons will not run: " + ex); } catch { }
        }
    }

    /// <summary>The mod namespace (the folder name under both Mods/ and User/Mods/). Internal,
    /// not private: LW-51's SaveLocation reuses it to resolve the update-safe save dir.</summary>
    internal const string ModId = "prawl.fft.livingweapons";

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
