using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dapai;

internal static class WindowsSession
{
    public static void AllowExistingWindowActivation(string title)
    {
        IntPtr hwnd = FindWindowW(IntPtr.Zero, title);
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
                AllowSetForegroundWindow(pid);
        }
    }

    public static IntPtr ForegroundLayout()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return IntPtr.Zero;
        uint thread = GetWindowThreadProcessId(hwnd, out _);
        return thread == 0 ? IntPtr.Zero : GetKeyboardLayout(thread);
    }

    public static void ForceEnglish()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return;

        IntPtr english = LoadKeyboardLayoutW("00000409", 1);
        if (english == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        SetLayout(hwnd, english);
    }

    public static void RestoreLayout(IntPtr layout)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero && layout != IntPtr.Zero)
            SetLayout(hwnd, layout);
    }

    private static void SetLayout(IntPtr hwnd, IntPtr layout)
    {
        if (!PostMessageW(hwnd, 0x0050, IntPtr.Zero, layout))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowW(IntPtr className, string title);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint thread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadKeyboardLayoutW(string layout, uint flags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}

internal sealed class KeyboardBlocker : IDisposable
{
    private readonly HookProc callback;
    private IntPtr hook;
    private bool leftControl, rightControl, leftAlt, rightAlt, leftShift, rightShift;

    public KeyboardBlocker() => callback = OnKey;

    public bool BlockWin { get; private set; }
    public bool BlockAltShift { get; private set; }

    public void Set(bool blockWin, bool blockAltShift)
    {
        if ((blockWin || blockAltShift) && hook == IntPtr.Zero)
        {
            leftControl = KeyIsDown(0xA2);
            rightControl = KeyIsDown(0xA3);
            leftAlt = KeyIsDown(0xA4);
            rightAlt = KeyIsDown(0xA5);
            leftShift = KeyIsDown(0xA0);
            rightShift = KeyIsDown(0xA1);
            hook = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
            if (hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        BlockWin = blockWin;
        BlockAltShift = blockAltShift;
        if (!blockWin && !blockAltShift)
            Dispose();
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == 0)
        {
            int key = Marshal.ReadInt32(lParam);
            bool down = wParam.ToInt32() is 0x0100 or 0x0104;
            switch (key)
            {
                case 0xA0: leftShift = down; break;
                case 0xA1: rightShift = down; break;
                case 0xA2: leftControl = down; break;
                case 0xA3: rightControl = down; break;
                case 0xA4: leftAlt = down; break;
                case 0xA5: rightAlt = down; break;
            }

            bool isAltShift = (key is 0xA0 or 0xA1) && (leftAlt || rightAlt)
                || (key is 0xA4 or 0xA5) && (leftShift || rightShift);
            if (BlockWin && ((key is 0x5B or 0x5C) || (down && key == 0x1B && (leftControl || rightControl)))
                || (BlockAltShift && down && isAltShift))
                return new IntPtr(1);
        }

        return CallNextHookEx(hook, code, wParam, lParam);
    }

    private static bool KeyIsDown(int key) => GetAsyncKeyState(key) < 0;

    public void Dispose()
    {
        if (hook == IntPtr.Zero)
            return;
        if (UnhookWindowsHookEx(hook))
            hook = IntPtr.Zero;
        else
            AppLog.Write("卸载键盘钩子失败。", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
}
