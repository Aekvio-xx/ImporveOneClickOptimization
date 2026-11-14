using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Dapai;

internal static class DisplayControl
{
    public const string DefaultName = "仅第二屏幕";
    private const uint CloneMode = 2;
    private const uint ExtendMode = 4;
    public static readonly IReadOnlyDictionary<string, uint> Modes = new Dictionary<string, uint>
    {
        ["仅电脑屏幕"] = 1,
        ["复制"] = CloneMode,
        ["扩展"] = ExtendMode,
        [DefaultName] = 8,
    };

    private const uint AllPaths = 1;
    private const uint ActivePaths = 2;
    private const uint ApplyConfiguration = 0x80;
    private const uint SuppliedConfiguration = 0x20;
    private const uint SaveConfiguration = 0x200;
    private const uint AllowChanges = 0x400;
    private const uint QueryVirtualModeAware = 0x10;
    private const uint SetVirtualModeAware = 0x8000;
    private const uint QueryVirtualRefreshAware = 0x40;
    private const uint SetVirtualRefreshAware = 0x20000;
    private const uint SupportsVirtualMode = 0x8;
    private const uint BoostRefreshRate = 0x10;
    private const uint SourceMode = 1;
    private static readonly bool SupportsVirtualRefresh = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static Snapshot Capture()
    {
        var snapshot = Query(ActivePaths);
        snapshot.DesktopGroups();
        return snapshot;
    }

    public static bool HasMultipleDisplays(Snapshot current) =>
        current.Paths.Where(path => path.Target.Available != 0)
            .Select(path => (path.Target.Adapter, path.Target.Id)).Distinct().Take(2).Count() >= 2
        || AvailableTargets().Count >= 2;

    public static void Apply(uint mode) =>
        Check(SetDisplayConfig(0, null, 0, null, ApplyConfiguration | mode));

    public static async Task RestoreAsync(Snapshot snapshot)
    {
        var expected = snapshot.DesktopGroups();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (await TryRestoreAsync($"还原显示配置（第 {attempt} 次）", () => ApplySnapshot(snapshot), expected))
                return;
        }

        // Let Windows choose a compatible signal mode for each original target.
        var paths = (PathInfo[])snapshot.Paths.Clone();
        for (int i = 0; i < paths.Length; i++)
        {
            paths[i].Target.ModeIndex = uint.MaxValue;
            paths[i].Target.RefreshNumerator = 0;
            paths[i].Target.RefreshDenominator = 0;
            paths[i].Target.ScanLineOrdering = 0;
            paths[i].Flags &= ~BoostRefreshRate;
        }
        if (await TryRestoreAsync("使用兼容信号模式还原显示配置",
                () => ApplySnapshot(new(paths, snapshot.Modes)), expected))
            return;

        int desktopCount = expected.Values.Distinct().Count();
        if (expected.Count > 1 && (desktopCount == 1 || desktopCount == expected.Count)
            && AvailableTargets().SetEquals(expected.Keys))
        {
            uint mode = desktopCount == 1 ? CloneMode : ExtendMode;
            if (await TryRestoreAsync("使用 Windows 保存的显示模式恢复",
                    () => Apply(mode), expected, requireOriginalPositions: false))
                return;
        }

