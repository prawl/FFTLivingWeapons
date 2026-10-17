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
public class Configurable<TParentType> : IUpdatableConfigurable
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

    Action IConfigurable.Save => Save;

    /// <summary>Loads <paramref name="filePath"/>, or writes a default one if missing. Watches the
    /// file and raises <see cref="ConfigurationUpdated"/> on external edits (the launcher's
    /// property grid saves this way).</summary>
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
            var fresh = LoadFromFile(FilePath!);
            fresh.FilePath = FilePath;
            fresh.ConfigName = ConfigName;
            ConfigurationUpdated?.Invoke(fresh);
        }
        catch { }
    }
}
