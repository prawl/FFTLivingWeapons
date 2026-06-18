using System;
using System.IO;
using Reloaded.Mod.Interfaces;

namespace LivingWeapon.Configuration;

/// <summary>
/// Reloaded-II configurator for FFT Living Weapons.  Implements IConfiguratorV3 so the
/// launcher shows a "Configure Mod" button that opens the built-in property grid for
/// <see cref="Config"/>.  TryRunCustomConfiguration returns false, delegating UI to the
/// Reloaded property grid (no WinForms dependency needed).
/// </summary>
public class Configurator : IConfiguratorV3
{
    private static readonly ConfiguratorMixin _mixin = new();

    public string?              ModFolder   { get; private set; }
    public string?              ConfigFolder { get; private set; }
    public ConfiguratorContext  Context      { get; private set; }

    public IUpdatableConfigurable[] Configurations => _configurations ?? MakeConfigurations();
    private IUpdatableConfigurable[]? _configurations;

    private IUpdatableConfigurable[] MakeConfigurations()
    {
        _configurations = _mixin.MakeConfigurations(ConfigFolder!);
        for (int i = 0; i < _configurations.Length; i++)
        {
            var idx = i;
            _configurations[i].ConfigurationUpdated += c => _configurations[idx] = c;
        }
        return _configurations;
    }

    public Configurator() { }
    public Configurator(string configDirectory) : this() => ConfigFolder = configDirectory;

    // ── IConfiguratorV3 ───────────────────────────────────────────────────────

    public IConfiguratorV3 SetModDirectory(string modDirectory)
    {
        ModFolder = modDirectory;
        return this;
    }

    public IConfiguratorV3 SetConfigDirectory(string configDirectory)
    {
        ConfigFolder = configDirectory;
        return this;
    }

    public IConfiguratorV3 SetConfigDirectory(string configDirectory, string oldDirectory)
    {
        SetConfigDirectory(configDirectory);
        MigrateFile(oldDirectory, configDirectory, "Config.json");
        return this;
    }

    void IConfiguratorV3.SetContext(in ConfiguratorContext context) => Context = context;

    // ── IConfiguratorV2 ───────────────────────────────────────────────────────

    void IConfiguratorV2.SetConfigDirectory(string configDirectory) => ConfigFolder = configDirectory;

    public void Migrate(string oldDirectory, string newDirectory) =>
        MigrateFile(oldDirectory, newDirectory, "Config.json");

    // ── IConfiguratorV1 ───────────────────────────────────────────────────────

    public IConfigurable[]          GetConfigurations()              => Configurations;
    void IConfiguratorV1.SetModDirectory(string d)                   => ModFolder = d;

    /// <summary>
    /// Returns false so Reloaded-II uses its built-in property grid.
    /// No WinForms, no custom UI.
    /// </summary>
    public bool TryRunCustomConfiguration() => false;

    public TType GetConfiguration<TType>(int index) where TType : class =>
        (TType)(object)Configurations[index];

    public void Save()
    {
        foreach (var c in Configurations)
            (c as IConfigurable)?.Save?.Invoke();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void MigrateFile(string oldDir, string newDir, string fileName)
    {
        try
        {
            var src = Path.Combine(oldDir, fileName);
            var dst = Path.Combine(newDir, fileName);
            if (File.Exists(src) && !File.Exists(dst))
            {
                Directory.CreateDirectory(newDir);
                File.Move(src, dst);
            }
        }
        catch { }
    }
}
