using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
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

    private string _currentRecognizedText = string.Empty;

    // 状态
    private bool _isCtrlPressed;
    private bool _isWinPressed;
    private bool _isRecording;

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
        _overlayWindow = new VoiceOverlayWindow { Opacity = 0 };
        _overlayWindow.Show();
        _overlayWindow.Hide();
        _overlayWindow.Opacity = 1;

        _trayMenuWindow = new TrayMenuWindow();
    }

    private void InitXunfeiApi()
    {
        var config = ConfigManager.LoadConfig();
        _xunfeiApi = new XunfeiApi(config.AppId, config.ApiSecret, config.ApiKey);

        _xunfeiApi.onTextChanged += text =>
        {
            _currentRecognizedText = text;
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

        _notifyIcon.MouseClick += (s, e) =>
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

        AppDomain.CurrentDomain.ProcessExit += (s, e) => DisposeNotifyIcon();
    }

    private void InitLifecycleAndHook()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            StartKeyboardHook();
        }
    }

    private void InitMicrophone()
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(AudioSampleRate, AudioBitsPerSample, AudioChannels)
        };
        _waveIn.DataAvailable += OnAudioDataAvailable;
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
        _currentRecognizedText = string.Empty;
        if (e.Data.KeyCode is KeyCode.VcLeftControl or KeyCode.VcRightControl) _isCtrlPressed = true;
        if (e.Data.KeyCode is KeyCode.VcLeftMeta or KeyCode.VcRightMeta) _isWinPressed = true;

        if (_isCtrlPressed && _isWinPressed && !_isRecording)
        {
            _isRecording = true;

            _ = Task.Run(async () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _overlayWindow.ShowWithAnimation();
                    _overlayWindow.UpdateText(string.Empty);
                });

                try
                {
                    await _xunfeiApi.ConnectAsync();
                    InitMicrophone();
                    _waveIn?.StartRecording();
                    Log.Information("开始录音...");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "连接讯飞 API 或初始化麦克风失败！");
                }
            });
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (e.Data.KeyCode is KeyCode.VcLeftControl or KeyCode.VcRightControl) _isCtrlPressed = false;
        if (e.Data.KeyCode is KeyCode.VcLeftMeta or KeyCode.VcRightMeta) _isWinPressed = false;

        if ((!_isCtrlPressed || !_isWinPressed) && _isRecording)
        {
            _isRecording = false;

            _ = Task.Run(async () =>
            {
                if (_waveIn is not null)
                {
                    _waveIn.DataAvailable -= OnAudioDataAvailable;
                    _waveIn.StopRecording();
                    _waveIn.Dispose();
                    _waveIn = null;
                }

                await _xunfeiApi.StopAndSendLastFrameAsync();
                var finalText = _currentRecognizedText;
                Log.Information("停止录音！");

                Dispatcher.UIThread.Post(() =>
                {
                    _ = _overlayWindow.HideWithAnimation();
                    if (string.IsNullOrWhiteSpace(finalText)) return;

                    var clipboard = TopLevel.GetTopLevel(_overlayWindow)?.Clipboard;
                    if (clipboard is not null)
                    {
                        _ = clipboard.SetTextAsync(finalText);
                        Log.Information("识别完成，已写入剪贴板并模拟粘贴。内容长度: {Length}", finalText.Length);
                    }

                    KeyboardSimulator.SimulateTextEntry(finalText);
                    _currentRecognizedText = string.Empty;
                });
            });
        }
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
}
