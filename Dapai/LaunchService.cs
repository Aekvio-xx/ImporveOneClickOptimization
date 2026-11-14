using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace Dapai;

internal static class LaunchService
{
    public static string ResolvePath(string path) => Path.GetFullPath(path.Trim(), AppContext.BaseDirectory);

    public static void Launch(IEnumerable<LaunchItem> items, Action<string, string> warn)
    {
        HashSet<string>? running = null;
        foreach (var item in items)
        {
            if (!item.Enabled || string.IsNullOrWhiteSpace(item.Path))
                continue;

            string name = string.IsNullOrWhiteSpace(item.Name) ? "启动项" : item.Name.Trim();
            try
            {
                string path = ResolvePath(item.Path);
                running ??= RunningPaths();
                if (running.Contains(path))
                    continue;
                if (!File.Exists(path))
                {
                    AppLog.Write($"快捷启动路径不存在：{name} -> {path}");
                    warn("路径错误", $"找不到 {name} 的路径：\n{path}");
                    continue;
                }

                using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                running.Add(path);
                AppLog.Write($"已启动快捷程序：{name} -> {path}");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or Win32Exception or InvalidOperationException
                or UnauthorizedAccessException or NotSupportedException)
            {
                AppLog.Write($"启动快捷程序失败：{name}", ex);
                warn("启动失败", $"无法启动 {name}。\n{ex.Message}");
            }
        }
    }

    private static HashSet<string> RunningPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new StringBuilder(32768);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            using (var handle = OpenProcess(0x1000, false, process.Id))
            {
                if (handle.IsInvalid)
                    continue;

                buffer.Clear();
                int length = buffer.Capacity;
                if (QueryFullProcessImageNameW(handle, 0, buffer, ref length))
                    paths.Add(buffer.ToString());
            }
        }
        return paths;
    }

    public static ImageSource? ReadIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            string fullPath = ResolvePath(path);
            if (!File.Exists(fullPath))
                return null;
            if (Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && CountIcons(fullPath, -1, IntPtr.Zero, IntPtr.Zero, 0) == 0)
                return null;

            SHGetFileInfoW(fullPath, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100);
            if (info.Icon == IntPtr.Zero)
                return null;

            try
            {
                var icon = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                icon.Freeze();
                return icon;
            }
            finally
            {
                DestroyIcon(info.Icon);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or Win32Exception or InvalidOperationException
            or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Write("读取启动项图标失败。", ex);
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags,
        StringBuilder path, ref int length);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CountIcons(string file, int index, IntPtr large, IntPtr small, uint count);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
