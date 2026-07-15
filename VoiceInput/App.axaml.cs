using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NAudio.Wave;
using Serilog;
using SharpHook;
using SharpHook.Data;
using VoiceInput.Api;
using VoiceInput.Utils;
using VoiceInput.Views;
using WinForms = System.Windows.Forms;

namespace VoiceInput;

public partial class App : Application
{
    private const int AudioSampleRate = 16000;
    private const int AudioBitsPerSample = 16;
    private const int AudioChannels = 1;
    private const float AudioNormalizeFactor = 32768f;

    private VoiceOverlayWindow _overlayWindow = null!;
    private TrayMenuWindow _trayMenuWindow = null!;
    private WinForms.NotifyIcon _notifyIcon = null!;
    private XunfeiApi _xunfeiApi = null!;

    private TaskPoolGlobalHook? _globalHook;
    private WaveInEvent? _waveIn;
    private readonly object _waveInLock = new();

    // 状态
    private string _currentRecognizedText = string.Empty;
    private readonly object _textLock = new();
    private volatile bool _isCtrlPressed;
    private volatile bool _isWinPressed;
    private int _recordingState = (int)RecordingState.Idle;

    private bool _notifyIconDisposed;
    private readonly object _disposeLock = new();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var appName = Assembly.GetExecutingAssembly().GetName().Name ?? "VoiceInput";

        InitWindows();
        InitXunfeiApi();
        InitTrayIcon(appName);
        InitLifecycleAndHook();

        base.OnFrameworkInitializationCompleted();
    }

    private void InitWindows()
    {
        _overlayWindow = new VoiceOverlayWindow
        {
            ShowInTaskbar = false,
            WindowState = WindowState.Minimized
        };
        _overlayWindow.Show();

        _trayMenuWindow = new TrayMenuWindow();
    }

    private void InitXunfeiApi()
    {
        var config = ConfigManager.LoadConfig();
        _xunfeiApi = new XunfeiApi(config.AppId, config.ApiSecret, config.ApiKey);

        _xunfeiApi.OnTextChanged += text =>
        {
            lock (_textLock)
            {
                _currentRecognizedText = text;
            }

            Dispatcher.UIThread.Post(() => _overlayWindow.UpdateText(text));
        };
    }

    private void InitTrayIcon(string appName)
    {
        var processPath = Environment.ProcessPath;
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = string.IsNullOrEmpty(processPath)
                ? SystemIcons.Application
                : Icon.ExtractAssociatedIcon(processPath),
            Text = appName,
            Visible = true
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (GetCursorPos(out var pt))
                    {
                        _trayMenuWindow.ShowWithAnimation(pt.X - 100, pt.Y - 50);
                    }
                });
            }
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeNotifyIcon();
    }

    private void InitLifecycleAndHook()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            StartKeyboardHook();
        }
    }

    /// <summary>
    /// 每次按下ctrl win 都会初始化一次
    /// </summary>
    private void InitMicrophone()
    {
        var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(AudioSampleRate, AudioBitsPerSample, AudioChannels)
        };
        waveIn.DataAvailable += OnAudioDataAvailable;

        lock (_waveInLock)
        {
            _waveIn = waveIn;
        }
    }

    private void StartKeyboardHook()
    {
        _globalHook = new TaskPoolGlobalHook();
        _globalHook.KeyPressed += OnKeyPressed;
        _globalHook.KeyReleased += OnKeyReleased;
        _globalHook.RunAsync();
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (e.Data.KeyCode is KeyCode.VcLeftControl or KeyCode.VcRightControl) _isCtrlPressed = true;
        if (e.Data.KeyCode is KeyCode.VcLeftMeta or KeyCode.VcRightMeta) _isWinPressed = true;
        if (!_isCtrlPressed || !_isWinPressed) return;

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
                _overlayWindow.UpdateText(string.Empty);
                _ = _overlayWindow.ShowWithAnimation();
            });

            try
            {
                await _xunfeiApi.ConnectAsync();

                // 连接期间若用户已松键（Stopping），立即终止，防止 _waveIn 泄露
                if (_recordingState == (int)RecordingState.Stopping)
                {
                    await _xunfeiApi.StopAndSendLastFrameAsync();
                    Dispatcher.UIThread.Post(() => _ = _overlayWindow.HideWithAnimation());
                    Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
                    return;
                }

                InitMicrophone();

                lock (_waveInLock)
                {
                    _waveIn?.StartRecording();
                }

                Interlocked.Exchange(ref _recordingState, (int)RecordingState.Recording);
                Log.Information("开始录音...");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "连接讯飞 API 或初始化麦克风失败！");
                // 异常时回退状态 并 收起界面
                Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
                Dispatcher.UIThread.Post(() => _ = _overlayWindow.HideWithAnimation());
            }
        });
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (e.Data.KeyCode is KeyCode.VcLeftControl or KeyCode.VcRightControl) _isCtrlPressed = false;
        if (e.Data.KeyCode is KeyCode.VcLeftMeta or KeyCode.VcRightMeta) _isWinPressed = false;
        if (_isCtrlPressed && _isWinPressed) return;

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
                lock (_waveInLock)
                {
                    if (_waveIn is not null)
                    {
                        _waveIn.DataAvailable -= OnAudioDataAvailable;
                        _waveIn.StopRecording();
                        _waveIn.Dispose();
                        _waveIn = null;
                    }
                }

                await _xunfeiApi.StopAndSendLastFrameAsync();

                string finalText;
                lock (_textLock)
                {
                    finalText = _currentRecognizedText;
                    _currentRecognizedText = string.Empty;
                }

                Log.Information("停止录音");

                Dispatcher.UIThread.Post(() =>
                {
                    _ = _overlayWindow.HideWithAnimation();
                    if (string.IsNullOrWhiteSpace(finalText)) return;

                    KeyboardSimulator.SimulateTextEntry(finalText);
                    var clipboard = TopLevel.GetTopLevel(_overlayWindow)?.Clipboard;
                    if (clipboard is not null)
                    {
                        _ = clipboard.SetTextAsync(finalText);
                        Log.Information("识别完成，已写入剪贴板并模拟粘贴。内容长度: {Length}", finalText.Length);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止录音或发送最终帧失败");
                Dispatcher.UIThread.Post(() => _ = _overlayWindow.HideWithAnimation());
            }
            finally
            {
                // 无论成功或异常，都必须归位到 Idle
                Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
            }
        });
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        _ = _xunfeiApi.SendAudioDataAsync(e.Buffer, e.BytesRecorded);

        float maxVolume = 0;
        for (var i = 0; i < e.BytesRecorded; i += 2)
        {
            var sample = BitConverter.ToInt16(e.Buffer, i);
            var val = Math.Abs(sample / AudioNormalizeFactor);
            if (val > maxVolume) maxVolume = val;
        }

        Dispatcher.UIThread.Post(() => _overlayWindow.UpdateVolume(maxVolume));
    }

    private void DisposeNotifyIcon()
    {
        // ✅ [改] 加幂等保护，ProcessExit 和 ExitApplication 都调用也不会崩
        lock (_disposeLock)
        {
            if (_notifyIconDisposed) return;
            _notifyIconDisposed = true;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    public void ExitApplication(object? sender, EventArgs e)
    {
        _globalHook?.Dispose();
        DisposeNotifyIcon();

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