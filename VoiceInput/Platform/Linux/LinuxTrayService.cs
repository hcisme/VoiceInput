using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using VoiceInput.Utils;

namespace VoiceInput.Platform.Linux;

public sealed class LinuxTrayService : ITrayService
{
    private TrayIcon? _trayIcon;
    private Application _application = null!;
    private Action? _exitApplication;
    private bool _disposed;

    public void Initialize(string appName, Action<int, int> showMenuAt, Action exitApplication)
    {
        _exitApplication = exitApplication;
        _application = Application.Current
                       ?? throw new InvalidOperationException("Application.Current is null.");

        var menu = new NativeMenu();
        var exitItem = new NativeMenuItem
        {
            Header = "退出"
        };
        exitItem.Click += OnExitClicked;
        menu.Add(exitItem);

        var stream = AssetLoader.Open(new Uri($"avares://{AppPaths.AppName}/Assets/voiceinput.png"));
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = appName,
            Menu = menu
        };

        TrayIcon.SetIcons(_application, new TrayIcons { _trayIcon });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_trayIcon is null) return;

        TrayIcon.SetIcons(_application, null);
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        _exitApplication?.Invoke();
    }
}
