using System;
using System.Drawing;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace VoiceInput.Platform.Windows;

public sealed partial class WindowsTrayService : ITrayService
{
    private WinForms.NotifyIcon? _notifyIcon;
    private readonly object _disposeLock = new();
    private bool _disposed;
    private Action<int, int>? _showMenuAt;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    public void Initialize(string appName, Action<int, int> showMenuAt, Action exitApplication)
    {
        _showMenuAt = showMenuAt;

        var processPath = Environment.ProcessPath;
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = string.IsNullOrEmpty(processPath)
                ? SystemIcons.Application
                : Icon.ExtractAssociatedIcon(processPath),
            Text = appName,
            Visible = true
        };

        _notifyIcon.MouseClick += OnMouseClick;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_notifyIcon is null) return;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }

    private void OnMouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button != WinForms.MouseButtons.Right) return;
        if (_showMenuAt is null) return;

        if (GetCursorPos(out var pt))
        {
            _showMenuAt(pt.X, pt.Y);
        }
    }
}
