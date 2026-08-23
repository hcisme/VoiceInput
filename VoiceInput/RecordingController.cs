using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VoiceInput.Api;
using VoiceInput.Platform;

namespace VoiceInput;

/// <summary>
/// 录音会话控制器：管理一次"按下热键 → 录音 → 松开 → 识别 → 收尾"的完整会话状态机，
/// 以及音频发送管道。只负责逻辑与状态，UI 相关动作通过事件暴露给上层（App）处理。
/// </summary>
public sealed class RecordingController
{
    private readonly XunfeiApi _xunfeiApi;
    private readonly IAudioCaptureService _audioCaptureService;

    private readonly Lock _audioSendGate = new();
    private Task _audioSendTail = Task.CompletedTask;

    private string _currentRecognizedText = string.Empty;
    private readonly Lock _textLock = new();
    private int _recordingState = (int)RecordingState.Idle;

    /// <summary>开始录音：需要显示悬浮窗（空文字）。在后台线程触发。</summary>
    public event Action? OverlayShowRequested;

    /// <summary>需要收起悬浮窗（出错/提前终止）。在后台线程触发。</summary>
    public event Action? OverlayHideRequested;

    /// <summary>识别文字变化。在后台线程触发。</summary>
    public event Action<string>? TextUpdated;

    /// <summary>一次会话收尾完成，携带最终文字（可能为空）。在后台线程触发。</summary>
    public event Action<string>? SessionCompleted;

    public RecordingController(XunfeiApi xunfeiApi, IAudioCaptureService audioCaptureService)
    {
        _xunfeiApi = xunfeiApi;
        _audioCaptureService = audioCaptureService;
        _xunfeiApi.OnTextChanged += OnXunfeiTextChanged;
        _audioCaptureService.DataAvailable += OnAudioDataAvailable;
    }

    public bool IsRecording => Volatile.Read(ref _recordingState) != (int)RecordingState.Idle;

    public void HandleHotkeyPressed()
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
            OverlayShowRequested?.Invoke();

            try
            {
                await _xunfeiApi.ConnectAsync();

                // 连接期间若用户已松键（Stopping），立即终止，防止录音服务空转
                if (_recordingState == (int)RecordingState.Stopping)
                {
                    await _xunfeiApi.StopAndSendLastFrameAsync();
                    OverlayHideRequested?.Invoke();
                    Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
                    return;
                }

                if (!_audioCaptureService.Start())
                {
                    Log.Error("录音服务启动失败，无法开始录音");
                    Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
                    OverlayHideRequested?.Invoke();
                    return;
                }

                Interlocked.Exchange(ref _recordingState, (int)RecordingState.Recording);
                Log.Information("开始录音...");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "连接讯飞 API 或初始化麦克风失败！");
                // 异常时回退状态并收起界面
                Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
                OverlayHideRequested?.Invoke();
            }
        });
    }

    public void HandleHotkeyReleased()
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
                await DrainPendingAudioSendsAsync();
                await _xunfeiApi.StopAndSendLastFrameAsync();

                string finalText;
                lock (_textLock)
                {
                    finalText = _currentRecognizedText;
                    _currentRecognizedText = string.Empty;
                }

                Log.Information("停止录音");

                SessionCompleted?.Invoke(finalText);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止录音或发送最终帧失败");
                OverlayHideRequested?.Invoke();
            }
            finally
            {
                // 无论成功或异常，都必须归位到 Idle
                Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
            }
        });
    }

    /// <summary>
    /// 退出时调用：若正在录音，先停止采集、排空已入队音频、发送最终帧，再返回。
    /// 空闲时调用也会安全地立即返回。
    /// </summary>
    public async Task StopAndFinalizeAsync()
    {
        try
        {
            _audioCaptureService.Stop();
            await DrainPendingAudioSendsAsync();
            await _xunfeiApi.StopAndSendLastFrameAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出前优雅停止录音失败，继续退出");
        }
        finally
        {
            Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
        }
    }

    private void OnXunfeiTextChanged(string text)
    {
        lock (_textLock)
        {
            _currentRecognizedText = text;
        }

        TextUpdated?.Invoke(text);
    }

    private void OnAudioDataAvailable(byte[] buffer, int bytesRecorded)
    {
        var audioChunk = ArrayPool<byte>.Shared.Rent(bytesRecorded);
        Buffer.BlockCopy(buffer, 0, audioChunk, 0, bytesRecorded);
        _ = SendAudioSequentiallyAsync(audioChunk, bytesRecorded);
    }

    private Task SendAudioSequentiallyAsync(byte[] audioData, int length)
    {
        lock (_audioSendGate)
        {
            var previous = _audioSendTail;
            var current = SendAfterPreviousAsync(previous, audioData, length);
            _audioSendTail = current;
            return current;
        }
    }

    private async Task SendAfterPreviousAsync(Task previous, byte[] audioData, int length)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // 单次发送失败不应阻断后续音频；连接状态由 XunfeiApi 内部判断。
        }

        try
        {
            await _xunfeiApi.SendAudioDataAsync(audioData, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(audioData);
        }
    }

    private async Task DrainPendingAudioSendsAsync()
    {
        Task tail;
        lock (_audioSendGate)
        {
            tail = _audioSendTail;
        }

        try
        {
            await tail.ConfigureAwait(false);
        }
        catch
        {
            // 停录阶段仅确保已入队音频尽量发完，单次失败不阻塞最终帧。
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
