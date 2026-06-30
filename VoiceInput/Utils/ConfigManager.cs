using Serilog;
using System;
using System.IO;
using System.Text.Json;

namespace VoiceInput.Utils;

public class AppConfig
{
    public string AppId { get; set; } = "REPLACED_XUNFEI_APPID";
    public string ApiSecret { get; set; } = "REPLACED_XUNFEI_APISECRET";
    public string ApiKey { get; set; } = "REPLACED_XUNFEI_APIKEY";
}

public static class ConfigManager
{
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };

    public static AppConfig LoadConfig()
    {
        var baseDir = AppContext.BaseDirectory;
        var configDirPath = Path.Combine(baseDir, "config");
        var configFilePath = Path.Combine(configDirPath, "settings.json");

        if (!Directory.Exists(configDirPath))
        {
            Directory.CreateDirectory(configDirPath);
        }

        if (!File.Exists(configFilePath))
        {
            var defaultConfig = new AppConfig();
            var json = JsonSerializer.Serialize(defaultConfig, _jsonOptions);

            File.WriteAllText(configFilePath, json);
            return defaultConfig;
        }

        try
        {
            var json = File.ReadAllText(configFilePath);
            return JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "配置文件读取失败");
            return new AppConfig();
        }
    }
}