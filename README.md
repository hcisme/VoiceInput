# VoiceInput

> Windows「按住说话」语音输入工具，将你的语音实时识别为文字并直接输入到当前光标所在位置。

VoiceInput 是一个运行在系统托盘里的桌面小工具：**按住全局热键说话 → 松开后自动识别 → 文字写入剪贴板并模拟输入到当前聚焦的输入框**。适用于微信、QQ、文档、聊天框等一切可以打字的场景，解放双手、提升输入效率。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Release](https://github.com/hcisme/VoiceInput/actions/workflows/release.yml/badge.svg)](https://github.com/hcisme/VoiceInput/actions/workflows/release.yml)

---

## ✨ 功能特性

- 🎤 **按住说话（Push-to-Talk）**：按住全局热键开始录音，松开立即停止并识别，不需要手动点击按钮。
- 🪟 **实时悬浮窗**：录音时屏幕中央显示绿色麦克风悬浮窗，实时回显识别文字。
- 📋 **一键输入**：识别结果同时写入**剪贴板**，并**模拟键盘输入**到当前聚焦的输入框。
- 🧠 **讯飞实时语音识别**：基于讯飞 WebSocket 流式接口（`iat`，中文普通话），支持动态修正、自动标点、数字转阿拉伯数字。
- 🖥️ **Windows 专用**：支持 Windows 10 1809 及以上版本。
- 📌 **常驻托盘**：退出、状态管理统一收纳在系统托盘菜单中。
- 🔒 **单实例运行**：重复启动不会创建多个进程。
- 📝 **结构化日志**：基于 Serilog，崩溃与运行日志落盘，便于排查问题。

## 🖥️ 支持平台

| 平台 | 目标框架 | 录音 | 全局热键 | 文字输入 | 托盘 |
| --- | --- | --- | --- | --- | --- |
| Windows 10 1809+ | `net10.0-windows10.0.17763.0` | NAudio | SharpHook（Ctrl+Win） | SendInput 模拟键盘 + 剪贴板 | WinForms NotifyIcon |

## 🚀 快速开始

### 1. 获取安装包

从 [GitHub Releases](https://github.com/hcisme/VoiceInput/releases) 下载 Windows 安装包：

- **Windows**：`VoiceInput_Setup_v*.exe`（Inno Setup 安装程序，安装到 `%LOCALAPPDATA%\VoiceInput`）

### 2. 配置讯飞 API

VoiceInput 依赖 [讯飞开放平台](https://www.xfyun.cn/) 的「语音听写」服务，**首次使用需要注册讯飞账号并创建应用**，获取 `AppId`、`ApiSecret`、`ApiKey` 三项凭证。

**获取凭证步骤：**

1. 访问 [讯飞开放平台控制台](https://console.xfyun.cn/app/myapp)，注册并登录账号。
2. 点击 **新建应用**，创建完成后**进入该应用**。
3. 在应用页**左侧菜单**依次选择 **语音识别 → 语音听写**。
4. 找到 **Websocket 服务接口认证信息** 区域，即可看到三项凭证：
   - `APPID`
   - `APISecret`
   - `APIKey`
5. 将这三项填入下方配置文件（字段名对应见下）。
6. **重启 VoiceInput**，配置即生效。

> 💡 每个用户每天有 **500 次**免费调用额度，可满足日常语音输入使用。

程序首次启动会在 `%APPDATA%\VoiceInput\config\settings.json` 自动生成配置文件，打开并填入凭证后重启即可。

```json
{
  "AppId": "你的 AppId",
  "ApiSecret": "你的 ApiSecret",
  "ApiKey": "你的 ApiKey"
}
```

### 3. 使用

1. 启动 VoiceInput（自动最小化到系统托盘）。
2. **按住**默认热键开始说话。
3. 松开热键，识别结果将自动输入到当前光标所在的输入框。

### ⌨️ 默认热键

默认热键为 `Ctrl + Win`：同时按住两个键开始，松开任一键停止。

## 🛠️ 从源码构建

### 环境要求

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Windows 构建**：额外需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)（`build/windows/build.ps1` 会自动检测并在缺失时通过 chocolatey 安装）

### 本地运行

```bash
dotnet run --project VoiceInput
```

## 📁 项目结构

```text
VoiceInput/
├── Api/
│   └── XunFeiApi.cs            # 讯飞 WebSocket 流式识别客户端
├── Services/                   # Windows 服务（托盘/录音/热键/文字输入）
├── Utils/
│   ├── AppPaths.cs             # 路径与常量
│   ├── ConfigManager.cs        # 配置读写
│   └── LoggerManager.cs        # Serilog 初始化
├── Views/
│   ├── TrayMenuWindow.*        # 托盘菜单窗口
│   └── VoiceOverlayWindow.*    # 录音悬浮窗
├── App.axaml.cs                # 组合根：组装服务、生命周期、会话事件
├── RecordingController.cs      # 录音会话状态机与音频发送管道
├── Program.cs                  # 入口、单实例、全局异常处理
└── VoiceInput.csproj
```

**架构说明**：业务代码直接使用 `Services` 中的 Windows 实现，`RecordingController` 负责一次「按下 → 录音 → 松开 → 识别 → 收尾」的完整会话状态机。

## 🔧 常见问题（FAQ）

**Q：提示「讯飞 API 配置不完整」？**
请检查配置文件路径与三项凭证是否填写正确，注意程序需要**重启**后才生效。

**Q：识别结果没有输入到输入框？**
请确认当前聚焦的是可输入的文本框；`SendInput` 会将文字输入到焦点所在控件。

**Q：日志在哪里？**
日志路径为 `%LOCALAPPDATA%\VoiceInput\logs\app_log.txt`。

## 📄 免责声明

- VoiceInput 使用**讯飞开放平台**的语音听写服务，识别功能依赖你的讯飞账号配额与网络，讯飞服务可能产生费用，请以讯飞官方计费说明为准。
- 本项目与讯飞无任何隶属或合作关系，相关接口以讯飞官方文档为准。

## 📄 开源协议

本项目基于 [MIT License](LICENSE) 开源。你可以自由使用、修改、分发，包括商业用途，但需保留版权声明。

## 🙏 致谢

- [Avalonia](https://avaloniaui.net/) —— UI 框架
- [NAudio](https://github.com/naudio/NAudio) / [SharpHook](https://github.com/TolikPylypchuk/SharpHook) —— Windows 音频与全局热键
- [Serilog](https://serilog.net/) —— 日志框架
- [讯飞开放平台](https://www.xfyun.cn/) —— 语音识别能力
