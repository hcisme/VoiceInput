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
    private const int AudioNormalizeFactor = 32768;

    private VoiceOverlayWindow _overlayWindow = null!;
    private TrayMenuWindow _trayMenuWindow = null!;
    private XunfeiApi _xunfeiApi = null!;

    private ITrayService _trayService = null!;
    private IAudioCaptureService _audioCaptureService = null!;
    private ITextEntryService _textEntryService = null!;
    private IGlobalHotkeyService _globalHotkeyService = null!;

    // 状态
    private string _currentRecognizedText = string.Empty;
    private readonly object _textLock = new();
    private int _recordingState = (int)RecordingState.Idle;

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

        InitXunfeiApi();
        InitPlatformServices(appName);
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

        _xunfeiApi.OnTextChanged += text =>
        {
            lock (_textLock)
            {
                _currentRecognizedText = text;
            }

            Dispatcher.UIThread.Post(() => GetOrCreateOverlayWindow().UpdateText(text));
        };
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

        _audioCaptureService.DataAvailable += OnAudioDataAvailable;
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

            _globalHotkeyService.HotkeyPressed += OnHotkeyPressed;
            _globalHotkeyService.HotkeyReleased += OnHotkeyReleased;
            _globalHotkeyService.Start();
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        // 只允许从 Idle → Connecting，防止重复触发
        if (Interlocked.CompareExchange(
                ref _recordingState,
                (int)RecordingState.Connecting,
                (int)RecordingState.Idle) != (int)RecordingState.Idle)
        {
            return;
        }

        // 只在真正开始录音时清空
        lock (_textLock)
        {
            _currentRecognizedText = string.Empty;
        }

        _ = Task.Run(async () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var overlayWindow = GetOrCreateOverlayWindow();
                overlayWindow.UpdateText(string.Empty);
                overlayWindow.ShowWithAnimation();
            });

            try
            {
                await _xunfeiApi.ConnectAsync();

                // 连接期间若用户已松键（Stopping），立即终止，防止录音服务空转
                if (_recordingState == (int)RecordingState.Stopping)
                {
                    await _xunfeiApi.StopAndSendLastFrameAsync();
                    Dispatcher.UIThread.Post(() => _ = GetOrCreateOverlayWindow().HideWithAnimation());
                    Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
                    return;
                }

                _audioCaptureService.Start();

                Interlocked.Exchange(ref _recordingState, (int)RecordingState.Recording);
                Log.Information("开始录音...");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "连接讯飞 API 或初始化麦克风失败！");
                // 异常时回退状态并收起界面
                Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
                Dispatcher.UIThread.Post(() => _ = GetOrCreateOverlayWindow().HideWithAnimation());
            }
        });
    }

    private void OnHotkeyReleased(object? sender, EventArgs e)
    {
        var prev = Interlocked.CompareExchange(
            ref _recordingState,
            (int)RecordingState.Stopping,
            (int)RecordingState.Recording);

        if (prev == (int)RecordingState.Idle) return;

        if (prev == (int)RecordingState.Connecting)
        {
            Interlocked.Exchange(ref _recordingState, (int)RecordingState.Stopping);
            return;
        }

        if (prev != (int)RecordingState.Recording) return;

        _ = Task.Run(async () =>
        {
            try
            {
                _audioCaptureService.Stop();
                await _xunfeiApi.StopAndSendLastFrameAsync();

                string finalText;
                lock (_textLock)
                {
                    finalText = _currentRecognizedText;
                    _currentRecognizedText = string.Empty;
                }

                Log.Information("停止录音");

                Dispatcher.UIThread.Post(async () =>
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
                        await overlayWindow.HideWithAnimation();
                    }
                    else
                    {
                        Log.Information("识别完成，已写入剪贴板。内容长度: {Length}", finalText.Length);
                        await overlayWindow.HideWithAnimation();
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止录音或发送最终帧失败");
                Dispatcher.UIThread.Post(() => _ = GetOrCreateOverlayWindow().HideWithAnimation());
            }
            finally
            {
                // 无论成功或异常，都必须归位到 Idle
                Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
            }
        });
    }

    private void OnAudioDataAvailable(byte[] buffer, int bytesRecorded)
    {
        _ = _xunfeiApi.SendAudioDataAsync(buffer, bytesRecorded);

        float maxVolume = 0;
        for (var i = 0; i < bytesRecorded; i += 2)
        {
            var sample = BitConverter.ToInt16(buffer, i);
            var val = Math.Abs(sample / (float)AudioNormalizeFactor);
            if (val > maxVolume) maxVolume = val;
        }

        Dispatcher.UIThread.Post(() => GetOrCreateOverlayWindow().UpdateVolume(maxVolume));
    }

    public void ExitApplication(object? sender, EventArgs e)
    {
        _globalHotkeyService?.Dispose();
        _trayService?.Dispose();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private enum RecordingState
    {
        Idle = 0,
        Connecting = 1,
        Recording = 2,
        Stopping = 3
    }
}
