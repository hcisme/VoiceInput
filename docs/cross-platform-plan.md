# VoiceInput 跨平台改造方案（按难度重新整理）

> 目标：在保留现有 Windows 版本和 `WinForms.NotifyIcon` 的基础上，新增当前 Ubuntu 26.04 环境的支持。  
> 当前环境：Ubuntu 26.04 LTS，x86_64，GNOME + Wayland，.NET SDK 10.0.110。

## 当前进度

- [x] 修正 `VoiceInput.csproj`：使用 `TargetFrameworks`，给 Windows 专属属性和 `NAudio` 依赖加上条件。
- [x] 更新 `AppPaths.cs` 注释，补充 Linux 路径说明。
- [x] 完成平台服务拆分方案：接口 + 平台目录 + 组合根，避免大量 `#if/#else`。
- [x] 创建 `Platform/Common` 接口、Windows/Linux 骨架实现，并在 csproj 中控制平台目录编译。
- [x] 把原有 Windows 托盘、录音、文字输入、全局热键逻辑迁入 Windows 平台服务，`App.axaml.cs` 改为通过接口调用。
- [x] 验证 Windows 目标和 Linux 目标均可构建：0 Warning / 0 Error。
- [x] 实现 Linux 托盘左键显示菜单，Windows 托盘仍保持原逻辑。
- [x] 实现 Linux 全局热键的 Wayland 版本：通过 XDG Desktop Portal `GlobalShortcuts` 监听 `Activated` / `Deactivated`，不再使用 X11/SharpHook。
- [x] Linux 录音服务使用 ALSA `default`/PipeWire，采集 16 kHz / 16 bit / mono PCM。
- [x] 将 `SharpHook` 依赖移入 Windows 目标，Linux 目标新增 `Tmds.DBus.Protocol`。
- [x] 将 Avalonia 升级到 12.1.1，并在 Linux 目标显式启用原生 Wayland 后端（`Avalonia.Wayland` + `UseWayland()`）。
- [x] 修复 Wayland Portal 调用方 app id 识别问题和 `handle_token` 含非法字符的问题。
- [x] 修复 Portal `Activated` / `Deactivated` 信号监听路径：信号由 `/org/freedesktop/portal/desktop` 发出，而不是 session 对象路径。
- [x] 提供 `scripts/run-linux-dev.sh`，通过 `systemd-run` 把进程放入 `app-com.chihaicheng.voiceinput-*` scope，并自动安装同名 `.desktop`，确保 Portal 能拿到正确 app id。
- [x] 在真实 GNOME Wayland 桌面会话中确认 Portal 注册成功；默认触发键改为 `CTRL+ALT+Z`，因为 GNOME 默认会吞掉 `Ctrl+Super_L`。
- [x] 修复 Linux 退出时 `DBusTrayIconImpl.WatchAsync()` 触发的 `TaskCanceledException`，不再记录为致命崩溃。
- [x] 延迟创建悬浮窗和托盘菜单窗口，并给 `TrayMenuWindow` 设置明确 `Title="VoiceInput"`，降低 GNOME Dock 出现未知窗口的概率。
- [x] Linux 文字输入完成后写入剪贴板，不显示“已复制”悬浮提示。
- [x] 实际按下默认热键 `Ctrl+Alt+Z`，验证 `Activated` / `Deactivated` 事件、录音启动/停止以及悬浮窗显示。

## 0. 先说明：GetStartedApp 里的 AddHandler 是什么

你在 `GetStartedApp` 里看到的代码类似：

```csharp
AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
```

它的作用是：

- 在 Avalonia 的 **窗口路由事件链** 上注册一个键盘事件处理器；
- `RoutingStrategies.Tunnel` 表示事件从窗口根部向下传递时先触发；
- `handledEventsToo: true` 表示即使子控件已经处理了按键，这里也继续接收。

但它不是“系统级全局热键”，它的监听范围是：

> 只有当这个 Avalonia 窗口获得键盘焦点时，OS 把按键事件发给应用，`AddHandler` 才能收到。

所以你测试 `GetStartedApp` 时能监听到，是因为当前那个窗口处于焦点状态。如果鼠标点到浏览器、编辑器或其他程序，再按 `Ctrl + Logo`，`AddHandler` 是收不到的。

VoiceInput 的场景不一样：用户按热键时，焦点通常在别的输入框里。因此 VoiceInput 必须使用系统级监听：

- Windows：SharpHook 或 Win32 Hook；
- Linux Wayland：XDG Desktop Portal `GlobalShortcuts`。

