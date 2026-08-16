namespace VoiceInput.Platform;

public static partial class PlatformServices
{
    public static ITrayService CreateTrayService()
    {
        return new Linux.LinuxTrayService();
    }

    public static IAudioCaptureService CreateAudioCaptureService()
    {
        return new Linux.LinuxAudioCaptureService();
    }

    public static ITextEntryService CreateTextEntryService()
    {
        return new Linux.LinuxTextEntryService();
    }

    public static IGlobalHotkeyService CreateGlobalHotkeyService()
    {
        return new Linux.LinuxGlobalHotkeyService();
    }
}
