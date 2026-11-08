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
    private const int InputBatchSize = 256;

    public void SimulateTextEntry(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new Input[Math.Min(text.Length, InputBatchSize / 2) * 2];
        var count = 0;

        foreach (var c in text)
        {
            if (count + 2 > inputs.Length)
            {
                if (!SendBatch(inputs, count)) return;
                count = 0;
            }

            inputs[count++] = CreateKeyInput(c, keyUp: false);
            inputs[count++] = CreateKeyInput(c, keyUp: true);
        }

        if (count > 0) SendBatch(inputs, count);
    }

    private static Input CreateKeyInput(char c, bool keyUp)
    {
        var input = new Input { type = InputKeyboard };
        input.U.ki.wScan = c;
        input.U.ki.dwFlags = keyUp ? KeyEventUnicode | KeyEventKeyUp : KeyEventUnicode;
        return input;
    }

    private static bool SendBatch(Input[] inputs, int count)
    {
        var sent = SendInput((uint)count, inputs, Input.Size);
        if (sent == (uint)count) return true;

        var errorCode = Marshal.GetLastPInvokeError();
        Log.Error("底层键盘模拟发送失败！期望 {Expected} 个事件，实际 {Sent} 个，错误码: {ErrorCode}",
            count, sent, errorCode);
        return false;
    }
}
