using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alterius.Models;

namespace Alterius.Core;

/// <summary>
/// Loads, validates, and atomically persists <see cref="AppConfig"/> in <c>config.json</c>.
/// </summary>
public sealed class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();
    private readonly FileLogger _logger;
    private AppConfig _current;

    public ConfigManager(FileLogger logger)
    {
        _logger = logger;
        _current = LoadOrCreateDefault();
    }

    public event Action<AppConfig>? ConfigChanged;

    /// <summary>
    /// Returns a snapshot copy of the current configuration.
    /// </summary>
    public AppConfig Current
    {
        get
        {
            lock (_sync)
            {
                return _current.Clone();
            }
        }
    }

    public void Save(AppConfig updatedConfig)
    {
        AppConfig normalized = updatedConfig.Clone();
        normalized.Normalize();

        lock (_sync)
        {
            WriteAtomically(normalized);
            _current = normalized;
        }

        _logger.SetRetentionDays(normalized.LogRetentionDays);
        AppPaths.EnsureDirectoriesExist(normalized.LocalBufferDir);

        _logger.Info(nameof(ConfigManager), "Configuration saved and applied.");
        ConfigChanged?.Invoke(normalized.Clone());
    }

    public AppConfig Reload()
    {
        AppConfig loaded = LoadOrCreateDefault();
        lock (_sync)
        {
            _current = loaded;
        }

        _logger.SetRetentionDays(loaded.LogRetentionDays);
        ConfigChanged?.Invoke(loaded.Clone());
        return loaded.Clone();
    }

    private AppConfig LoadOrCreateDefault()
    {
        string path = AppPaths.ConfigFilePath;
        if (!File.Exists(path))
        {
            var defaultConfig = new AppConfig();
            defaultConfig.Normalize();
            WriteAtomically(defaultConfig);
            AppPaths.EnsureDirectoriesExist(defaultConfig.LocalBufferDir);
            _logger.Info(nameof(ConfigManager), $"Created default configuration at {path}");
            return defaultConfig;
        }

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            AppConfig? parsed = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (parsed is null)
            {
                throw new InvalidDataException("Deserialized config.json is null.");
            }

            parsed.Normalize();
            AppPaths.EnsureDirectoriesExist(parsed.LocalBufferDir);
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(ConfigManager), $"Failed to parse {path}; backing up corrupt file and restoring defaults.", ex);
            TryBackupCorruptConfig(path);

            var fallback = new AppConfig();
            fallback.Normalize();
            WriteAtomically(fallback);
            AppPaths.EnsureDirectoriesExist(fallback.LocalBufferDir);
            return fallback;
        }
    }

    private void TryBackupCorruptConfig(string path)
    {
        try
        {
            string backupPath = $"{path}.corrupt_{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Move(path, backupPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(ConfigManager), $"Unable to back up corrupt config.json: {ex.Message}");
        }
    }

    private void WriteAtomically(AppConfig config)
    {
        string targetPath = AppPaths.ConfigFilePath;
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = targetPath + ".tmp";
        string json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, targetPath, overwrite: true);
    }
}
