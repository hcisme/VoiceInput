using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SharpHook;
using SharpHook.Data;

namespace VoiceInput.Services;

public sealed class GlobalHotkeyService
{
    private readonly Lock _stateLock = new();
    private SimpleGlobalHook? _globalHook;
    private bool _isCtrlPressed;
    private bool _isWinPressed;
    private bool _hotkeyActive;
    private bool _suppressWinRelease;
    private bool _disposed;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;

    public void Start()
    {
        lock (_stateLock)
        {
            if (_globalHook is not null || _disposed) return;

            var hook = new SimpleGlobalHook();
            hook.KeyPressed += OnKeyPressed;
            hook.KeyReleased += OnKeyReleased;

            try
            {
                _ = ObserveRunAsync(hook.RunAsync());
                _globalHook = hook;
            }
            catch (Exception ex)
            {
                hook.KeyPressed -= OnKeyPressed;
                hook.KeyReleased -= OnKeyReleased;
                hook.Dispose();
                Log.Error(ex, "全局热键服务启动失败");
            }
        }
    }

    public void Dispose()
    {
        SimpleGlobalHook? hook;

        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            hook = _globalHook;
            _globalHook = null;
        }

        hook?.Dispose();
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        EventHandler? handler = null;

        lock (_stateLock)
        {
            if (_disposed) return;

            if (IsCtrlKey(e.Data.KeyCode)) _isCtrlPressed = true;
            if (IsMetaKey(e.Data.KeyCode)) _isWinPressed = true;

            if (!_isCtrlPressed || !_isWinPressed) return;

            // Ctrl+Win 作为专用组合键，屏蔽 Win 键避免松开时弹出开始菜单。
            _suppressWinRelease = true;
            if (IsMetaKey(e.Data.KeyCode)) e.SuppressEvent = true;

            if (_hotkeyActive) return;

            _hotkeyActive = true;
            handler = HotkeyPressed;
        }

        RaiseSafely(handler, "热键按下");
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        EventHandler? handler = null;

        lock (_stateLock)
        {
            if (_disposed) return;

            if (IsMetaKey(e.Data.KeyCode) && _suppressWinRelease)
            {
                e.SuppressEvent = true;
                _suppressWinRelease = false;
            }

            if (IsCtrlKey(e.Data.KeyCode)) _isCtrlPressed = false;
            if (IsMetaKey(e.Data.KeyCode)) _isWinPressed = false;

            if (!_hotkeyActive || (_isCtrlPressed && _isWinPressed)) return;

            _hotkeyActive = false;
            handler = HotkeyReleased;
        }

        RaiseSafely(handler, "热键松开");
    }

    private void RaiseSafely(EventHandler? handler, string action)
    {
        if (handler is null) return;

        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理{Action}事件失败", action);
        }
    }

    private static async Task ObserveRunAsync(Task runTask)
    {
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "全局热键服务运行异常");
        }
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
