using System;
using System.IO;

namespace VoiceInput.Utils;

/// <summary>
/// 集中管理应用的常量与路径。
/// Windows 下配置存放在 %APPDATA%\VoiceInput，日志存放在 %LOCALAPPDATA%\VoiceInput；
/// Linux 下通常对应 ~/.config/VoiceInput 和 ~/.local/share/VoiceInput。
/// 统一避免写入安装目录，降低权限问题。
/// </summary>
public static class AppPaths
{
    public const string AppName = "VoiceInput";
    public const string AppMutexName = "VoiceInput_Unique_App_Mutex";

    private const string ConfigDirectoryName = "config";
    private const string LogsDirectoryName = "logs";

    private const string ConfigFileName = "settings.json";
    private const string LogFileName = "app_log.txt";

    private static string RoamingDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppName
    );

    private static string LocalDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName
    );

    public static string ConfigDirectory { get; } = Path.Combine(RoamingDataDirectory, ConfigDirectoryName);

    public static string ConfigFilePath { get; } = Path.Combine(ConfigDirectory, ConfigFileName);

    public static string LogsDirectory { get; } = Path.Combine(LocalDataDirectory, LogsDirectoryName);

    public static string LogFilePath { get; } = Path.Combine(LogsDirectory, LogFileName);
}
