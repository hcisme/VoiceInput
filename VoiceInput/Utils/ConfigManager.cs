using System;
using System.IO;
using System.Text.Json;

namespace VoiceInput.Utils;

public class AppConfig
{
    public string AppId { get; set; } = "81f0b855";
    public string ApiSecret { get; set; } = "OGFhOGU5YWRiNDIxMDg1MWRiYTMzYmMx";
    public string ApiKey { get; set; } = "69e751d7b70cb4ff76db75d362b3032b";
}

public static class ConfigManager
{
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
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(defaultConfig, options);
            
            File.WriteAllText(configFilePath, json);
            return defaultConfig;
        }

        try
        {
            var json = File.ReadAllText(configFilePath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine("配置文件读取失败: " + ex.Message);
            return new AppConfig();
        }
    }
}