using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace VoiceInput.Utils;

public class MemoryEventSink : ILogEventSink
{
    public event Action<DateTime, string, string>? OnLogEmitted;

    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage();
        var level = logEvent.Level.ToString();
        OnLogEmitted?.Invoke(logEvent.Timestamp.DateTime, level, message);
    }
}

public static class LoggerManager
{
    public static MemoryEventSink EventSink { get; } = new();

    public static void Initialize()
    {
        var logDir = AppPaths.LogsDirectory;
        var logFilePath = AppPaths.LogFilePath;

        try
        {
            Directory.CreateDirectory(logDir);

            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .MinimumLevel.Information()
                .WriteTo.File(
                    path: logFilePath,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    retainedFileCountLimit: 3,
                    shared: true
                )
                .WriteTo.Sink(EventSink)
                .CreateLogger();
        }
        catch (Exception ex)
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .MinimumLevel.Information()
                .CreateLogger();

            Log.Error(ex, "日志目录创建或文件日志初始化失败，已退回控制台日志");
            return;
        }

        Log.Information("日志系统初始化成功");
    }

    public static void Close()
    {
        Log.CloseAndFlush();
    }
}
