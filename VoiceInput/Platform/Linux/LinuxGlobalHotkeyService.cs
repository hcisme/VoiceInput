using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Tmds.DBus.Protocol;

namespace VoiceInput.Platform.Linux;

/// <summary>
/// Wayland 下的全局热键实现。
/// Wayland 不允许客户端随意读取全局键盘输入，必须通过
/// XDG Desktop Portal 的 org.freedesktop.portal.GlobalShortcuts 接口注册。
/// </summary>
public sealed class LinuxGlobalHotkeyService : IGlobalHotkeyService
{
    private const string PortalDestination = "org.freedesktop.portal.Desktop";
    private const string PortalObjectPath = "/org/freedesktop/portal/desktop";
    private const string GlobalShortcutsInterface = "org.freedesktop.portal.GlobalShortcuts";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private const string SessionInterface = "org.freedesktop.portal.Session";

    private const string ShortcutId = "voiceinput.push-to-talk";
    private const string ShortcutDescription = "开始/停止语音输入";
    // XDG Shortcuts 的 trigger 必须包含“至少一个 modifier + 至少一个 key”。
    // LOGO 属于 modifier，不能单独作为 key；在 XKB 中“Logo/Windows/Super”键通常是 Super_L。
    // GNOME 默认把 Super 用作 Activities Overview 的 overlay key，会吞掉
    // Ctrl+Super_L，所以 Linux 默认使用 Ctrl+Alt+Z。若用户调整过 overlay key，
    // 可通过 VOICEINPUT_HOTKEY_TRIGGER=CTRL+Super_L 恢复 Logo 键方案。
    private const string DefaultPreferredTrigger = "CTRL+ALT+Z";

