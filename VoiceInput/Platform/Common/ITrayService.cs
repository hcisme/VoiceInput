using System;

namespace VoiceInput.Platform;

public interface ITrayService : IDisposable
{
    void Initialize(string appName, Action<int, int> showMenuAt, Action exitApplication);
}
