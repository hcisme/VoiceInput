using System;
using SharpHook;
using SharpHook.Data;

namespace VoiceInput.Platform.Windows;

public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private TaskPoolGlobalHook? _globalHook;
    private bool _isCtrlPressed;
    private bool _isWinPressed;
    private bool _hotkeyActive;
    private bool _disposed;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;

    public void Start()
    {
        if (_globalHook is not null || _disposed) return;

        _globalHook = new TaskPoolGlobalHook();
        _globalHook.KeyPressed += OnKeyPressed;
        _globalHook.KeyReleased += OnKeyReleased;
        _globalHook.RunAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _globalHook?.Dispose();
        _globalHook = null;
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (IsCtrlKey(e.Data.KeyCode)) _isCtrlPressed = true;
        if (IsMetaKey(e.Data.KeyCode)) _isWinPressed = true;

        if (!_isCtrlPressed || !_isWinPressed || _hotkeyActive) return;

        _hotkeyActive = true;
        HotkeyPressed?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (IsCtrlKey(e.Data.KeyCode)) _isCtrlPressed = false;
        if (IsMetaKey(e.Data.KeyCode)) _isWinPressed = false;

        if (!_hotkeyActive) return;
        if (_isCtrlPressed && _isWinPressed) return;

        _hotkeyActive = false;
        HotkeyReleased?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsCtrlKey(KeyCode keyCode)
    {
        return keyCode is KeyCode.VcLeftControl or KeyCode.VcRightControl;
    }

    private static bool IsMetaKey(KeyCode keyCode)
    {
        return keyCode is KeyCode.VcLeftMeta or KeyCode.VcRightMeta;
    }
}
