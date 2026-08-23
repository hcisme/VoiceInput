using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Serilog;

namespace VoiceInput.Platform.Windows;

public sealed partial class WindowsTextEntryService : ITextEntryService
{
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion U;
        public static int Size => Marshal.SizeOf<Input>();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeybdInput ki;
        [FieldOffset(0)] public HardwareInput hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private const uint InputKeyboard = 1;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;

    public bool IsSupported => OperatingSystem.IsWindows();

    public void SimulateTextEntry(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new List<Input>();

        foreach (var c in text)
        {
            var down = new Input { type = InputKeyboard };
            down.U.ki.wScan = c;
            down.U.ki.dwFlags = KeyEventUnicode;

            var up = new Input { type = InputKeyboard };
            up.U.ki.wScan = c;
            up.U.ki.dwFlags = KeyEventUnicode | KeyEventKeyUp;

            inputs.Add(down);
            inputs.Add(up);
        }

        var result = SendInput((uint)inputs.Count, inputs.ToArray(), Input.Size);

        if (result != 0) return;

        var errorCode = Marshal.GetLastPInvokeError();
        Log.Error("底层键盘模拟发送失败！错误码: {ErrorCode}", errorCode);
    }
}
