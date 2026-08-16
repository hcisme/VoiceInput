namespace VoiceInput.Platform;

public static partial class PlatformServices
{
    public static ITrayService CreateTrayService()
    {
        return new Windows.WindowsTrayService();
    }

    public static IAudioCaptureService CreateAudioCaptureService()
    {
        return new Windows.WindowsAudioCaptureService();
    }

    public static ITextEntryService CreateTextEntryService()
    {
        return new Windows.WindowsTextEntryService();
    }

    public static IGlobalHotkeyService CreateGlobalHotkeyService()
    {
        return new Windows.WindowsGlobalHotkeyService();
    }
}
