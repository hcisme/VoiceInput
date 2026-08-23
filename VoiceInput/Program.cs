using Avalonia;
using System;
using System.Text;
using System.Threading;
using Serilog;
using VoiceInput.Utils;

namespace VoiceInput;

sealed class Program
{
    private static Mutex? _mutex;
    
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        LoggerManager.Initialize();
        _mutex = new Mutex(true, AppPaths.AppMutexName, out var createdNew);
        
        if (!createdNew)
        {
            Log.Warning("程序已经在运行中");
            LoggerManager.Close();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (OperationCanceledException ex)
        {
            // Linux 托盘退出时，Avalonia 的 DBusTrayIconImpl.WatchAsync() 可能抛出
            // TaskCanceledException，这是退出路径中的预期取消，不应记录为致命崩溃。
            Log.Debug(ex, "应用退出过程中发生预期取消。");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "程序发生未处理的崩溃异常！");
        }
        finally
        {
            _mutex.ReleaseMutex();
            LoggerManager.Close();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

#if !WINDOWS
        if (OperatingSystem.IsLinux()
            && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 })
        {
            builder = builder.UseWayland();
        }
#endif

        return builder
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
    }
}
