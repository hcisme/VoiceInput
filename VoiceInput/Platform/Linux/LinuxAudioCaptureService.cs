using System;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;

namespace VoiceInput.Platform.Linux;

/// <summary>
/// 使用 ALSA 的 default/PipeWire 设备采集音频。
/// 输出格式固定为 16000 Hz / 16 bit / mono PCM，与讯飞 API 要求一致。
/// </summary>
public sealed class LinuxAudioCaptureService : IAudioCaptureService
{
    private const string CaptureDevice = "default";

    private const int AudioSampleRate = 16000;
    private const int AudioChannels = 1;
    private const int AudioFormatS16Le = 2;
    private const int AudioAccessRwInterleaved = 3;
    private const int AudioStreamCapture = 1;
    private const uint AudioLatencyMicroseconds = 200_000;

    private const int FramesPerBuffer = 4096;

    private readonly object _gate = new();
    private IntPtr _pcm;
    private Thread? _captureThread;
    private volatile bool _running;
    private bool _disposed;

    public event Action<byte[], int>? DataAvailable;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _running || _captureThread is { IsAlive: true })
            {
                return;
            }

            var openResult = snd_pcm_open(
                out var pcm,
                CaptureDevice,
                AudioStreamCapture,
                mode: 0);

            if (openResult < 0)
            {
                Log.Error("Linux 录音服务打开 ALSA 设备失败。设备: {Device}，错误码: {ErrorCode}",
                    CaptureDevice, openResult);
                return;
            }

            var setParamsResult = snd_pcm_set_params(
                pcm,
                AudioFormatS16Le,
                AudioAccessRwInterleaved,
                AudioChannels,
                AudioSampleRate,
                softResample: 1,
                AudioLatencyMicroseconds);

            if (setParamsResult < 0)
            {
                Log.Error("Linux 录音服务设置 ALSA 参数失败。错误码: {ErrorCode}", setParamsResult);
                snd_pcm_close(pcm);
                return;
            }

            _pcm = pcm;
            _running = true;
            _captureThread = new Thread(() => CaptureLoop(pcm))
            {
                IsBackground = true,
                Name = "VoiceInput-LinuxAudioCapture"
            };
            _captureThread.Start();

            Log.Information("Linux 录音服务已启动。设备: {Device}，采样率: {SampleRate}Hz，格式: 16bit mono PCM",
                CaptureDevice, AudioSampleRate);
        }
    }

    public void Stop()
    {
        Thread? captureThread;
        IntPtr pcm;

        lock (_gate)
        {
            if (!_running && _pcm == IntPtr.Zero)
            {
                return;
            }

            _running = false;
            captureThread = _captureThread;
            _captureThread = null;
            pcm = _pcm;
            _pcm = IntPtr.Zero;
        }

        if (pcm != IntPtr.Zero)
        {
            // drop 可以让阻塞中的 snd_pcm_readi 尽快返回，避免 Join 长时间等待。
            snd_pcm_drop(pcm);
        }

        if (captureThread is { IsAlive: true } &&
            captureThread != Thread.CurrentThread)
        {
            captureThread.Join(TimeSpan.FromSeconds(1));
        }

        if (pcm != IntPtr.Zero)
        {
            snd_pcm_close(pcm);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }

    private void CaptureLoop(IntPtr pcm)
    {
        var buffer = new byte[FramesPerBuffer * AudioChannels * sizeof(short)];

        try
        {
            while (_running)
            {
                var frames = snd_pcm_readi(pcm, buffer, (nuint)FramesPerBuffer);

                if (frames > 0)
                {
                    var bytesRecorded = (int)frames * AudioChannels * sizeof(short);
                    DataAvailable?.Invoke(buffer, bytesRecorded);
                    continue;
                }

                if (frames == 0)
                {
                    continue;
                }

                if (frames is -4 or -11)
                {
                    // -EINTR 或 -EAGAIN，短暂等待后继续。
                    Thread.Sleep(1);
                    continue;
                }

                Log.Warning("Linux 录音读取失败，尝试恢复 PCM 流。错误码: {ErrorCode}", frames);
                var prepareResult = snd_pcm_prepare(pcm);
                if (prepareResult < 0)
                {
                    Log.Error("Linux 录音 PCM 流恢复失败。错误码: {ErrorCode}", prepareResult);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Linux 录音线程发生异常。");
        }
    }

    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_open(
        out IntPtr pcm,
        string name,
        int stream,
        int mode);

    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_set_params(
        IntPtr pcm,
        int format,
        int access,
        uint channels,
        uint rate,
        int softResample,
        uint latency);

    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern long snd_pcm_readi(
        IntPtr pcm,
        byte[] buffer,
        nuint frames);

    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_prepare(IntPtr pcm);

    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_drop(IntPtr pcm);

    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_close(IntPtr pcm);
}
