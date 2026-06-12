using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reloaded.Mod.Interfaces;

namespace LivingWeapon.Configuration;

/// <summary>
/// Base for mod config types.  Mirrors the Configurable pattern from the
/// Reloaded-II mod template: FromFile constructs or loads an instance, Save
/// writes it back, and IUpdatableConfigurable ties it to the Reloaded launcher
/// property grid via Configurator.
/// </summary>
public class Configurable<TParentType> : IUpdatableConfigurable
    where TParentType : Configurable<TParentType>, new()
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // ── IConfigurable / IUpdatableConfigurable ───────────────────────────────

    [Browsable(false)]
    public event Action<IUpdatableConfigurable>? ConfigurationUpdated;

    [Browsable(false)]
    [JsonIgnore]
    public string? FilePath { get; set; }

    [Browsable(false)]
    [JsonIgnore]
    public string? ConfigName { get; set; }

    // FileSystemWatcher kept as a field so the GC doesn't collect it while active.
    // Private fields are ignored by System.Text.Json by default -- no attribute needed.
    private FileSystemWatcher? _watcher;

    Action IConfigurable.Save => Save;

    // ── static factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Load from <paramref name="filePath"/> if it exists, otherwise create a default
    /// instance and write the defaults to disk.  Attaches a file-system watcher that
    /// fires <see cref="ConfigurationUpdated"/> when the file changes externally (e.g.
    /// the Reloaded launcher saves after the user edits the property grid).
    /// </summary>
    public static TParentType FromFile(string filePath, string configName)
    {
        var result = File.Exists(filePath) ? LoadFromFile(filePath) : new TParentType();
        result.FilePath   = filePath;
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

    // ── instance methods ──────────────────────────────────────────────────────

    public void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize((TParentType)this, SerializerOptions);
        File.WriteAllText(FilePath, json);
    }

    // ── file watcher ──────────────────────────────────────────────────────────

    private void EnableWatcher()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        var dir  = Path.GetDirectoryName(FilePath);
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
            fresh.FilePath   = FilePath;
            fresh.ConfigName = ConfigName;
            ConfigurationUpdated?.Invoke(fresh);
        }
        catch { }
    }
}
