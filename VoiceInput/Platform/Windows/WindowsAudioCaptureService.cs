using System;
using NAudio.Wave;
using Serilog;

namespace VoiceInput.Platform.Windows;

public sealed class WindowsAudioCaptureService : IAudioCaptureService
{
    private const int AudioSampleRate = 16000;
    private const int AudioBitsPerSample = 16;
    private const int AudioChannels = 1;
    private const int AudioBufferMilliseconds = 40;

    private WaveInEvent? _waveIn;
    private readonly object _waveInLock = new();
    private bool _disposed;

    public event Action<byte[], int>? DataAvailable;

    public bool Start()
    {
        lock (_waveInLock)
        {
            if (_disposed) return false;
            if (_waveIn is not null) return true;

            try
            {
                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(AudioSampleRate, AudioBitsPerSample, AudioChannels),
                    BufferMilliseconds = AudioBufferMilliseconds
                };
                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.StartRecording();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Windows 录音服务启动失败");
                _waveIn?.Dispose();
                _waveIn = null;
                return false;
            }
        }
    }

    public void Stop()
    {
        WaveInEvent? waveIn;

        lock (_waveInLock)
        {
            waveIn = _waveIn;
            _waveIn = null;
        }

        if (waveIn is null) return;

        waveIn.DataAvailable -= OnDataAvailable;

        try
        {
            waveIn.StopRecording();
        }
        finally
        {
            waveIn.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_waveInLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        DataAvailable?.Invoke(e.Buffer, e.BytesRecorded);
    }
}
