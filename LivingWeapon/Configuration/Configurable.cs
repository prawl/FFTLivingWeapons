using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reloaded.Mod.Interfaces;

namespace LivingWeapon.Configuration;

/// <summary>Base for mod config types, following the Reloaded-II mod template. FromFile loads or
/// creates an instance, Save writes it back, and IUpdatableConfigurable ties it to the launcher
/// property grid via Configurator.</summary>
public class Configurable<TParentType> : IUpdatableConfigurable, IDisposable
    where TParentType : Configurable<TParentType>, new()
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Browsable(false)]
    public event Action<IUpdatableConfigurable>? ConfigurationUpdated;

    [Browsable(false)]
    [JsonIgnore]
    public string? FilePath { get; set; }

    [Browsable(false)]
    [JsonIgnore]
    public string? ConfigName { get; set; }

    // Held in a field so the GC keeps it alive. System.Text.Json skips private fields.
    private FileSystemWatcher? _watcher;
    private readonly object _reloadLock = new();

    internal bool Watching => _watcher is not null;

    Action IConfigurable.Save => Save;

    /// <summary>Loads the settings file, or writes a default one if it is missing, and keeps watching it so edits made while the game runs take effect.
    /// Raises <see cref="ConfigurationUpdated"/> on external edits (the launcher's property grid saves this way).</summary>
    public static TParentType FromFile(string filePath, string configName)
    {
        var result = File.Exists(filePath) ? LoadFromFile(filePath) : new TParentType();
        result.FilePath = filePath;
        result.ConfigName = configName;
        if (!File.Exists(filePath))
            result.Save();
        result.EnableWatcher();
        return result;
    }

    private static TParentType LoadFromFile(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<TParentType>(json, SerializerOptions) ?? new TParentType();
        }
        catch
        {
            return new TParentType();
        }
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize((TParentType)this, SerializerOptions);
        File.WriteAllText(FilePath, json);
    }

    public void Dispose()
    {
        lock (_reloadLock)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
        GC.SuppressFinalize(this);
    }

    private void EnableWatcher()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        var dir = Path.GetDirectoryName(FilePath);
        var file = Path.GetFileName(FilePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(dir, file);
        _watcher.Changed += (_, _) => OnFileChanged();
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged()
    {
        try
        {
            TParentType fresh;
            lock (_reloadLock)
            {
                if (_watcher is null) return;
                fresh = LoadFromFile(FilePath!);
            }
            fresh.FilePath = FilePath;
            fresh.ConfigName = ConfigName;
            ConfigurationUpdated?.Invoke(fresh);
        }
        catch (Exception ex) { ModLogger.Debug(LogVerb.Config, $"The config file changed but the new settings could not be applied, so the settings from before stay in use ({ex.GetType().Name}, {ex.Message})."); }
    }
}
