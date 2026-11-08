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

        try
        {
            Directory.CreateDirectory(configDirPath);

            if (!File.Exists(configFilePath))
            {
                var defaultConfig = new AppConfig();
                var defaultJson = JsonSerializer.Serialize(defaultConfig, JsonOptions);

                File.WriteAllText(configFilePath, defaultJson);
                return defaultConfig;
            }

            var configJson = File.ReadAllText(configFilePath);
            return JsonSerializer.Deserialize<AppConfig>(configJson, JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "配置文件读取失败，将使用空配置：{ConfigPath}", configFilePath);
            return new AppConfig();
        }
    }
}
