using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Serilog;
using VoiceInput.Api;
using VoiceInput.Platform;
using VoiceInput.Utils;
using VoiceInput.Views;

namespace VoiceInput;

public partial class App : Application
{
    private VoiceOverlayWindow _overlayWindow = null!;
    private TrayMenuWindow _trayMenuWindow = null!;
    private XunfeiApi _xunfeiApi = null!;
    private RecordingController _recordingController = null!;

    private ITrayService _trayService = null!;
    private IAudioCaptureService _audioCaptureService = null!;
    private ITextEntryService _textEntryService = null!;
    private IGlobalHotkeyService _globalHotkeyService = null!;
    private int _isExiting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object? sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        // Linux 托盘图标在应用退出时，Avalonia 的 DBusTrayIconImpl.WatchAsync()
        // 会因为会话取消抛出 TaskCanceledException。这是退出路径中的预期取消，
        // 不应被当成致命崩溃。
        if (e.Exception is OperationCanceledException)
        {
            Log.Debug(e.Exception, "应用退出过程中取消了 UI 线程异步操作。");
            e.Handled = true;
            return;
        }

        Log.Error(e.Exception, "UI 线程发生未处理异常。");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var appName = Assembly.GetExecutingAssembly().GetName().Name ?? AppPaths.AppName;

        InitPlatformServices(appName);
        InitXunfeiApi();
        InitLifecycleAndHotkey();

        base.OnFrameworkInitializationCompleted();
    }

    private void InitXunfeiApi()
    {
        var config = ConfigManager.LoadConfig();

        if (string.IsNullOrWhiteSpace(config.AppId) ||
            string.IsNullOrWhiteSpace(config.ApiSecret) ||
            string.IsNullOrWhiteSpace(config.ApiKey))
        {
            Log.Warning("讯飞 API 配置不完整，请编辑 {ConfigPath}，填写 AppId、ApiSecret、ApiKey 后重启程序",
                AppPaths.ConfigFilePath);
        }

        _xunfeiApi = new XunfeiApi(config.AppId, config.ApiSecret, config.ApiKey);
        _recordingController = new RecordingController(_xunfeiApi, _audioCaptureService);

        _recordingController.OverlayShowRequested += () => Dispatcher.UIThread.Post(() =>
        {
            var overlayWindow = GetOrCreateOverlayWindow();
            overlayWindow.UpdateText(string.Empty);
            overlayWindow.ShowWithAnimation();
        });
        _recordingController.OverlayHideRequested += () => Dispatcher.UIThread.Post(() =>
            _ = GetOrCreateOverlayWindow().HideWithAnimation());
        _recordingController.TextUpdated += text => Dispatcher.UIThread.Post(() =>
            GetOrCreateOverlayWindow().UpdateText(text));
        _recordingController.SessionCompleted += finalText => Dispatcher.UIThread.Post(async () =>
        {
            var overlayWindow = GetOrCreateOverlayWindow();
            if (string.IsNullOrWhiteSpace(finalText))
            {
                await overlayWindow.HideWithAnimation();
                return;
            }

            var clipboard = TopLevel.GetTopLevel(overlayWindow)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(finalText);
            }

            if (_textEntryService.IsSupported)
            {
                _textEntryService.SimulateTextEntry(finalText);
                Log.Information("识别完成，已写入剪贴板并模拟输入。内容长度: {Length}", finalText.Length);
            }
            else
            {
                Log.Information("识别完成，已写入剪贴板。内容长度: {Length}", finalText.Length);
            }

            await overlayWindow.HideWithAnimation();
        });
    }

    private VoiceOverlayWindow GetOrCreateOverlayWindow()
    {
        return _overlayWindow ??= new VoiceOverlayWindow
        {
            ShowInTaskbar = false
        };
    }

    private TrayMenuWindow GetOrCreateTrayMenuWindow()
    {
        return _trayMenuWindow ??= new TrayMenuWindow();
    }

    private void InitPlatformServices(string appName)
    {
        _trayService = PlatformServices.CreateTrayService();
        _audioCaptureService = PlatformServices.CreateAudioCaptureService();
        _textEntryService = PlatformServices.CreateTextEntryService();
        _globalHotkeyService = PlatformServices.CreateGlobalHotkeyService();

        _trayService.Initialize(
            appName,
            ShowTrayMenu,
            () => ExitApplication(null, EventArgs.Empty));

        AppDomain.CurrentDomain.ProcessExit += (_, _) => _trayService.Dispose();
    }

    private void ShowTrayMenu(int x, int y)
    {
        var trayMenuWindow = GetOrCreateTrayMenuWindow();

        if (x < 0 || y < 0)
        {
            var screen = trayMenuWindow.Screens?.Primary;

            x = screen?.WorkingArea.X ?? 100;
            y = (screen?.WorkingArea.Bottom ?? 100) - 60;
        }
        else
        {
            x -= 100;
            y -= 50;
        }

        trayMenuWindow.ShowWithAnimation(x, y);
    }

    private void InitLifecycleAndHotkey()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _globalHotkeyService.HotkeyPressed += (_, _) => _recordingController.HandleHotkeyPressed();
            _globalHotkeyService.HotkeyReleased += (_, _) => _recordingController.HandleHotkeyReleased();
            _globalHotkeyService.Start();
        }
    }

    public void ExitApplication(object? sender, EventArgs e)
    {
        if (Interlocked.CompareExchange(ref _isExiting, 1, 0) != 0)
        {
            return;
        }

        // 正在录音时先优雅停止：停采集、排空已入队音频、发送最终帧，避免退出时截断发送。
        if (_recordingController.IsRecording)
        {
            _ = Task.Run(async () =>
            {
                await _recordingController.StopAndFinalizeAsync();
                Dispatcher.UIThread.Post(Shutdown);
            });
            return;
        }

        Shutdown();
    }

    private void Shutdown()
    {
        _globalHotkeyService.Dispose();
        _trayService.Dispose();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

}