`AddHandler` 可以作为 Linux 上“临时调试/窗口内触发”的辅助方案，但不能作为最终全局热键方案。

## 1. 必须保留的约束

按你的要求，方案不再建议把 Windows 目标改成单一 `net10.0`，而是：

1. 保留 Windows 的 `WinForms.NotifyIcon`，避免 Avalonia 原生托盘在 Windows 上右键卡顿。
2. Linux 上使用 Avalonia 原生 `TrayIcon`，并且只处理左键，不处理右键。
3. Windows 的 `TargetFramework` 不删除，改为“多目标”：同时保留 Windows 目标，并新增 Linux 目标。

推荐 csproj 形态：

```xml
<TargetFrameworks>net10.0-windows10.0.17763.0;net10.0</TargetFrameworks>
```

这样 Windows 构建仍然使用原来的 Windows 目标，Linux 构建使用 `net10.0`。

## 2. 从简单到困难的改造顺序

| 顺序 | 改造项 | 难度 | 说明 |
| --- | --- | --- | --- |
| 1 | 路径和日志确认 | 简单 | 基本不用改 |
| 2 | csproj 多目标改造 | 简单 | 保留 Windows 目标，新增 Linux 目标 |
| 3 | 平台服务拆分 | 中等 | 让 Windows/Linux 代码不互相污染 |
| 4 | Linux 托盘 | 简单 | 左键弹出菜单，保留 Windows WinForms |
| 5 | Linux 文字输入 | 简单 | 优先走剪贴板降级 |
| 6 | Linux 录音 | 中等 | PortAudio 或 OpenAL 替换 NAudio |
| 7 | Linux 全局热键 | 中等到困难 | 当前环境只支持 Wayland，直接使用 XDG Desktop Portal |
| 8 | 构建、打包和 CI | 中等 | 新增 Linux 发布流程 |

下面按这个顺序展开。

## 3. 步骤 1：路径和日志确认

当前 `AppPaths` 使用：

```csharp
Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
```

在 Linux 上通常映射为：

```text
~/.config/VoiceInput/config/settings.json
~/.local/share/VoiceInput/logs/app_log.txt
```

这部分不需要改逻辑，只需要更新注释，避免继续写“%APPDATA%”这类 Windows 专用描述。

当前已完成：`AppPaths.cs` 注释已更新为同时描述 Windows 和 Linux 路径。

## 4. 步骤 2：csproj 多目标改造

当前关键内容：

```xml
<TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
<UseWindowsForms>true</UseWindowsForms>
```

已修正为：

```xml
<TargetFrameworks>net10.0-windows10.0.17763.0;net10.0</TargetFrameworks>
<UseWindowsForms Condition="'$(TargetFramework)' == 'net10.0-windows10.0.17763.0'">true</UseWindowsForms>
<ApplicationManifest Condition="'$(TargetFramework)' == 'net10.0-windows10.0.17763.0'">app.manifest</ApplicationManifest>
```

注意：这里必须是 `TargetFrameworks`（复数），不是 `TargetFramework`。

依赖也按平台分开：

```xml
<ItemGroup>
    <PackageReference Include="Avalonia" Version="12.1.1"/>
    <PackageReference Include="Avalonia.Desktop" Version="12.1.1"/>
    <PackageReference Include="Avalonia.Themes.Fluent" Version="12.1.1"/>
    <PackageReference Include="Avalonia.Fonts.Inter" Version="12.1.1"/>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.1"/>
    <PackageReference Include="Serilog" Version="4.3.1"/>
    <PackageReference Include="Serilog.Sinks.Console" Version="6.1.1"/>
    <PackageReference Include="Serilog.Sinks.File" Version="7.0.0"/>
</ItemGroup>

<!-- Windows 继续使用原来的 NAudio 和 SharpHook -->
<ItemGroup Condition="'$(TargetFramework)' == 'net10.0-windows10.0.17763.0'">
    <PackageReference Include="NAudio" Version="2.3.0" />
    <PackageReference Include="SharpHook" Version="7.1.2" />
    <PackageReference Include="SharpHook.R3" Version="7.1.2" />
    <PackageReference Include="SharpHook.Reactive" Version="7.1.2" />
</ItemGroup>

<!-- Linux 目前先使用 Tmds.DBus 处理 Wayland Portal；录音库待接入 -->
<ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="Tmds.DBus.Protocol" Version="0.94.1" />
    <PackageReference Include="Avalonia.Wayland" Version="12.1.1" />
</ItemGroup>
```

Linux 目标还需要在 `Program.cs` 中显式启用 Wayland 后端。Avalonia 12.1 的 `UsePlatformDetect()` 默认不会自动选择 Wayland：

