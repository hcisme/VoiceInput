using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace VoiceInput.Utils;

public class AppConfig
{
    public string? AppId { get; set; }
    public string? ApiSecret { get; set; }
    public string? ApiKey { get; set; }
}

public static class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppConfig LoadConfig()
    {
        var configDirPath = AppPaths.ConfigDirectory;
        var configFilePath = AppPaths.ConfigFilePath;

        if (!Directory.Exists(configDirPath))
        {
            Directory.CreateDirectory(configDirPath);
        }

        if (!File.Exists(configFilePath))
        {
            var defaultConfig = new AppConfig();
            var json = JsonSerializer.Serialize(defaultConfig, JsonOptions);

            File.WriteAllText(configFilePath, json);
            return defaultConfig;
        }

        try
        {
            var json = File.ReadAllText(configFilePath);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "配置文件读取失败");
            return new AppConfig();
        }
    }
}