        throw new InvalidOperationException($"未能恢复优化前的 {expected.Count} 块屏幕。请确认原有显示器已连接并唤醒后重试。");
    }

    private static async Task<bool> TryRestoreAsync(string action, Action apply,
        Dictionary<(AdapterId Adapter, uint Id), (int X, int Y)> expected, bool requireOriginalPositions = true)
    {
        try
        {
            apply();
            int verified = 0;
            for (int check = 0; check < 5; check++)
            {
                await Task.Delay(250);
                var active = Query(ActivePaths);
                var current = active.DesktopGroups();
                if (active.Paths.All(path => path.Target.Available != 0)
                    && SameTopology(expected, current, requireOriginalPositions))
                {
                    if (++verified == 2)
                    {
                        AppLog.Write($"{action}成功，已确认 {current.Count} 块屏幕启用。");
                        return true;
                    }
                }
                else
                    verified = 0;
            }
            AppLog.Write($"{action}后，启用的屏幕、排列或复制分组与优化前不同。");
        }
        catch (Win32Exception ex)
        {
            AppLog.Write($"{action}失败，Windows 错误码 {ex.NativeErrorCode}。", ex);
            await Task.Delay(350);
        }
        return false;
    }

    private static bool SameTopology(Dictionary<(AdapterId Adapter, uint Id), (int X, int Y)> expected,
        Dictionary<(AdapterId Adapter, uint Id), (int X, int Y)> current, bool requireOriginalPositions)
    {
        if (expected.Count != current.Count || expected.Keys.Any(target => !current.ContainsKey(target)))
            return false;

        if (requireOriginalPositions)
            return expected.All(entry => current[entry.Key] == entry.Value);

        var groups = expected.GroupBy(entry => entry.Value, entry => current[entry.Key]);
        var positions = new HashSet<(int X, int Y)>();
        foreach (var group in groups)
        {
            var position = group.First();
            if (group.Any(value => value != position) || !positions.Add(position))
                return false;
        }
        return true;
    }

    private static void ApplySnapshot(Snapshot snapshot) =>
        Check(SetDisplayConfig((uint)snapshot.Paths.Length, snapshot.Paths,
            (uint)snapshot.Modes.Length, snapshot.Modes,
            ApplyConfiguration | SuppliedConfiguration | SaveConfiguration | AllowChanges | SetVirtualModeAware
            | (SupportsVirtualRefresh ? SetVirtualRefreshAware : 0)));

    private static HashSet<(AdapterId Adapter, uint Id)> AvailableTargets() =>
        Query(AllPaths).Paths.Where(path => path.Target.Available != 0)
            .Select(path => (path.Target.Adapter, path.Target.Id)).ToHashSet();

    private static Snapshot Query(uint flags)
    {
        const int insufficientBuffer = 122;
        flags |= QueryVirtualModeAware | (SupportsVirtualRefresh ? QueryVirtualRefreshAware : 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount));
            var paths = new PathInfo[Math.Max(pathCount, 1)];
            var modes = new ModeInfo[Math.Max(modeCount, 1)];
            int error = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (error == insufficientBuffer)
                continue;
            Check(error);
            Array.Resize(ref paths, checked((int)pathCount));
            Array.Resize(ref modes, checked((int)modeCount));
            return new(paths, modes);
        }
        throw new Win32Exception(insufficientBuffer);
    }

    private static void Check(int error)
    {
        if (error != 0)
            throw new Win32Exception(error);
    }

    internal sealed record Snapshot(PathInfo[] Paths, ModeInfo[] Modes)
    {
        public Dictionary<(AdapterId Adapter, uint Id), (int X, int Y)> DesktopGroups()
        {
            const int invalidData = 13;
            if (Paths.Length == 0)
                throw new Win32Exception(invalidData, "无法读取当前启用的显示器。");

            var groups = new Dictionary<(AdapterId, uint), (int, int)>();
            foreach (var path in Paths)
            {
                uint index = (path.Flags & SupportsVirtualMode) != 0 ? path.Source.ModeIndex >> 16 : path.Source.ModeIndex;
                if (index >= Modes.Length || Modes[index].Type != SourceMode)
                    throw new Win32Exception(invalidData, "无法读取当前桌面布局。");
                groups[(path.Target.Adapter, path.Target.Id)] = (Modes[index].X, Modes[index].Y);
            }
            return groups;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdapterId
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceInfo
    {
        public AdapterId Adapter;
        public uint Id, ModeIndex, StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TargetInfo
    {
        public AdapterId Adapter;
        public uint Id, ModeIndex, Technology, Rotation, Scaling;
        public uint RefreshNumerator, RefreshDenominator, ScanLineOrdering;
        public int Available;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public SourceInfo Source;
        public TargetInfo Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct ModeInfo
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public AdapterId Adapter;
        // Source mode's desktop position inside the native 48-byte mode union.
        [FieldOffset(28)] public int X;
        [FieldOffset(32)] public int Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
        ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, [In] PathInfo[]? paths,
        uint modeCount, [In] ModeInfo[]? modes, uint flags);
}
