using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace Dapai;

internal static class DisplayControl
{
    public const string DefaultName = "仅第二屏幕";
    public static readonly IReadOnlyDictionary<string, uint> Modes = new Dictionary<string, uint>
    {
        ["仅电脑屏幕"] = 1,
        ["复制"] = 2,
        ["扩展"] = 4,
        [DefaultName] = 8,
    };

    private const uint AllPaths = 1;
    private const uint ActivePaths = 2;
    private const uint ApplyConfiguration = 0x80;
    private const uint SuppliedConfiguration = 0x20;
    private const uint AllowChanges = 0x400;

    public static Snapshot Capture()
    {
        var snapshot = Query(ActivePaths);
        if (snapshot.Paths.Length == 0)
            throw new InvalidOperationException("无法记录当前显示配置。");
        return snapshot;
    }

    public static bool HasMultipleDisplays()
    {
        if (GetSystemMetrics(80) >= 2)
            return true;

        return Query(AllPaths).Paths
            .Where(path => path.Target.Available != 0)
            .Select(path => (path.Target.Adapter, path.Target.Id))
            .Distinct()
            .Take(2)
            .Count() == 2;
    }

    public static void Apply(uint mode) =>
        Check(SetDisplayConfig(0, null, 0, null, ApplyConfiguration | mode));

    public static void Restore(Snapshot snapshot)
    {
        Check(SetDisplayConfig((uint)snapshot.Paths.Length, snapshot.Paths,
            (uint)snapshot.Modes.Length, snapshot.Modes,
            ApplyConfiguration | SuppliedConfiguration | AllowChanges));
    }

    private static Snapshot Query(uint flags)
    {
        const int insufficientBuffer = 122;
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

    internal sealed record Snapshot(PathInfo[] Paths, ModeInfo[] Modes);

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

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ModeInfo
    {
        public uint Type, Id;
        public AdapterId Adapter;
        // The native DISPLAYCONFIG_MODE_INFO union is 48 bytes and 8-byte aligned.
        private ulong alignment;
        private fixed byte payload[40];
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
        ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, [In] PathInfo[]? paths,
        uint modeCount, [In] ModeInfo[]? modes, uint flags);
}
