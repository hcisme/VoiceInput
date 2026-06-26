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
    private const string LogsFolderName = "logs";
    private const string LogsFileName = "app_log.txt";
    public static MemoryEventSink EventSink { get; } = new();

    public static void Initialize()
    {
        var baseDir = AppContext.BaseDirectory;
        var logDir = Path.Combine(baseDir, LogsFolderName);

        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        var logFilePath = Path.Combine(logDir, LogsFileName);

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

        Log.Information("日志系统初始化成功");
    }

    public static void Close()
    {
        Log.CloseAndFlush();
    }
}