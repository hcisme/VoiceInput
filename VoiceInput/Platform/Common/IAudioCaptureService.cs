using System;

namespace VoiceInput.Platform;

public interface IAudioCaptureService : IDisposable
{
    event Action<byte[], int>? DataAvailable;

    void Start();
    void Stop();
}