```csharp
public static AppBuilder BuildAvaloniaApp()
{
    var builder = AppBuilder.Configure<App>()
        .UsePlatformDetect();

    if (OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 })
    {
        builder = builder.UseWayland();
    }

    return builder;
}
```

多目标下编译器会自动定义 `WINDOWS` 符号，因此代码里也可以这样分流：

```csharp
#if WINDOWS
    // 使用 WinForms.NotifyIcon、NAudio、user32.dll
#else
    // 使用 Avalonia TrayIcon、PortAudio/OpenAL、Linux 服务
#endif
```

这比直接 `RuntimeInformation.IsOSPlatform` 更适合编译期隔离，因为 Windows 专用 API 在 `net10.0` 目标下不会被编译进去。

## 5. 步骤 3：平台服务拆分

这里不要直接在 `App.axaml.cs` 里堆 `#if WINDOWS / #else`，而是用“接口 + 平台实现目录 + 一个组合根”的方式。

### 5.1 建议目录结构

```text
VoiceInput/
  Platform/
    Common/
      ITrayService.cs
      IAudioCaptureService.cs
      ITextEntryService.cs
      IGlobalHotkeyService.cs
      PlatformServices.cs
    Windows/
      WindowsTrayService.cs
      WindowsAudioCaptureService.cs
      WindowsTextEntryService.cs
      WindowsGlobalHotkeyService.cs
      PlatformServices.Windows.cs
    Linux/
      LinuxTrayService.cs
      LinuxAudioCaptureService.cs
      LinuxTextEntryService.cs
      LinuxGlobalHotkeyService.cs
      PlatformServices.Linux.cs
```

### 5.2 定义通用接口

```csharp
public interface ITrayService : IDisposable
{
    void Initialize();
    void ShowMenu();
    void Exit();
}

public interface IAudioCaptureService : IDisposable
{
    event Action<byte[], int>? DataAvailable;
    void Start();
    void Stop();
}

public interface ITextEntryService
{
    bool IsSupported { get; }
    void SimulateTextEntry(string text);
}

public interface IGlobalHotkeyService : IDisposable
{
    event Action? HotkeyPressed;
    event Action? HotkeyReleased;
    void Start();
}
```

`App.axaml.cs` 只依赖这些接口，不直接依赖 WinForms、NAudio、user32 或 Linux 录音库。

### 5.3 用 csproj 控制平台目录，而不是到处 #if

因为 SDK 项目默认会编译项目下所有 `.cs`，所以可以在 csproj 中按目标框架移除另一平台的实现：

```xml
<!-- Windows 目标不编译 Linux 实现 -->
<ItemGroup Condition="'$(TargetFramework)' == 'net10.0-windows10.0.17763.0'">
    <Compile Remove="Platform/Linux/**/*.cs" />
</ItemGroup>

<!-- Linux 目标不编译 Windows 实现 -->
<ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <Compile Remove="Platform/Windows/**/*.cs" />
</ItemGroup>
```

这样源码里基本不需要 `#if WINDOWS`，平台差异由“编译哪个目录”决定。

### 5.4 组合根：PlatformServices

`Platform/Common/PlatformServices.cs` 只声明一个 partial 静态类：

```csharp
public static partial class PlatformServices
{
}
```

Windows 目录里提供 Windows 实现：

```csharp
public static partial class PlatformServices
{
    public static ITrayService CreateTrayService() => new WindowsTrayService();
    public static IAudioCaptureService CreateAudioCaptureService() => new WindowsAudioCaptureService();
    public static ITextEntryService CreateTextEntryService() => new WindowsTextEntryService();
    public static IGlobalHotkeyService CreateGlobalHotkeyService() => new WindowsGlobalHotkeyService();
}
```

Linux 目录里提供 Linux 实现：

```csharp
public static partial class PlatformServices
{
    public static ITrayService CreateTrayService() => new LinuxTrayService();
    public static IAudioCaptureService CreateAudioCaptureService() => new LinuxAudioCaptureService();
    public static ITextEntryService CreateTextEntryService() => new LinuxTextEntryService();
    public static IGlobalHotkeyService CreateGlobalHotkeyService() => new LinuxGlobalHotkeyService();
}
```

`App` 初始化时只调用：

```csharp
_trayService = PlatformServices.CreateTrayService();
_audioCaptureService = PlatformServices.CreateAudioCaptureService();
_textEntryService = PlatformServices.CreateTextEntryService();
_globalHotkeyService = PlatformServices.CreateGlobalHotkeyService();
```

