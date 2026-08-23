namespace VoiceInput.Platform;

public interface ITextEntryService
{
    bool IsSupported { get; }

    void SimulateTextEntry(string text);
}
