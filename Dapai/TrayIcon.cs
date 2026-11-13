using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Dapai;

internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = 0x8001;
    private const int Add = 0, Modify = 1, Delete = 2, SetFocus = 3, SetVersion = 4;
    private const int ContextMenuMessage = 0x007B;
    private const int Select = 0x0400, KeySelect = 0x0401;
    private const int LeftButtonUp = 0x0202, RightButtonUp = 0x0205;

    private readonly HwndSource source;
    private readonly ContextMenu menu;
    private readonly Action showWindow;
    private readonly byte[] iconFile;
    private readonly int taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private IntPtr icon;
    private int iconSize;
    private bool version4;
    private bool disposed;

    private TrayIcon(Action showWindow, Action optimize, Action restore, Action exit)
    {
        this.showWindow = showWindow;
        var resource = Application.GetResourceStream(new Uri(App.IconUri))
            ?? throw new InvalidOperationException("未找到 Apex 图标资源。");
        using (resource.Stream)
        using (var buffer = new MemoryStream())
        {
            resource.Stream.CopyTo(buffer);
            iconFile = buffer.ToArray();
        }

        menu = CreateMenu(optimize, restore, showWindow, exit);
        source = new HwndSource(new HwndSourceParameters("DapaiTray")
        {
            WindowStyle = unchecked((int)0x80000000),
            Width = 1,
            Height = 1,
        });
        source.AddHook(OnMessage);
    }

    public bool IsAvailable { get; private set; }

    public static TrayIcon? TryCreate(Action showWindow, Action optimize, Action restore, Action exit)
    {
        TrayIcon? tray = null;
        try
        {
            tray = new TrayIcon(showWindow, optimize, restore, exit);
            tray.Register();
            return tray;
        }
        catch (Exception ex)
        {
            tray?.Dispose();
            AppLog.Write("初始化系统托盘失败。", ex);
            return null;
        }
    }

    private ContextMenu CreateMenu(Action optimize, Action restore, Action show, Action exit)
    {
        var styles = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/打π优化;component/TrayMenu.xaml"),
        };
        var context = new ContextMenu
        {
            Style = (Style)styles["TrayMenu"],
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
        };

        void AddItem(string text, string glyph, Action action)
        {
            var item = new MenuItem
            {
                Header = text,
                Style = (Style)styles["TrayMenuItem"],
                Icon = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 15,
                },
            };
            item.Click += (_, _) =>
            {
                context.IsOpen = false;
                context.Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
            };
            context.Items.Add(item);
        }

        AddItem("一键优化", "\uE945", optimize);
        AddItem("一键恢复", "\uE777", restore);
        AddItem("显示/打开", "\uE8A7", show);
        context.Items.Add(new Separator { Style = (Style)styles["TraySeparator"] });
        AddItem("退出", "\uE8BB", exit);
        context.Closed += (_, _) =>
        {
            if (IsAvailable)
            {
                var data = Data();
                Shell_NotifyIconW(SetFocus, ref data);
            }
        };
        return context;
    }

    private void Register()
    {
        UpdateIcon();
        var data = Data();
        data.Flags = 7; // Callback, icon, and tooltip.
        data.Callback = CallbackMessage;
        data.Tooltip = App.Title;
        if (!Shell_NotifyIconW(Add, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未接受托盘图标。");
        IsAvailable = true;
        data.Version = 4;
        version4 = Shell_NotifyIconW(SetVersion, ref data);
        if (version4)
        {
            data.Flags = 0x84; // Keep the standard tooltip with version-4 callbacks.
            Shell_NotifyIconW(Modify, ref data);
        }
    }

    private void UpdateIcon()
    {
        IntPtr taskbar = FindWindowW("Shell_TrayWnd", null);
        uint dpi = taskbar == IntPtr.Zero ? GetDpiForSystem() : GetDpiForWindow(taskbar);
        int size = GetSystemMetricsForDpi(49, dpi == 0 ? 96 : dpi);
        size = size > 0 ? size : 16;
        if (icon != IntPtr.Zero && size == iconSize)
            return;

        IntPtr replacement = LoadFrame(size);
        if (IsAvailable)
        {
            var data = Data();
            data.Flags = 2;
            data.Icon = replacement;
            if (!Shell_NotifyIconW(Modify, ref data))
            {
                DestroyIcon(replacement);
                throw new InvalidOperationException("无法刷新托盘图标。");
            }
        }
        if (icon != IntPtr.Zero)
            DestroyIcon(icon);
        icon = replacement;
        iconSize = size;
    }

    private unsafe IntPtr LoadFrame(int size)
    {
        using var reader = new BinaryReader(new MemoryStream(iconFile, writable: false));
        reader.ReadUInt32();
        int count = reader.ReadUInt16();
        int best = int.MaxValue, length = 0, offset = 0;
        for (int i = 0; i < count; i++)
        {
            int width = reader.ReadByte();
            if (width == 0)
                width = 256;
            reader.BaseStream.Position += 7;
            int frameLength = reader.ReadInt32();
            int frameOffset = reader.ReadInt32();
            int distance = Math.Abs(width - size) * 2 + (width < size ? 1 : 0);
            if (distance < best)
                (best, length, offset) = (distance, frameLength, frameOffset);
        }

        fixed (byte* frame = iconFile.AsSpan(offset, length))
        {
            IntPtr handle = CreateIconFromResourceEx(frame, (uint)length, true, 0x00030000, size, size, 0);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return handle;
        }
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            int notification = (int)(lParam.ToInt64() & 0xffff);
            if (notification == ContextMenuMessage || (!version4 && notification == RightButtonUp))
            {
                long coordinates = wParam.ToInt64();
                int x = unchecked((short)coordinates);
                int y = unchecked((short)(coordinates >> 16));
                bool keyboard = version4 && x == -1 && y == -1;
                source.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ShowMenu(keyboard));
            }
            else if (notification is Select or KeySelect || (!version4 && notification == LeftButtonUp))
                source.Dispatcher.BeginInvoke(DispatcherPriority.Input, showWindow);
            handled = true;
        }
        else if (message == taskbarCreated || message is 0x001A or 0x007E or 0x02E0)
        {
            bool restart = message == taskbarCreated;
            source.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (disposed)
                    return;
                try
                {
                    if (restart)
                    {
                        IsAvailable = false;
                        Register();
                    }
                    else
                        UpdateIcon();
                }
                catch (Exception ex)
                {
                    AppLog.Write("刷新系统托盘失败。", ex);
                }
            });
        }

        return IntPtr.Zero;
    }

    private void ShowMenu(bool keyboard)
    {
        if (disposed || !IsAvailable || menu.IsOpen)
            return;

        SetForegroundWindow(source.Handle);
        menu.IsOpen = true;
        if (keyboard)
        {
            menu.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!menu.IsOpen)
                    return;
                PositionAtTrayIcon();
                ((MenuItem)menu.Items[0]).Focus();
            });
        }
    }

    private void PositionAtTrayIcon()
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Window = source.Handle,
            Id = IconId,
        };
        if (Shell_NotifyIconGetRect(in identifier, out Rect tray) != 0
            || PresentationSource.FromVisual(menu) is not HwndSource popup
            || !GetWindowRect(popup.Handle, out Rect bounds))
            return;

        var anchor = new Point((tray.Left + tray.Right) / 2, (tray.Top + tray.Bottom) / 2);
        var size = new Point(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        const uint alignBottomCenterAndUseWorkArea = 0x24 | 0x10000;
        if (CalculatePopupWindowPosition(in anchor, in size, alignBottomCenterAndUseWorkArea, in tray, out Rect position))
            SetWindowPos(popup.Handle, IntPtr.Zero, position.Left, position.Top, 0, 0, 0x0015);
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = source.Handle,
        Id = IconId,
        Icon = icon,
    };

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        menu.IsOpen = false;
        if (IsAvailable)
        {
            var data = Data();
            Shell_NotifyIconW(Delete, ref data);
            IsAvailable = false;
        }
        source.Dispose();
        if (icon != IntPtr.Zero)
            DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Tooltip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point(int x, int y)
    {
        public int X = x, Y = y;
    }

    [DllImport("shell32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int operation, ref NotifyIconData data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(in NotifyIconIdentifier identifier, out Rect rect);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessageW(string name);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? title);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe IntPtr CreateIconFromResourceEx(byte* bits, uint length, bool icon,
        uint version, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool CalculatePopupWindowPosition(in Point anchor, in Point size, uint flags,
        in Rect exclude, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
