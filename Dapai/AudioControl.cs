using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Dapai;

internal sealed record AudioDevice(string Name, string Id);

internal static class AudioControl
{
    private static readonly Guid DeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid PolicyClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid AlternatePolicyClient = new("294935CE-F637-4E7C-A41B-AB255460B862");
    private static readonly Guid PolicyInterface = new("F8679F50-850A-41CF-9C72-430F290290C8");
    private static readonly Guid AlternatePolicyInterface = new("568B9108-44BF-40B4-9006-86AFE5B5A620");
    private const int NoDefaultEndpoint = unchecked((int)0x80070490);

    public static List<AudioDevice> ListDevices(out string? defaultId)
    {
        var enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(DeviceEnumerator, true)!)!;
        try
        {
            int result = enumerator.GetDefaultAudioEndpoint(0, 1, out IDevice? defaultDevice);
            try
            {
                if (result != NoDefaultEndpoint)
                    Check(result);
                defaultId = defaultDevice?.GetId();
            }
            finally
            {
                Release(defaultDevice);
            }

            var devices = new List<AudioDevice>();
            Check(enumerator.EnumAudioEndpoints(0, 1, out var collection));
            try
            {
                Check(collection.GetCount(out uint count));
                for (uint i = 0; i < count; i++)
                {
                    Check(collection.Item(i, out var device));
                    try
                    {
                        devices.Add(ReadDevice(device));
                    }
                    finally
                    {
                        Release(device);
                    }
                }
            }
            finally
            {
                Release(collection);
            }
            return devices;
        }
        finally
        {
            Release(enumerator);
        }
    }

    private static AudioDevice ReadDevice(IDevice device)
    {
        string id = device.GetId();
        Check(device.OpenPropertyStore(0, out var store));
        var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
        try
        {
            store.GetValue(in key, out PropVariant value);
            try
            {
                return new(value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? id : id, id);
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    public static unsafe void SetDefault(string id)
    {
        int hr = CoCreateInstance(in PolicyClient, IntPtr.Zero, 1, in PolicyInterface, out IntPtr policy);
        if (hr < 0)
            hr = CoCreateInstance(in AlternatePolicyClient, IntPtr.Zero, 1, in AlternatePolicyInterface, out policy);
        Check(hr);

        try
        {
            // These two IPolicyConfig variants have SetDefaultEndpoint at slot 13.
            var vtable = *(IntPtr**)policy;
            var setEndpoint = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtable[13];
            fixed (char* device = id)
                for (int role = 0; role < 3; role++)
                    Check(setEndpoint(policy, device, role));
        }
        finally
        {
            Marshal.Release(policy);
        }
    }

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    private static void Release(object? instance)
    {
        if (instance is not null)
            Marshal.ReleaseComObject(instance);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IDeviceCollection collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice? endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        void Activate(in Guid iid, uint context, IntPtr activationParameters, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetId();
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
