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
        _mutex = new Mutex(true, "VoiceInput_Unique_App_Mutex", out var createdNew);
        
        if (!createdNew)
        {
            Log.Warning("程序已经在运行中，即将退出...");
            return; 
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}