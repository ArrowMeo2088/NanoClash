using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clash.Tun.Windows;

/// <summary>WinTUN adapter + session for L3 IPv4 packet I/O.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WintunDevice : IDisposable
{
    public const string AdapterName = "NanoClash";
    public const string TunnelType = "Wintun";

    private readonly WintunNative _api;
    private IntPtr _adapter;
    private IntPtr _session;
    private IntPtr _readEvent;
    private bool _disposed;

    public ulong Luid { get; private set; }

    private WintunDevice(WintunNative api) => _api = api;

    public static WintunDevice Create(WintunNative api)
    {
        var dev = new WintunDevice(api);
        // Prefer create; if a previous crash left the adapter, open it.
        Marshal.SetLastPInvokeError(0);
        dev._adapter = api.CreateAdapter(AdapterName, TunnelType, IntPtr.Zero);
        if (dev._adapter == IntPtr.Zero)
        {
            var createErr = Marshal.GetLastPInvokeError();
            Marshal.SetLastPInvokeError(0);
            dev._adapter = api.OpenAdapter(AdapterName);
            if (dev._adapter == IntPtr.Zero)
                throw new Win32Exception(createErr, "WintunCreateAdapter/OpenAdapter failed");
        }

        api.GetAdapterLuid(dev._adapter, out var luid);
        dev.Luid = luid;

        Marshal.SetLastPInvokeError(0);
        dev._session = api.StartSession(dev._adapter, WintunNative.DefaultRingCapacity);
        if (dev._session == IntPtr.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            api.CloseAdapter(dev._adapter);
            dev._adapter = IntPtr.Zero;
            throw new Win32Exception(err, "WintunStartSession failed");
        }

        dev._readEvent = api.GetReadWaitEvent(dev._session);
        _ = api.GetRunningDriverVersion();
        return dev;
    }

    /// <summary>Copy one received packet into <paramref name="buffer"/>; returns length or 0 if empty.</summary>
    public int TryReceive(byte[] buffer, out bool sessionClosed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        sessionClosed = false;
        // Clear so a stale last-error cannot masquerade as a real failure when the ring is empty.
        Marshal.SetLastPInvokeError(0);
        var ptr = _api.ReceivePacket(_session, out var size);
        if (ptr == IntPtr.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            // Empty ring (normal). Also treat 0 as empty — some interop paths lose SetLastError.
            if (err is WintunNative.ErrorNoMoreItems or WintunNative.ErrorSuccess)
                return 0;
            if (err == WintunNative.ErrorHandleEof)
            {
                sessionClosed = true;
                return 0;
            }

            throw new Win32Exception(err, "WintunReceivePacket failed");
        }

        try
        {
            var n = (int)size;
            if (n > buffer.Length)
            {
                // Drop oversize rather than truncating (would corrupt headers).
                return 0;
            }

            Marshal.Copy(ptr, buffer, 0, n);
            return n;
        }
        finally
        {
            _api.ReleaseReceivePacket(_session, ptr);
        }
    }

    public bool WaitForPacket(int timeoutMs)
    {
        if (_readEvent == IntPtr.Zero)
            return false;
        return WaitForSingleObject(_readEvent, (uint)timeoutMs) == 0;
    }

    public void Send(ReadOnlySpan<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packet.Length is 0 or > WintunNative.MaxIpPacketSize)
            return;
        Marshal.SetLastPInvokeError(0);
        var ptr = _api.AllocateSendPacket(_session, (uint)packet.Length);
        if (ptr == IntPtr.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err is WintunNative.ErrorBufferOverflow or WintunNative.ErrorSuccess)
                return;
            throw new Win32Exception(err, "WintunAllocateSendPacket failed");
        }

        unsafe
        {
            packet.CopyTo(new Span<byte>((void*)ptr, packet.Length));
        }

        _api.SendPacket(_session, ptr);
    }

    /// <summary>End the session so readers wake with EOF; adapter stays open until <see cref="Dispose"/>.</summary>
    public void EndSession()
    {
        if (_session == IntPtr.Zero)
            return;
        _api.EndSession(_session);
        _session = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        EndSession();

        if (_adapter != IntPtr.Zero)
        {
            _api.CloseAdapter(_adapter);
            _adapter = IntPtr.Zero;
        }
}

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
}
