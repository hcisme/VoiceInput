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
using VoiceInput.Services;
using VoiceInput.Utils;
using VoiceInput.Views;

namespace VoiceInput;

public partial class App : Application
{
    private VoiceOverlayWindow _overlayWindow = null!;
    private TrayMenuWindow _trayMenuWindow = null!;
    private XunfeiApi _xunfeiApi = null!;
    private RecordingController _recordingController = null!;

    private TrayService _trayService = null!;
    private AudioCaptureService _audioCaptureService = null!;
    private TextEntryService _textEntryService = null!;
    private GlobalHotkeyService _globalHotkeyService = null!;
    private int _isExiting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object? sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        // 应用退出时某些异步操作可能被取消，这是退出路径中的预期取消，
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

        InitServices(appName);
        InitXunfeiApi();
        InitLifecycleAndHotkey();

        base.OnFrameworkInitializationCompleted();
    }

    private void InitXunfeiApi()
    {
        var config = LoadApiConfig();
        CreateRecordingServices(config);
    }

    private static AppConfig LoadApiConfig()
    {
        var config = ConfigManager.LoadConfig();

        if (string.IsNullOrWhiteSpace(config.AppId) ||
            string.IsNullOrWhiteSpace(config.ApiSecret) ||
            string.IsNullOrWhiteSpace(config.ApiKey))
        {
            Log.Warning("讯飞 API 配置不完整，请编辑 {ConfigPath}，填写 AppId、ApiSecret、ApiKey 后重启程序",
                AppPaths.ConfigFilePath);
        }

        return config;
    }

    private void CreateRecordingServices(AppConfig config)
    {
        _xunfeiApi = new XunfeiApi(config.AppId, config.ApiSecret, config.ApiKey);
        _recordingController = new RecordingController(_xunfeiApi, _audioCaptureService);
        BindRecordingEvents();
    }

    private void BindRecordingEvents()
    {
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
            try
            {
                if (string.IsNullOrWhiteSpace(finalText)) return;

                var clipboard = TopLevel.GetTopLevel(overlayWindow)?.Clipboard;
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(finalText);
                }

                _textEntryService.SimulateTextEntry(finalText);
                Log.Information("识别完成，已写入剪贴板并模拟输入。内容长度: {Length}", finalText.Length);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "写入剪贴板或模拟输入失败");
            }
            finally
            {
                await overlayWindow.HideWithAnimation();
            }
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

    private void InitServices(string appName)
    {
        _trayService = new TrayService();
        _audioCaptureService = new AudioCaptureService();
        _textEntryService = new TextEntryService();
        _globalHotkeyService = new GlobalHotkeyService();

        _trayService.Initialize(
            appName,
            ShowTrayMenu);

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

    public void ExitApplication()
    {
        if (Interlocked.CompareExchange(ref _isExiting, 1, 0) != 0)
        {
            return;
        }

        // 正在录音时先优雅停止：停采集、排空已入队音频、发送最终帧，避免退出时截断发送。
        if (_recordingController.IsSessionActive)
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
        _audioCaptureService.Dispose();
        _xunfeiApi.Dispose();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

}