    // 该 id 不是通过 CreateSession options 传给 Portal 的；Portal 会从
    // 进程所属的 systemd scope（app-<ApplicationID>-<random>.scope）推断 app_id。
    // 因此实际运行时请使用 VoiceInput/scripts/run-linux-dev.sh，或通过对应
    // 的 .desktop 文件启动，避免在 VS Code 终端中被识别成 code。
    private const string PortalAppId = "com.chihaicheng.voiceinput";

    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private DBusConnection? _connection;
    private string? _sessionPath;
    private IDisposable? _activatedWatcher;
    private IDisposable? _deactivatedWatcher;
    private bool _hotkeyActive;
    private bool _disposed;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _cancellation is not null)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
        }

        _ = Task.Run(() => RunAsync(_cancellation.Token), _cancellation.Token);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        DBusConnection? connection;
        string? sessionPath;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _cancellation;
            connection = _connection;
            sessionPath = _sessionPath;
        }

        _activatedWatcher?.Dispose();
        _deactivatedWatcher?.Dispose();
        _activatedWatcher = null;
        _deactivatedWatcher = null;

        cancellation?.Cancel();

        if (connection is not null)
        {
            CloseSessionBestEffort(connection, sessionPath);
            connection.Dispose();
        }

        cancellation?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            WarnIfRunningInsideVsCodeScope();

            if (string.IsNullOrWhiteSpace(DBusAddress.Session))
            {
                Log.Warning("当前环境没有可用的 D-Bus session bus，Wayland 全局热键不会启动。");
                return;
            }

            var connection = new DBusConnection(DBusAddress.Session);

            lock (_gate)
            {
                if (_disposed)
                {
                    connection.Dispose();
                    return;
                }

                _connection = connection;
            }

            await connection.ConnectAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Log.Information("正在初始化 Wayland 全局热键（XDG Desktop Portal GlobalShortcuts）...");

            var createSessionToken = CreateToken("create-session");
            var createResponse = await WaitForRequestAsync(
                    connection,
                    createSessionToken,
                    () => CallCreateSessionAsync(connection, createSessionToken, CreateToken("session")),
                    cancellationToken)
                .ConfigureAwait(false);

            if (createResponse.Response != 0)
            {
                Log.Warning("Wayland 全局热键会话创建失败，Portal 返回状态: {Response}",
                    createResponse.Response);
                return;
            }

            var sessionPath = GetSessionHandle(createResponse.Results);
            if (string.IsNullOrWhiteSpace(sessionPath))
            {
                Log.Error("Wayland 全局热键会话创建成功，但 Portal 未返回 session_handle。");
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _sessionPath = sessionPath;
            }

            var bindToken = CreateToken("bind-shortcuts");
            var bindResponse = await WaitForRequestAsync(
                    connection,
                    bindToken,
                    () => CallBindShortcutsAsync(connection, sessionPath, bindToken),
                    cancellationToken)
                .ConfigureAwait(false);

            if (bindResponse.Response != 0)
            {
                Log.Warning("Wayland 全局热键注册被取消或失败，Portal 返回状态: {Response}",
                    bindResponse.Response);
                return;
            }

            var boundShortcutIds = GetBoundShortcutIds(bindResponse.Results);
            if (!boundShortcutIds.Contains(ShortcutId))
            {
                Log.Warning(
                    "Wayland 全局热键注册结果中没有包含 {ShortcutId}。" +
                    "如果当前是从 VS Code 终端启动，XDG Desktop Portal 会把 app_id 识别为 code。" +
                    "请改用 scripts/run-linux-dev.sh 启动，或通过 com.chihaicheng.voiceinput.desktop 启动。",
                    ShortcutId);
                return;
            }

            Log.Information(
                "Wayland 全局热键绑定完成。触发键: {PreferredTrigger}",
                PreferredTrigger);

            await WatchGlobalShortcutSignalsAsync(connection, sessionPath, cancellationToken)
                .ConfigureAwait(false);

            Log.Information("Wayland 全局热键已就绪，触发键: {PreferredTrigger}", PreferredTrigger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 应用退出时主动取消。
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Error(ex, "初始化 Wayland 全局热键失败。");
        }
    }

    private static void WarnIfRunningInsideVsCodeScope()
    {
        try
        {
            var cgroup = File.ReadAllText("/proc/self/cgroup");
            if (cgroup.Contains("app-code-", StringComparison.Ordinal))
            {
                Log.Warning(
                    "检测到当前进程位于 VS Code 的 app-code-* scope。" +
                    "XDG Desktop Portal 可能把 app_id 识别为 code，建议使用 scripts/run-linux-dev.sh 启动。");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取 /proc/self/cgroup 判断启动 scope 失败，可忽略。");
        }
    }

    private async Task<PortalResponse> WaitForRequestAsync(
        DBusConnection connection,
        string requestToken,
        Func<Task<string>> invokeAsync,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<PortalResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var watchers = new List<IDisposable>();

        try
        {
            var uniqueName = connection.UniqueName;
            if (string.IsNullOrWhiteSpace(uniqueName))
            {
                throw new InvalidOperationException("D-Bus 连接没有可用的 unique name。");
            }

            var expectedPath = BuildExpectedRequestPath(uniqueName, requestToken);
            watchers.Add(await WatchRequestResponseAsync(connection, expectedPath, completion)
                .ConfigureAwait(false));

            var actualPath = await invokeAsync().ConfigureAwait(false);
            if (!string.Equals(actualPath, expectedPath, StringComparison.Ordinal))
            {
                Log.Debug("Portal 返回的 Request 路径与预期不同，额外订阅实际路径: {ActualPath}",
                    actualPath);
                watchers.Add(await WatchRequestResponseAsync(connection, actualPath, completion)
                    .ConfigureAwait(false));
            }

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
        }
    }

    private async Task<IDisposable> WatchRequestResponseAsync(
        DBusConnection connection,
        string requestPath,
        TaskCompletionSource<PortalResponse> completion)
    {
        return await connection.WatchSignalAsync<PortalResponse>(
            sender: null,
            path: requestPath,
            @interface: RequestInterface,
            signal: "Response",
            reader: static (message, _) => ReadPortalResponse(message),
            handler: notification =>
            {
                if (notification.HasValue)
                {
                    completion.TrySetResult(notification.Value);
                    return;
                }

                if (notification.IsCompletion)
                {
                    var exception = notification.Exception;
                    if (exception is not null)
                    {
                        completion.TrySetException(exception);
                    }
                    else
                    {
                        completion.TrySetCanceled(CancellationToken.None);
                    }
                }
            },
            flags: ObserverFlags.None,
            emitOnCapturedContext: false,
            state: null).ConfigureAwait(false);
    }

    private async Task WatchGlobalShortcutSignalsAsync(
        DBusConnection connection,
        string sessionPath,
        CancellationToken cancellationToken)
    {
        _activatedWatcher = await connection.WatchSignalAsync<GlobalShortcutSignal>(
            sender: null,
            path: PortalObjectPath,
            @interface: GlobalShortcutsInterface,
            signal: "Activated",
            reader: static (message, _) => ReadGlobalShortcutSignal(message),
            handler: notification => OnActivationSignal(notification, sessionPath, isPressed: true),
            flags: ObserverFlags.None,
            emitOnCapturedContext: false,
            state: null).ConfigureAwait(false);

        _deactivatedWatcher = await connection.WatchSignalAsync<GlobalShortcutSignal>(
            sender: null,
            path: PortalObjectPath,
            @interface: GlobalShortcutsInterface,
            signal: "Deactivated",
            reader: static (message, _) => ReadGlobalShortcutSignal(message),
            handler: notification => OnActivationSignal(notification, sessionPath, isPressed: false),
            flags: ObserverFlags.None,
            emitOnCapturedContext: false,
            state: null).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void OnActivationSignal(
        Notification<GlobalShortcutSignal> notification,
        string expectedSessionPath,
        bool isPressed)
    {
        if (!notification.HasValue)
        {
            if (notification.IsCompletion && notification.Exception is not null)
            {
                Log.Error(notification.Exception, "Wayland 全局热键信号监听异常。");
            }

            return;
        }

        var signal = notification.Value;

        if (!string.Equals(signal.SessionPath, expectedSessionPath, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.Equals(signal.ShortcutId, ShortcutId, StringComparison.Ordinal))
        {
            return;
        }

        if (isPressed)
        {
            if (_hotkeyActive)
            {
                return;
            }

            _hotkeyActive = true;
            Log.Information("Wayland 全局热键按下: {ShortcutId}", signal.ShortcutId);
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            if (!_hotkeyActive)
            {
                return;
            }

            _hotkeyActive = false;
            Log.Information("Wayland 全局热键松开: {ShortcutId}", signal.ShortcutId);
            HotkeyReleased?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task<string> CallCreateSessionAsync(
        DBusConnection connection,
        string requestToken,
        string sessionToken)
    {
        var message = CreateCreateSessionMessage(connection, requestToken, sessionToken);

        return await connection.CallMethodAsync<string>(
                message,
                static (message, _) => message.GetBodyReader().ReadObjectPathAsString(),
                null)
            .ConfigureAwait(false);
    }

    private static async Task<string> CallBindShortcutsAsync(
        DBusConnection connection,
        string sessionPath,
        string requestToken)
    {
        var message = CreateBindShortcutsMessage(connection, sessionPath, requestToken);

        return await connection.CallMethodAsync<string>(
                message,
                static (message, _) => message.GetBodyReader().ReadObjectPathAsString(),
                null)
            .ConfigureAwait(false);
    }

    private static MessageBuffer CreateCreateSessionMessage(
        DBusConnection connection,
        string requestToken,
        string sessionToken)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: PortalDestination,
            path: PortalObjectPath,
            @interface: GlobalShortcutsInterface,
            member: "CreateSession",
            signature: "a{sv}",
            flags: MessageFlags.None);

        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["handle_token"] = VariantValue.String(requestToken),
            ["session_handle_token"] = VariantValue.String(sessionToken)
        });

        return writer.CreateMessage();
    }

    private static MessageBuffer CreateBindShortcutsMessage(
        DBusConnection connection,
        string sessionPath,
        string requestToken)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: PortalDestination,
            path: PortalObjectPath,
            @interface: GlobalShortcutsInterface,
            member: "BindShortcuts",
            signature: "oa(sa{sv})sa{sv}",
            flags: MessageFlags.None);

        writer.WriteObjectPath(sessionPath);

        var shortcutsStart = writer.WriteArrayStart(DBusType.Struct);
        writer.WriteStructureStart();
        writer.WriteString(ShortcutId);

        var shortcutOptionsStart = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("description");
        writer.WriteVariantString(ShortcutDescription);

        writer.WriteDictionaryEntryStart();
        writer.WriteString("preferred_trigger");
        writer.WriteVariantString(PreferredTrigger);
        writer.WriteDictionaryEnd(shortcutOptionsStart);
        writer.WriteArrayEnd(shortcutsStart);

        writer.WriteString(string.Empty);
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["handle_token"] = VariantValue.String(requestToken)
        });

        return writer.CreateMessage();
    }

    private static string PreferredTrigger
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("VOICEINPUT_HOTKEY_TRIGGER");
            return string.IsNullOrWhiteSpace(configured)
                ? DefaultPreferredTrigger
                : configured.Trim();
        }
    }

    private static void CloseSessionBestEffort(DBusConnection connection, string? sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            return;
        }

        try
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalDestination,
                path: sessionPath,
                @interface: SessionInterface,
                member: "Close",
                signature: string.Empty,
                flags: MessageFlags.NoReplyExpected);

            connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "关闭 Wayland 全局热键 Portal 会话时发生异常，可忽略。");
        }
    }

    private static PortalResponse ReadPortalResponse(Message message)
    {
        var reader = message.GetBodyReader();
        var response = reader.ReadUInt32();
        var results = reader.ReadDictionaryOfStringToVariantValue();
        return new PortalResponse(response, results);
    }

    private static GlobalShortcutSignal ReadGlobalShortcutSignal(Message message)
    {
        var reader = message.GetBodyReader();
        var sessionPath = reader.ReadObjectPathAsString();
        var shortcutId = reader.ReadString();
        var timestamp = reader.ReadUInt64();
        var options = reader.ReadDictionaryOfStringToVariantValue();

        return new GlobalShortcutSignal(sessionPath, shortcutId, timestamp, options);
    }

    private static string? GetSessionHandle(Dictionary<string, VariantValue> results)
    {
        if (!results.TryGetValue("session_handle", out var value))
        {
            return null;
        }

        return value.Type == VariantValueType.String
            ? value.GetString()
            : null;
    }

    private static HashSet<string> GetBoundShortcutIds(Dictionary<string, VariantValue> results)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        if (!results.TryGetValue("shortcuts", out var shortcuts) ||
            shortcuts.Type != VariantValueType.Array)
        {
            Log.Debug("Wayland BindShortcuts 响应中没有 shortcuts 数组。Results keys: {Keys}",
                string.Join(", ", results.Keys));
            return ids;
        }

        Log.Debug("Wayland BindShortcuts 返回 shortcuts 数组，元素数: {Count}，ItemType: {ItemType}",
            shortcuts.Count, shortcuts.ItemType);

        for (var i = 0; i < shortcuts.Count; i++)
        {
            var shortcut = shortcuts.GetItem(i);
            if (shortcut.Type != VariantValueType.Struct || shortcut.Count < 1)
            {
                Log.Debug("Wayland BindShortcuts shortcuts[{Index}] 类型不是 struct 或字段不足: Type={Type}, Count={Count}",
                    i, shortcut.Type, shortcut.Count);
                continue;
            }

            var id = shortcut.GetItem(0);
            if (id.Type == VariantValueType.String)
            {
                ids.Add(id.GetString());
            }
            else
            {
                Log.Debug("Wayland BindShortcuts shortcuts[{Index}] 第 0 个字段不是字符串: {Type}",
                    i, id.Type);
            }
        }

        return ids;
    }

    private static string BuildExpectedRequestPath(string uniqueName, string requestToken)
    {
        var sender = uniqueName.TrimStart(':').Replace('.', '_');
        return $"{PortalObjectPath}/request/{sender}/{requestToken}";
    }

    private static string CreateToken(string purpose)
    {
        // D-Bus object path 元素只允许 [A-Za-z0-9_]，不能包含连字符等字符。
        var safePurpose = purpose.Replace('-', '_');
        return $"voiceinput_{safePurpose}_{Guid.NewGuid():N}";
    }

    private sealed record PortalResponse(
        uint Response,
        Dictionary<string, VariantValue> Results);

    private sealed record GlobalShortcutSignal(
        string SessionPath,
        string ShortcutId,
        ulong Timestamp,
        Dictionary<string, VariantValue> Options);
}
