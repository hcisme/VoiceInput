using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VoiceInput.Api;
using VoiceInput.Services;

namespace VoiceInput;

/// <summary>
/// 录音会话控制器：管理一次"按下热键 → 录音 → 松开 → 识别 → 收尾"的完整会话状态机，
/// 以及音频发送管道。只负责逻辑与状态，UI 相关动作通过事件暴露给上层（App）处理。
/// </summary>
public sealed class RecordingController
{
    private readonly XunfeiApi _xunfeiApi;
    private readonly AudioCaptureService _audioCaptureService;

    private readonly Lock _audioSendGate = new();
    private Task _audioSendTail = Task.CompletedTask;

    private string _currentRecognizedText = string.Empty;
    private readonly Lock _textLock = new();
    private int _recordingState = (int)RecordingState.Idle;
    private int _sessionVersion;
    private int _isFinalizing;
    private readonly Lock _captureGate = new();

    /// <summary>开始录音：需要显示悬浮窗（空文字）。在后台线程触发。</summary>
    public event Action? OverlayShowRequested;

    /// <summary>需要收起悬浮窗（出错/提前终止）。在后台线程触发。</summary>
    public event Action? OverlayHideRequested;

    /// <summary>识别文字变化。在后台线程触发。</summary>
    public event Action<string>? TextUpdated;

    /// <summary>一次会话收尾完成，携带最终文字（可能为空）。在后台线程触发。</summary>
    public event Action<string>? SessionCompleted;

    public RecordingController(XunfeiApi xunfeiApi, AudioCaptureService audioCaptureService)
    {
        _xunfeiApi = xunfeiApi;
        _audioCaptureService = audioCaptureService;
        _xunfeiApi.OnTextChanged += OnXunfeiTextChanged;
        _audioCaptureService.DataAvailable += OnAudioDataAvailable;
    }

    public bool IsRecording => Volatile.Read(ref _recordingState) != (int)RecordingState.Idle;

    private bool IsFinalizing => Volatile.Read(ref _isFinalizing) != 0;

    private bool IsCurrentSession(int version) =>
        Volatile.Read(ref _sessionVersion) == version;

    private bool TrySetState(RecordingState expected, RecordingState next) =>
        Interlocked.CompareExchange(
            ref _recordingState,
            (int)next,
            (int)expected) == (int)expected;

