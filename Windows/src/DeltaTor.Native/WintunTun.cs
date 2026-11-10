using System.Net;
using System.Runtime.InteropServices;
using DeltaTor.Core;
using DeltaTor.Core.Sstp;
using DeltaTor.Core.Util;

namespace DeltaTor.Native;

/// <summary>
/// The chain's data plane: a dedicated Wintun adapter driven straight over
/// Wintun's C API, the Windows counterpart of the <c>VpnService.Builder</c> +
/// <c>ParcelFileDescriptor</c> pair the Android build hands to the ported SSTP
/// core.
///
/// It is deliberately not the app's normal tun2socks adapter: the chain needs a
/// raw IP packet pipe (the kernels negotiate PPP itself), so this class only
/// creates an adapter with the address IPCP hands out, points the default route
/// and the IPv6 blackholes at it, and exposes read/write. Route bookkeeping is
/// shared with <see cref="ChainNetwork"/>, the exact same commands the loop
/// prevention code uses.
/// </summary>
public sealed class WintunTun : ISstpTun
{
    private const string Tag = "WintunTun";

    /// <summary>Session ring size: 4 MiB, inside Wintun's 128 KiB…64 MiB range.</summary>
    private const uint RingCapacity = 0x0040_0000;

    /// <summary>Fixed adapter GUID so the machine sees the same NLA entry every run.</summary>
    private static Guid FixedGuid = new("D3A7B0C1-5E4F-4A2B-9C8D-1E2F3A4B5C6D");

    private const int ErrorNoMoreItems = 259;
    private const int ErrorBufferOverflow = 111;
    private const uint WaitTimeout = 258;

    private readonly object _gate = new();
    private readonly List<string> _dns = new();

    private IntPtr _adapter;
    private IntPtr _session;
    private IntPtr _readEvent;
    private string? _address;
    private bool _routeAll;
    private int _mtu = 1400;
    private volatile bool _closed;

    // --- ISstpTun: the Builder half ------------------------------------------

    public void AddAddress(byte[] address, int prefixLength)
    {
        // IPCP hands out a single /32; the prefix argument is kept for parity
        // with the Android builder and ignored beyond that.
        _address = new IPAddress(address).ToString();
    }

    public void AddDnsServer(byte[] address)
    {
        var text = new IPAddress(address).ToString();
        lock (_gate)
        {
            if (!_dns.Contains(text)) _dns.Add(text);
        }
    }

    public void AddRoute(byte[] network, int prefixLength)
    {
        // The only route the chain asks for is the default one.
        if (prefixLength == 0 && network.All(b => b == 0)) _routeAll = true;
    }

    public void SetMtu(int mtu)
    {
        if (mtu > 0) _mtu = mtu;
    }

    public void SetBlocking(bool blocking)
    {
        // Wintun is poll-based; Read() blocks on the session's wait event
        // regardless, so there is nothing to switch here.
    }

    public ISstpTunSession? Establish()
    {
        lock (_gate)
        {
            if (_closed || _session != IntPtr.Zero) return null;
            if (_address is null)
            {
                AppLog.W(Tag, "establish without an IPCP address");
                return null;
            }

            var name = ChainNetwork.AdapterName;
            var guid = FixedGuid;
            var adapter = WintunCreateAdapter(name, "DeltaTor", ref guid);
            if (adapter == IntPtr.Zero)
            {
                AppLog.W(Tag,
                    $"create adapter failed ({Marshal.GetLastWin32Error()}); opening the existing one");
                adapter = WintunOpenAdapter(name);
            }

            if (adapter == IntPtr.Zero)
            {
                AppLog.W(Tag, $"adapter {name} is not available ({Marshal.GetLastWin32Error()})");
                return null;
            }

            _adapter = adapter;
            var session = WintunStartSession(adapter, RingCapacity);
            if (session == IntPtr.Zero)
            {
                AppLog.W(Tag, $"start session failed ({Marshal.GetLastWin32Error()})");
                WintunCloseAdapter(adapter);
                _adapter = IntPtr.Zero;
                return null;
            }

            _session = session;
            _readEvent = WintunGetReadWaitEvent(session);

            // Order matters: the address must exist before routes can point at
            // the adapter, and DNS is only meaningful once it is configured.
            ChainNetwork.SetAdapterAddress(_address);
            ChainNetwork.SetAdapterMtu(_mtu);
            ChainNetwork.SetAdapterDns(_dns);
            if (_routeAll)
            {
                ChainNetwork.AddDefaultRoute();
                ChainNetwork.AddIpv6Blackholes();
            }

            AppLog.I(Tag,
                $"chain adapter up: {name} {_address}/32 mtu={_mtu} dns={string.Join(',', _dns)}");
            return new WintunTunSession(this);
        }
    }

