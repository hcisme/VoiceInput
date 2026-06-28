using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Serilog;

namespace VoiceInput.Utils;

public static partial class KeyboardSimulator
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
        [FieldOffset(0)] public Mouseinput mi;
        [FieldOffset(0)] public Keybdinput ki;
        [FieldOffset(0)] public Hardwareinput hi;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct Mouseinput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct Keybdinput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Hardwareinput
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
    
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static void SimulateTextEntry(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new List<Input>();

        foreach (var c in text)
        {
            var down = new Input { type = INPUT_KEYBOARD };
            down.U.ki.wScan = c;
            down.U.ki.dwFlags = KEYEVENTF_UNICODE;

            var up = new Input { type = INPUT_KEYBOARD };
            up.U.ki.wScan = c;
            up.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;

            inputs.Add(down);
            inputs.Add(up);
        }

        var result = SendInput((uint)inputs.Count, inputs.ToArray(), Input.Size);

        if (result != 0) return;
        var errorCode = Marshal.GetLastPInvokeError();
        Log.Error("底层键盘模拟发送失败！错误码: {ErrorCode}", errorCode);
    }
}