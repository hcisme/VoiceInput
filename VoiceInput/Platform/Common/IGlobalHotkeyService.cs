using System;

namespace VoiceInput.Platform;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? HotkeyPressed;
    event EventHandler? HotkeyReleased;

    void Start();
}