    // --- Data plane ----------------------------------------------------------

    private int ReadPacket(byte[] buffer, int offset, int count)
    {
        while (!_closed)
        {
            var session = _session;
            if (session == IntPtr.Zero) return 0;

            var packet = WintunReceivePacket(session, out var size);
            if (packet != IntPtr.Zero)
            {
                try
                {
                    var length = (int)Math.Min(size, (uint)count);
                    Marshal.Copy(packet, buffer, offset, length);
                    return length;
                }
                finally
                {
                    WintunReleaseReceivePacket(session, packet);
                }
            }

            // ERROR_NO_MORE_ITEMS is the normal "ring empty" answer; wait for
            // the session's event instead of spinning.
            var eventHandle = _readEvent;
            if (eventHandle != IntPtr.Zero)
            {
                var waited = WaitForSingleObject(eventHandle, 200);
                if (waited == WaitTimeout) continue;
            }
            else
            {
                Thread.Sleep(20);
            }
        }

        return 0;
    }

    private void WritePacket(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        var deadline = Environment.TickCount64 + 5_000;

        while (!_closed)
        {
            var session = _session;
            if (session == IntPtr.Zero) return;

            var packet = WintunAllocateSendPacket(session, (uint)count);
            if (packet != IntPtr.Zero)
            {
                try
                {
                    Marshal.Copy(buffer, offset, packet, count);
                }
                finally
                {
                    WintunSendPacket(session, packet);
                }

                return;
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorBufferOverflow && error != ErrorNoMoreItems)
            {
                AppLog.W(Tag, $"send dropped: WintunAllocateSendPacket error {error}");
                return;
            }

            if (Environment.TickCount64 > deadline)
            {
                AppLog.W(Tag, "send dropped: session ring stayed full for 5s");
                return;
            }

            Thread.Sleep(10);
        }
    }

    private void CloseSession()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;

            // Routes go first while the adapter still exists; afterwards the
            // names they referenced are gone.
            if (_routeAll)
            {
                ChainNetwork.RemoveDefaultRoute();
                ChainNetwork.RemoveIpv6Blackholes();
            }

            var session = _session;
            _session = IntPtr.Zero;
            _readEvent = IntPtr.Zero;
            if (session != IntPtr.Zero)
            {
                try { WintunEndSession(session); } catch { /* already gone */ }
            }

            var adapter = _adapter;
            _adapter = IntPtr.Zero;
            if (adapter != IntPtr.Zero)
            {
                try { WintunCloseAdapter(adapter); } catch { /* already gone */ }
            }

            ChainNetwork.FlushDns();
            AppLog.I(Tag, "chain adapter closed");
        }
    }

    /// <summary>One established session: the read/write pipe <c>IpTerminal</c> drives.</summary>
    private sealed class WintunTunSession : ISstpTunSession
    {
        private readonly WintunTun _owner;

        public WintunTunSession(WintunTun owner) => _owner = owner;

        public int Read(byte[] buffer, int offset, int count) =>
            _owner.ReadPacket(buffer, offset, count);

        public void Write(byte[] buffer, int offset, int count) =>
            _owner.WritePacket(buffer, offset, count);

        public void Close() => _owner.CloseSession();
    }

    // --- Wintun P/Invoke ------------------------------------------------------

    [DllImport("wintun.dll", CallingConvention = CallingConvention.Winapi,
        SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr WintunCreateAdapter(
        string name, string tunnelType, ref Guid requestedGuid);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.Winapi,
        SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr WintunOpenAdapter(string name);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern void WintunCloseAdapter(IntPtr adapter);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.Winapi,
        SetLastError = true)]
    private static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern void WintunEndSession(IntPtr session);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr WintunGetReadWaitEvent(IntPtr session);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.Winapi,
        SetLastError = true)]
    private static extern IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.Winapi,
        SetLastError = true)]
    private static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern void WintunSendPacket(IntPtr session, IntPtr packet);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}