这就是所谓的“组合根”模式：平台差异集中在一个入口，业务代码保持干净。

### 5.5 和简单 #if 的对比

- 少量 `#if`：写起来快，但平台逻辑一多会很难维护。
- 接口 + 目录：前期多一点文件，但后续增加 macOS、改造 Wayland 或调整 Windows 逻辑时，只需增删平台目录中的类。
- 建议：只在 csproj 条件编译中控制平台目录，业务代码里不写 `#if`。

## 6. 步骤 4：Linux 托盘，只处理左键

Windows 侧保持 `WinForms.NotifyIcon`，不改。

Linux 侧使用 Avalonia `TrayIcon`。你要求“Ubuntu 上不要右键点击，左键点击显示”，所以 Linux 实现建议：

- 不使用需要右键的交互；
- 用 `TrayIcon.Command` 或 `Clicked` 事件处理左键；
- 左键触发后显示现有的 `TrayMenuWindow` 或一个 Linux 菜单；
- 不绑定右键菜单。

参考方向：

```xml
<TrayIcon Icon="/Assets/favicon.ico"
          ToolTipText="VoiceInput">
    <TrayIcon.Menu>
        <NativeMenu>
            <NativeMenuItem Header="退出" Command="{Binding ExitCommand}" />
        </NativeMenu>
    </TrayIcon.Menu>
</TrayIcon>
```

如果你发现 Ubuntu GNOME 的托盘图标不显示，通常是 GNOME 需要 AppIndicator 扩展。

## 7. 步骤 5：Linux 文字输入，先做剪贴板降级

Windows 保留当前 `user32.dll SendInput` 方案。

Linux 第一版不建议强行模拟键盘，因为：

- X11 下可以用 SharpHook `IEventSimulator.SimulateTextEntry`，但速度慢且不保证准确；
- Wayland 下普通应用不能可靠地向其他窗口注入按键。

所以 Linux 第一版推荐：

1. 把识别结果写入剪贴板；
2. 由用户手动 `Ctrl+V` 粘贴；
3. 用户手动 `Ctrl+V`。

当前代码已经会写剪贴板，因此这一步主要是把 `KeyboardSimulator.SimulateTextEntry` 替换成平台服务，Linux 实现直接跳过模拟。

## 8. 步骤 6：Linux 录音

Windows 继续使用 `NAudio.WaveInEvent`。

Linux 使用 `PortAudioSharp2` 或 OpenTK + OpenAL。录音格式保持讯飞要求：

```text
16000 Hz / 16 bit / mono / PCM
```

本机 Ubuntu 需要安装对应运行时：

```bash
sudo apt update
sudo apt install libportaudio2
```

如果最终选 OpenAL：

```bash
sudo apt install libopenal1
```

建议把录音封装成 `IAudioCaptureService`，Windows 实现继续用 NAudio，Linux 实现用 PortAudio/OpenAL。

## 9. 步骤 7：Linux 全局热键

当前这台机器是 Wayland，并且按你的要求不再支持 X11：

```text
XDG_SESSION_TYPE=wayland
WAYLAND_DISPLAY=wayland-0
```

SharpHook/libuiohook 不支持 Wayland，因此 Linux 全局热键直接接入 XDG Desktop Portal：

```text
org.freedesktop.portal.GlobalShortcuts
```

当前 `LinuxGlobalHotkeyService` 已经按这个方案实现，基本流程是：

1. 连接 D-Bus session bus；
2. 调用 `org.freedesktop.portal.GlobalShortcuts.CreateSession`；
3. 通过 `org.freedesktop.portal.Request::Response` 取得 `session_handle`；
4. 调用 `BindShortcuts` 注册：
   - shortcut id：`voiceinput.push-to-talk`
   - description：`开始/停止语音输入`
   - preferred_trigger：默认 `CTRL+ALT+Z`
5. 监听 `org.freedesktop.portal.GlobalShortcuts.Activated` 和 `Deactivated`，分别触发：
   - `IGlobalHotkeyService.HotkeyPressed`
   - `IGlobalHotkeyService.HotkeyReleased`
6. 应用退出时调用 `org.freedesktop.portal.Session.Close`。

关键依赖：

```xml
<ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="Tmds.DBus.Protocol" Version="0.94.1" />
    <PackageReference Include="Avalonia.Wayland" Version="12.1.1" />
</ItemGroup>
```

验证 Linux 目标：

```bash
cd /home/chihaicheng/Code/csharp/VoiceInput

dotnet build VoiceInput/VoiceInput/VoiceInput.csproj \
  -f net10.0 \
  -p:EnableWindowsTargeting=true
```

运行：

