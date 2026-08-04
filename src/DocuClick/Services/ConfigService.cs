using System.IO;
using System.Text.Json;

namespace DocuClick.Services;

/// <summary>Loads/saves AppConfig as JSON under %APPDATA%/DocuClick/config.json.</summary>
public static class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocuClick");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config is not null)
                {
                    return config;
                }
            }
        }
        catch (Exception)
        {
            // A corrupt/unreadable config file must not block startup;
            // fall through and (re)write sensible defaults instead.
        }

        var defaultConfig = new AppConfig();
        Save(defaultConfig);
        return defaultConfig;
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
