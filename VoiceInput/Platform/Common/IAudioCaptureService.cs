using System;

namespace VoiceInput.Platform;

public interface IAudioCaptureService : IDisposable
{
    event Action<byte[], int>? DataAvailable;

    /// <summary>
    /// 启动录音。
    /// </summary>
    /// <returns>true 表示录音已成功启动；false 表示启动失败（例如设备被占用或不可用）。</returns>
    bool Start();
    void Stop();
}