```bash
cd /home/chihaicheng/Code/csharp/VoiceInput
./scripts/run-linux-dev.sh
```

需要注意：

- 不要直接在 VS Code 的终端里用 `dotnet run` 启动。VS Code 会给子进程分配 `app-code-*` 这样的 systemd scope，XDG Desktop Portal 会因此把 app_id 识别成 `code`，导致全局快捷键注册到错误的应用上。
- `scripts/run-linux-dev.sh` 会：
  - 在 `~/.local/share/applications` 生成 `com.chihaicheng.voiceinput.desktop`；
  - 使用 `systemd-run --user --scope --unit=app-com.chihaicheng.voiceinput-<pid>` 启动；
  - 这样 Portal 会得到正确的 app id：`com.chihaicheng.voiceinput`。
- 第一次注册时 GNOME 可能会弹出“全局快捷键”授权/设置对话框，需要允许。
- `handle_token` / `session_handle_token` 必须是合法的 D-Bus object path 元素，不能包含 `-` 等字符；当前实现已经做字符替换。
- XDG Shortcuts 的 trigger 必须是“至少一个 modifier + 至少一个 key”。GNOME 默认把 `Super` 作为 Activities Overview 的 overlay key，`Ctrl+Super_L` 会被 Shell 吞掉，因此 Linux 默认注册 `CTRL+ALT+Z`。
- 如果确实想使用 Logo 键，可以先关闭 GNOME Overview 对 Super 的占用，再设置 `VOICEINPUT_HOTKEY_TRIGGER=CTRL+Super_L`。
- 不要在普通 X11/XWayland 路径下运行：本项目 Linux 目标现在显式调用 `UseWayland()`，直接使用原生 Wayland 后端。
- Linux 录音已使用 ALSA `default`/PipeWire 采集 16 kHz / 16 bit / mono PCM；按下默认热键后会启动录音并显示悬浮窗。

## 10. 步骤 8：构建、打包和 CI

新增 Linux 构建脚本，例如 `build/linux/build.sh`：

```bash
#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT_DIR="$REPO_ROOT/VoiceInput"
PUBLISH_DIR="$PROJECT_DIR/bin/Release/net10.0/linux-x64/publish"
VERSION="${VERSION:-0.0.0}"
DIST_DIR="$REPO_ROOT/dist/linux-x64"
OUTPUT_NAME="VoiceInput_linux-x64_v${VERSION}.tar.gz"

dotnet publish "$PROJECT_DIR/VoiceInput.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:DebugType=none \
    -p:Version="$VERSION"

rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"
cp -r "$PUBLISH_DIR"/* "$DIST_DIR/"

tar -C "$REPO_ROOT/dist" -czf "$REPO_ROOT/dist/$OUTPUT_NAME" linux-x64
echo "created $REPO_ROOT/dist/$OUTPUT_NAME"
```

Windows 构建仍保留原来的 PowerShell + Inno Setup 流程。

## 11. 现在建议先做什么

已经完成的：

1. csproj 已是 Windows + Linux 双目标。
2. 平台服务已经拆分，业务代码只依赖接口。
3. Linux 托盘左键显示菜单已经可用。
4. Linux 文字输入先走剪贴板降级。
5. Linux Wayland 全局热键已接入 XDG Desktop Portal。
6. Avalonia 已升级到 12.1.1，Linux 使用原生 Wayland 后端而不是 XWayland/X11。
7. Wayland 全局热键已修复 Portal 调用方 app id 识别问题，并修复了 request token 中 `-` 导致的 D-Bus 路径非法问题。
8. 增加 `scripts/run-linux-dev.sh`，解决从 VS Code 终端启动时 Portal 把 app_id 识别成 `code` 的问题。
9. Linux 退出时的托盘取消异常已降级为预期日志，不再作为致命崩溃。
10. 悬浮窗和托盘菜单窗口改为延迟创建，`TrayMenuWindow` 已设置 `Title="VoiceInput"`，减少 GNOME Dock 中出现未知窗口。
11. Linux 录音已改为通过 ALSA `default`/PipeWire 采集 16 kHz / 16 bit / mono PCM。
12. Linux 识别完成后会写入剪贴板，不显示额外提示。

接下来优先做：

1. 使用 `scripts/run-linux-dev.sh` 启动，实际按下默认热键 `Ctrl+Alt+Z` 验证按下/松开事件、录音启动/停止和悬浮窗显示。
2. 如果仍需在 VS Code 中调试，可以把调试命令包装成 `systemd-run` 或改用桌面启动器；Portal 的 app id 校验已经由脚本处理。
