using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clash.Tun.Windows;

/// <summary>Dynamic LoadLibrary bindings for wintun.dll (see Res/wintun/wintun.h).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WintunNative : IDisposable
{
    public const uint MinRingCapacity = 0x20000;
    public const uint MaxRingCapacity = 0x4000000;
    public const uint DefaultRingCapacity = 0x800000;
    public const int MaxIpPacketSize = 0xFFFF;

    public const int ErrorSuccess = 0;
    public const int ErrorNoMoreItems = 259;
    public const int ErrorHandleEof = 38;
    public const int ErrorBufferOverflow = 111;

    private readonly IntPtr _module;
    private bool _disposed;

    public WintunCreateAdapter CreateAdapter { get; }
    public WintunOpenAdapter OpenAdapter { get; }
    public WintunCloseAdapter CloseAdapter { get; }
    public WintunGetAdapterLuid GetAdapterLuid { get; }
    public WintunGetRunningDriverVersion GetRunningDriverVersion { get; }
    public WintunStartSession StartSession { get; }
    public WintunEndSession EndSession { get; }
    public WintunGetReadWaitEvent GetReadWaitEvent { get; }
    public WintunReceivePacket ReceivePacket { get; }
    public WintunReleaseReceivePacket ReleaseReceivePacket { get; }
    public WintunAllocateSendPacket AllocateSendPacket { get; }
    public WintunSendPacket SendPacket { get; }

    private WintunNative(IntPtr module)
    {
        _module = module;
        CreateAdapter = Load<WintunCreateAdapter>(module, "WintunCreateAdapter");
        OpenAdapter = Load<WintunOpenAdapter>(module, "WintunOpenAdapter");
        CloseAdapter = Load<WintunCloseAdapter>(module, "WintunCloseAdapter");
        GetAdapterLuid = Load<WintunGetAdapterLuid>(module, "WintunGetAdapterLUID");
        GetRunningDriverVersion = Load<WintunGetRunningDriverVersion>(module, "WintunGetRunningDriverVersion");
        StartSession = Load<WintunStartSession>(module, "WintunStartSession");
        EndSession = Load<WintunEndSession>(module, "WintunEndSession");
        GetReadWaitEvent = Load<WintunGetReadWaitEvent>(module, "WintunGetReadWaitEvent");
        ReceivePacket = Load<WintunReceivePacket>(module, "WintunReceivePacket");
        ReleaseReceivePacket = Load<WintunReleaseReceivePacket>(module, "WintunReleaseReceivePacket");
        AllocateSendPacket = Load<WintunAllocateSendPacket>(module, "WintunAllocateSendPacket");
        SendPacket = Load<WintunSendPacket>(module, "WintunSendPacket");
    }

    public static WintunNative LoadFrom(string dllPath)
    {
        var full = Path.GetFullPath(dllPath);
        if (!File.Exists(full))
            throw new FileNotFoundException(
                "wintun.dll not found under user data (start the app once or rebuild with Res/wintun/wintun.dll)",
                full);
        var mod = NativeLibrary.Load(full);
        try
        {
            return new WintunNative(mod);
        }
        catch
        {
            NativeLibrary.Free(mod);
            throw;
        }
    }

    private static T Load<T>(IntPtr module, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(module, name, out var addr) || addr == IntPtr.Zero)
            throw new EntryPointNotFoundException("Missing export: " + name);
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_module != IntPtr.Zero)
            NativeLibrary.Free(_module);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
    public delegate IntPtr WintunCreateAdapter(string name, string tunnelType, IntPtr requestedGuid);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
    public delegate IntPtr WintunOpenAdapter(string name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void WintunCloseAdapter(IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void WintunGetAdapterLuid(IntPtr adapter, out ulong luid);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
    public delegate uint WintunGetRunningDriverVersion();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
    public delegate IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void WintunEndSession(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr WintunGetReadWaitEvent(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
    public delegate IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
    public delegate IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void WintunSendPacket(IntPtr session, IntPtr packet);
}
