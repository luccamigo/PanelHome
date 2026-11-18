using System.Net;
using System.Net.Sockets;

namespace PanelHost.Security.Wifi;

internal sealed record WifiBenchOptions(bool Enabled = false, string BindAddress = "",
    int SessionPort = 0, int WindowSeconds = 0, bool CableDisconnected = false, int PairPort = 0)
{
    public IPAddress Validate()
    {
        if (!Enabled || !CableDisconnected || WindowSeconds is < 1 or > 900 ||
            SessionPort is < 1024 or > 65535 or 8765 or 8766 or 8767 ||
            PairPort != 0 && (PairPort is < 1024 or > 65535 or 8765 or 8766 or 8767 || PairPort == SessionPort) ||
            !IPAddress.TryParse(BindAddress, out var address) || address.ToString() != BindAddress || !IsPrivate(address))
            throw new ArgumentException("WiFi bench requires explicit opt-in, disconnected cable, RFC1918 IPv4, port and a 1..900 second window.");
        return address;
    }

    internal static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
    }
}

// One shared instance for every bench port. No waiting queue or task before admission.
internal sealed class WifiHandshakeAdmission(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Queue<long> _attempts = new();
    private int _active;
    private double _tokens = 4;
    private long? _refilled;

    public IDisposable? TryEnter()
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            _tokens = Math.Min(4, _tokens + (_refilled is { } previous ? _clock.GetElapsedTime(previous, now).TotalSeconds / 5 : 0));
            _refilled = now;
            while (_attempts.TryPeek(out var first) && _clock.GetElapsedTime(first, now) >= TimeSpan.FromMinutes(1)) _attempts.Dequeue();
            if (_active >= 4 || _tokens < 1 || _attempts.Count >= 12) return null;
            _tokens--; _attempts.Enqueue(now); _active++;
            return new Lease(this);
        }
    }

    private sealed class Lease(WifiHandshakeAdmission owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                lock (owner._gate) owner._active--;
        }
    }
}

internal sealed record WifiLanBinding(string InterfaceId, IPAddress Address, int PrefixLength, bool Physical, bool Private, bool Up)
{
    public void Validate()
    {
        if (!Physical || !Private || !Up || !WifiBenchOptions.IsPrivate(Address) || PrefixLength is < 8 or > 30)
            throw new InvalidOperationException("WiFi bench requires an active physical Private IPv4 interface.");
    }

    public bool Allows(IPAddress source)
    {
        Validate();
        if (!WifiBenchOptions.IsPrivate(source) || source.Equals(Address)) return false;
        var local = Address.GetAddressBytes(); var remote = source.GetAddressBytes();
        uint host = 0;
        for (var bit = 0; bit < 32; bit++)
        {
            var mask = 1 << (7 - bit % 8);
            if (bit < PrefixLength && (local[bit / 8] & mask) != (remote[bit / 8] & mask)) return false;
            if (bit >= PrefixLength) host = (host << 1) | ((remote[bit / 8] & mask) != 0 ? 1u : 0u);
        }
        return host != 0 && host != (1u << (32 - PrefixLength)) - 1;
    }
}
