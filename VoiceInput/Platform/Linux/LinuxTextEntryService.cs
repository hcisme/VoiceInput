using System;

namespace VoiceInput.Platform.Linux;

public sealed class LinuxTextEntryService : ITextEntryService
{
    public bool IsSupported => false;

    public void SimulateTextEntry(string text)
    {
        throw new NotImplementedException();
    }
}
