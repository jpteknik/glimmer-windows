// Settings › Performance › Graphics on Windows: the graphics adapter's name and video memory (DXGI), and the
// computer's memory. The Mac shows the Metal device and its recommended working set, which Windows has no counterpart for.
using System.Runtime.InteropServices;

namespace Glimmer.App.Platform;

public static class GraphicsInfo
{
    /// <summary>The first hardware adapter: its name and dedicated video memory; null when DXGI answers nothing.</summary>
    public static (string Name, ulong VideoMemory)? Adapter()
    {
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory is null) return null;
            try
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) >= 0 && adapter is not null; i++)
                {
                    try
                    {
                        if (adapter.GetDesc1(out var desc) < 0) continue;
                        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;   // Microsoft Basic Render Driver
                        return (desc.Description, (ulong)desc.DedicatedVideoMemory);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            // No DXGI (a server without graphics, or a remote session): nothing to show.
        }
        return null;
    }

    /// <summary>The computer's physical memory in bytes.</summary>
    public static ulong PhysicalMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullTotalPhys : 0;
    }

    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    // Only the slots that are called are declared with their real signatures; the others hold their place in the table.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1? adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1? factory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}
