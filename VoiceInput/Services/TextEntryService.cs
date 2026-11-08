using System;
using System.Runtime.InteropServices;
using Serilog;

namespace VoiceInput.Services;

public sealed partial class TextEntryService
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

    public void SimulateTextEntry(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new Input[text.Length * 2];
        var index = 0;

        foreach (var c in text)
        {
            inputs[index].type = InputKeyboard;
            inputs[index].U.ki.wScan = c;
            inputs[index].U.ki.dwFlags = KeyEventUnicode;
            index++;

            inputs[index].type = InputKeyboard;
            inputs[index].U.ki.wScan = c;
            inputs[index].U.ki.dwFlags = KeyEventUnicode | KeyEventKeyUp;
            index++;
        }

        var result = SendInput((uint)inputs.Length, inputs, Input.Size);

        if (result != 0) return;

        var errorCode = Marshal.GetLastPInvokeError();
        Log.Error("底层键盘模拟发送失败！错误码: {ErrorCode}", errorCode);
    }
}
