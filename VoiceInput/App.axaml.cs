using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NAudio.Wave;
using SharpHook;
using SharpHook.Data;
using VoiceInput.Api;
using VoiceInput.Utils;
using VoiceInput.Views;

namespace VoiceInput;

public partial class App : Application
{
    private VoiceOverlayWindow? _overlayWindow;
    private TaskPoolGlobalHook? _globalHook;
    private WaveInEvent? _waveIn;
    private XunfeiApi? _xunfeiApi;
    private string _currentRecognizedText = "";

    // 状态
    private bool _isCtrlPressed;
    private bool _isWinPressed;
    private bool _isRecording;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var config = ConfigManager.LoadConfig();
        _xunfeiApi = new XunfeiApi(config.AppId, config.ApiSecret, config.ApiKey);

        _xunfeiApi.onTextChanged += (text) =>
        {
            _currentRecognizedText = text;
            Dispatcher.UIThread.Post(() => { _overlayWindow?.UpdateText(text); });
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            StartKeyboardHook();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitMicrophone()
    {
        _waveIn = new WaveInEvent();
        _waveIn.WaveFormat = new WaveFormat(16000, 16, 1);
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
        if (e.Data.KeyCode is KeyCode.VcLeftControl or KeyCode.VcRightControl)
        {
            _isCtrlPressed = true;
        }

        if (e.Data.KeyCode is KeyCode.VcLeftMeta or KeyCode.VcRightMeta)
        {
            _isWinPressed = true;
        }

        if (_isCtrlPressed && _isWinPressed && !_isRecording)
        {
            _isRecording = true;
            _currentRecognizedText = "";

            _ = Task.Run(async () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ShowOverlayWindow();
                    _overlayWindow?.UpdateText("");
                });

                try
                {
                    await _xunfeiApi.ConnectAsync();
                    InitMicrophone();
                    _waveIn?.StartRecording();
                    Console.WriteLine("开始录音...");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("启动失败: " + ex.Message);
                }
            });
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (e.Data.KeyCode is KeyCode.VcLeftControl or KeyCode.VcRightControl)
        {
            _isCtrlPressed = false;
        }

        if (e.Data.KeyCode is KeyCode.VcLeftMeta or KeyCode.VcRightMeta)
        {
            _isWinPressed = false;
        }

        if ((!_isCtrlPressed || !_isWinPressed) && _isRecording)
        {
            _isRecording = false;

            _ = Task.Run(async () =>
            {
                if (_waveIn != null)
                {
                    _waveIn.StopRecording();
                    _waveIn.Dispose();
                    _waveIn = null;
                }

                await _xunfeiApi.StopAndSendLastFrameAsync();
                var finalText = _currentRecognizedText;
                Console.WriteLine("停止录音！");

                Dispatcher.UIThread.Post(async () =>
                {
                    HideOverlayWindow();

                    if (!string.IsNullOrWhiteSpace(finalText))
                    {
                        if (_overlayWindow != null)
                        {
                            var clipboard = TopLevel.GetTopLevel(_overlayWindow)?.Clipboard;
                            if (clipboard != null)
                            {
                                await clipboard.SetTextAsync(finalText);
                                Console.WriteLine("已写入剪贴板：" + finalText);
                            }
                        }

                        await Task.Delay(200);
                        KeyboardSimulator.SimulateCtrlV();
                    }
                });
            });
        }
    }

    private void ShowOverlayWindow()
    {
        _overlayWindow ??= new VoiceOverlayWindow();
        _overlayWindow.Show();
    }

    private void HideOverlayWindow()
    {
        _overlayWindow?.Hide();
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        _ = _xunfeiApi.SendAudioDataAsync(e.Buffer, e.BytesRecorded);

        float maxVolume = 0;
        for (var i = 0; i < e.BytesRecorded; i += 2)
        {
            var sample = BitConverter.ToInt16(e.Buffer, i);
            // 0.0 ~ 1.0
            var val = Math.Abs(sample / 32768f);
            if (val > maxVolume) maxVolume = val;
        }

        Dispatcher.UIThread.Post(() => { _overlayWindow?.UpdateVolume(maxVolume); });
    }

    public void ExitApplication(object? sender, EventArgs e)
    {
        _globalHook?.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}