    private async Task CloseApiQuietlyAsync()
    {
        try
        {
            await _xunfeiApi.CloseAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "关闭讯飞连接失败");
        }
    }

    public void HandleHotkeyPressed()
    {
        if (IsFinalizing) return;
        if (!TrySetState(RecordingState.Idle, RecordingState.Connecting)) return;

        var version = Interlocked.Increment(ref _sessionVersion);

        // 只在真正开始录音时清空
        lock (_textLock)
        {
            _currentRecognizedText = string.Empty;
        }

        OverlayShowRequested?.Invoke();

        _ = Task.Run(async () =>
        {
            // Task.Run 可能延迟启动；若此时已松键或退出，不要再建立连接。
            if (!IsCurrentSession(version) ||
                IsFinalizing ||
                Volatile.Read(ref _recordingState) != (int)RecordingState.Connecting)
            {
                return;
            }

            try
            {
                await _xunfeiApi.ConnectAsync();

                // 连接期间如果已经松键或退出，由松键/退出路径负责清理，避免误关新会话。
                if (!IsCurrentSession(version)) return;
                if (IsFinalizing ||
                    Volatile.Read(ref _recordingState) != (int)RecordingState.Connecting)
                {
                    await CloseApiQuietlyAsync();
                    return;
                }

                // 先进入 Recording，再启动采集；松键能立即观察到该状态。
                if (!TrySetState(RecordingState.Connecting, RecordingState.Recording))
                {
                    await CloseApiQuietlyAsync();
                    return;
                }

                bool started;
                lock (_captureGate)
                {
                    if (IsFinalizing ||
                        Volatile.Read(ref _recordingState) != (int)RecordingState.Recording)
                    {
                        return;
                    }

                    started = _audioCaptureService.Start();
                }

                if (!started)
                {
                    Log.Error("录音服务启动失败，无法开始录音");
                    if (IsCurrentSession(version) &&
                        TrySetState(RecordingState.Recording, RecordingState.Idle))
                    {
                        await CloseApiQuietlyAsync();
                        OverlayHideRequested?.Invoke();
                    }
                    return;
                }

                Log.Information("开始录音...");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "连接讯飞 API 或初始化麦克风失败！");

                // 只清理当前会话；过期会话交给松键/退出路径处理。
                if (!IsCurrentSession(version)) return;

                await CloseApiQuietlyAsync();
                if (TrySetState(RecordingState.Connecting, RecordingState.Idle) ||
                    TrySetState(RecordingState.Recording, RecordingState.Idle))
                {
                    OverlayHideRequested?.Invoke();
                }
            }
        });
    }

    public void HandleHotkeyReleased()
    {
        if (IsFinalizing) return;

        // 循环处理 Connecting/Recording 与按下任务之间的竞争，避免漏掉松键。
        while (true)
        {
            var state = Volatile.Read(ref _recordingState);
            if (state is (int)RecordingState.Idle or (int)RecordingState.Stopping) return;

            if (state == (int)RecordingState.Connecting)
            {
                if (Interlocked.CompareExchange(
                        ref _recordingState,
                        (int)RecordingState.Stopping,
                        (int)RecordingState.Connecting) != (int)RecordingState.Connecting)
                {
                    continue;
                }

                Interlocked.Increment(ref _sessionVersion);
                _ = Task.Run(async () =>
                {
                    OverlayHideRequested?.Invoke();
                    await CloseApiQuietlyAsync();
                    Interlocked.CompareExchange(
                        ref _recordingState,
                        (int)RecordingState.Idle,
                        (int)RecordingState.Stopping);
                });
                return;
            }

            if (state == (int)RecordingState.Recording)
            {
                if (Interlocked.CompareExchange(
                        ref _recordingState,
                        (int)RecordingState.Stopping,
                        (int)RecordingState.Recording) != (int)RecordingState.Recording)
                {
                    continue;
                }

                Interlocked.Increment(ref _sessionVersion);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        lock (_captureGate)
                        {
                            _audioCaptureService.Stop();
                        }

                        await DrainPendingAudioSendsAsync();
                        await _xunfeiApi.StopAndSendLastFrameAsync();

                        string finalText;
                        lock (_textLock)
                        {
                            finalText = _currentRecognizedText;
                            _currentRecognizedText = string.Empty;
                        }

                        Log.Information("停止录音");

                        if (!IsFinalizing) SessionCompleted?.Invoke(finalText);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "停止录音或发送最终帧失败");
                        OverlayHideRequested?.Invoke();
                    }
                    finally
                    {
                        await CloseApiQuietlyAsync();
                        // 无论成功或异常，都必须归位到 Idle
                        Interlocked.Exchange(ref _recordingState, (int)RecordingState.Idle);
                    }
                });
                return;
            }
        }
    }

    /// <summary>
    /// 退出时调用：若正在录音，先停止采集、排空已入队音频、发送最终帧，再返回。
    /// 空闲时调用也会安全地立即返回。
    /// </summary>
    public async Task StopAndFinalizeAsync()
    {
        Interlocked.Exchange(ref _isFinalizing, 1);
        Interlocked.Increment(ref _sessionVersion);
        Interlocked.Exchange(ref _recordingState, (int)RecordingState.Stopping);

        try
        {
            lock (_captureGate)
            {
                _audioCaptureService.Stop();
            }

            await DrainPendingAudioSendsAsync();
            await _xunfeiApi.StopAndSendLastFrameAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出前优雅停止录音失败，继续退出");
        }
        finally
        {
            await CloseApiQuietlyAsync();
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
