using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using PanelOS.Windows.Wifi;

namespace PanelHost.Security.Wifi;

// The Control Center's operations on the WiFi identity: open one pairing invitation, see the request waiting for
// approval, approve / deny it, list devices and revoke one. It runs in the process that owns the WifiPeerRegistry (the
// bench window), so the Issued -> Active hand-off between pairing and the session listener stays in one process.
internal sealed class WifiAdminBackend : IDisposable
{
    private readonly WifiPeerRegistry _registry;
    private readonly X509Certificate2 _root, _server;
    private readonly WifiHandshakeAdmission _admission;
    private readonly WifiBenchCounters _counters;
    private readonly IPAddress _advertised;
    private readonly int _sessionPort, _pairPort, _windowSeconds;
    private readonly Func<TcpListener> _pairListener;
    private readonly Func<IPAddress, bool> _allowsSource;
    private readonly CancellationToken _window;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly object _gate = new();
    private WifiPairingSession? _session;
    private Task _run = Task.CompletedTask;

    public WifiAdminBackend(WifiPeerRegistry registry, X509Certificate2 root, X509Certificate2 server, WifiHandshakeAdmission admission,
        WifiBenchCounters counters, IPAddress advertised, int sessionPort, int pairPort, int windowSeconds,
        Func<TcpListener> pairListener, Func<IPAddress, bool> allowsSource, CancellationToken window, TimeProvider? clock = null)
    {
        _registry = registry; _root = root; _server = server; _admission = admission; _counters = counters;
        _advertised = advertised; _sessionPort = sessionPort; _pairPort = pairPort; _windowSeconds = windowSeconds;
        _pairListener = pairListener; _allowsSource = allowsSource; _window = window; _clock = clock ?? TimeProvider.System;
        _started = _clock.GetTimestamp();
    }

    public WifiAdminResponse Handle(WifiAdminRequest request)
    {
        if (request.ProtocolVersion != WifiAdminProtocol.Version) return Fail(WifiAdminProtocol.ErrorInvalid);
        try
        {
            return request.Type switch
            {
                WifiAdminProtocol.Status => Ok(),
                WifiAdminProtocol.OpenPairing => OpenPairing(request.Mode),
                WifiAdminProtocol.CancelPairing => Cancel(),
                WifiAdminProtocol.Approve => Decide(request.Fingerprint, approve: true),
                WifiAdminProtocol.Deny => Decide(request.Fingerprint, approve: false),
                WifiAdminProtocol.Revoke => Revoke(request.Fingerprint),
                _ => Fail(WifiAdminProtocol.ErrorUnsupported),
            };
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or SocketException
            or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        { return Fail(WifiAdminProtocol.ErrorFailed); }
    }

    private static bool ValidFingerprint(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private WifiAdminResponse Ok() => new(true, null, StatusSnapshot());
    private WifiAdminResponse Fail(string code) => new(false, code, StatusSnapshot());

    private WifiAdminResponse OpenPairing(string? mode)
    {
        if (mode is not (WifiAdminProtocol.ModeQr or WifiAdminProtocol.ModeCode)) return Fail(WifiAdminProtocol.ErrorInvalid);
        if (_window.IsCancellationRequested) return Fail(WifiAdminProtocol.ErrorWindowClosed);
        lock (_gate)
        {
            if (_session is { Outcome: WifiPairingOutcome.Pending }) return Fail(WifiAdminProtocol.ErrorPairingOpen);
            var listener = _pairListener();
            try { listener.Start(4); }
            catch { listener.Stop(); throw; }
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string? pin = null;
            WifiPairingOffer offer;
            try
            {
                offer = mode == WifiAdminProtocol.ModeCode
                    ? WifiPairingOffer.CreateWithPin(_root, _advertised.ToString(), port, _sessionPort, out pin)
                    : WifiPairingOffer.Create(_root, _advertised.ToString(), port, _sessionPort);
            }
            catch { listener.Stop(); throw; }
            _session?.Dispose();
            _session = new WifiPairingSession(offer, pin, _root, _server, _registry, _admission, _allowsSource, _counters, _clock);
            var session = _session;
            _run = Task.Run(async () =>
            {
                try { await session.RunAsync(listener, _window).ConfigureAwait(false); }
                catch (Exception) { session.Cancel(); }
            });
            return Ok();
        }
    }

    private WifiAdminResponse Cancel()
    {
        lock (_gate) _session?.Cancel();
        return Ok();
    }

    private WifiAdminResponse Decide(string? fingerprint, bool approve)
    {
        if (!ValidFingerprint(fingerprint)) return Fail(WifiAdminProtocol.ErrorInvalid);
        WifiPairingSession? session;
        lock (_gate) session = _session;
        var applied = session is not null && (approve ? session.Approve(fingerprint!) : session.Deny(fingerprint!));
        return applied ? Ok() : Fail(WifiAdminProtocol.ErrorNoRequest);
    }

    private WifiAdminResponse Revoke(string? fingerprint)
    {
        if (!ValidFingerprint(fingerprint)) return Fail(WifiAdminProtocol.ErrorInvalid);
        var keyId = fingerprint!.ToUpperInvariant();
        if (!_registry.Peers().Any(peer => peer.KeyId == keyId)) return Fail(WifiAdminProtocol.ErrorUnknownDevice);
        _registry.Revoke(keyId);
        return Ok();
    }

    private WifiAdminStatus StatusSnapshot()
    {
        WifiPairingSession? session;
        lock (_gate) session = _session;
        var elapsed = (int)_clock.GetElapsedTime(_started).TotalSeconds;
        var left = _window.IsCancellationRequested ? 0 : Math.Max(0, _windowSeconds - elapsed);
        WifiAdminPairing? pairing = null;
        if (session is not null)
        {
            var open = session.Outcome == WifiPairingOutcome.Pending;
            var pending = session.Pending;
            pairing = new(session.PinMode ? WifiAdminProtocol.ModeCode : WifiAdminProtocol.ModeQr, session.Outcome.ToString(),
                open ? session.ExpiresInSeconds : 0, open ? session.QrText : null, open ? session.TypedCode : null,
                pending is null ? null : new(pending.Label, pending.Fingerprint, pending.ApproveSecondsLeft));
        }
        var devices = _registry.Peers().OrderByDescending(peer => peer.CreatedAt ?? DateTimeOffset.MinValue)
            .Select(peer => new WifiAdminDevice(peer.KeyId, peer.Label, peer.State, peer.CreatedAt, _registry.IsConnected(peer.KeyId))).ToArray();
        return new WifiAdminStatus(left > 0, left, _advertised.ToString(), _sessionPort, _pairPort, pairing, devices,
            Convert.ToHexString(WifiCertificates.HostId(_root)));
    }

    public void Dispose()
    {
        lock (_gate) { _session?.Cancel(); }
        try { _run.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        lock (_gate) { _session?.Dispose(); _session = null; }
    }
}