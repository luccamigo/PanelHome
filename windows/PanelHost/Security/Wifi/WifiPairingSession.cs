using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace PanelHost.Security.Wifi;

// Abandoned: the app closed the connection while the operator was still being asked. LimitReached: approved, but the PC
// already holds the maximum number of devices, so nothing was issued.
internal enum WifiPairingOutcome { Pending, Approved, Denied, Expired, Cancelled, TooManyInvalid, Abandoned, LimitReached }

// What the operator is asked to approve. Label is an untrusted display name; the fingerprint is the full
// SHA-256 of the device public key (64 hex), the only thing that identifies it.
internal sealed record WifiPairingRequestInfo(string Label, string Fingerprint, int ApproveSecondsLeft);

// One pairing invitation on one ephemeral listener (seguranca-wifi.md section 5): TLS 1.3 with ALPN panelos-pair/1,
// challenge, proof of possession, local approval, certificate. No PLMX, no application protocol and nothing but
// pairing is ever served here. Approval is a local method call (Approve); the protocol has no message for it.
internal sealed class WifiPairingSession : IDisposable
{
    public const int TotalSeconds = WifiPairingInvitation.LifetimeSeconds;
    public const int ApprovalSeconds = 60;
    public const int PairPanelCapabilities = WifiBenchSession.PanelReadCapability;

    private readonly WifiPairingOffer _offer;
    private readonly WifiPairingInvitation _invitation;
    private readonly bool _pinMode;
    private readonly X509Certificate2 _root, _server;
    private readonly WifiPeerRegistry _registry;
    private readonly WifiHandshakeAdmission _admission;
    private readonly WifiBenchCounters? _counters;
    private readonly Func<IPAddress, bool> _allowsSource;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private string? _pendingLabel, _pendingFingerprint;
    private long _pendingSince;
    private int _outcome;

    public WifiPairingSession(WifiPairingOffer offer, string? pin, X509Certificate2 root, X509Certificate2 server,
        WifiPeerRegistry registry, WifiHandshakeAdmission admission, Func<IPAddress, bool> allowsSource,
        WifiBenchCounters? counters = null, TimeProvider? clock = null)
    {
        _offer = offer; _pinMode = pin is not null; _root = root; _server = server; _registry = registry;
        _admission = admission; _allowsSource = allowsSource; _counters = counters; _clock = clock ?? TimeProvider.System;
        _invitation = new WifiPairingInvitation(offer, _clock);
        _started = _clock.GetTimestamp();
        // The QR text and the typed code exist only in this object's memory; neither is logged or persisted.
        if (_pinMode) TypedCode = WifiTypedCode.Encode(offer, pin!);
        else QrText = System.Text.Encoding.UTF8.GetString(offer.Encode());
    }

    public string? QrText { get; }
    public string? TypedCode { get; }
    public bool PinMode => _pinMode;
    public WifiPairingOutcome Outcome => (WifiPairingOutcome)Volatile.Read(ref _outcome);
    public int ExpiresInSeconds => Math.Max(0, TotalSeconds - (int)_clock.GetElapsedTime(_started).TotalSeconds);

    public WifiPairingRequestInfo? Pending
    {
        get
        {
            lock (_gate)
            {
                if (_pendingFingerprint is null || Outcome != WifiPairingOutcome.Pending) return null;
                var left = ApprovalSeconds - (int)_clock.GetElapsedTime(_pendingSince).TotalSeconds;
                return new(_pendingLabel!, _pendingFingerprint, Math.Max(0, left));
            }
        }
    }

    // The fingerprint must match the request currently shown: an approval of a different (swapped) request fails.
    public bool Approve(string fingerprint) => Decide(fingerprint, approve: true);
    public bool Deny(string fingerprint) => Decide(fingerprint, approve: false);

    private bool Decide(string fingerprint, bool approve)
    {
        lock (_gate)
        {
            if (_pendingFingerprint is null || Outcome != WifiPairingOutcome.Pending ||
                !string.Equals(_pendingFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) return false;
            return _decision.TrySetResult(approve);
        }
    }

    public void Cancel() => Finish(WifiPairingOutcome.Cancelled);

    // The first outcome wins. Ending the invitation always wakes the accept loop and any waiting approval, so the
    // listener closes promptly instead of waiting for the next connection or the expiry.
    private void Finish(WifiPairingOutcome outcome)
    {
        Interlocked.CompareExchange(ref _outcome, (int)outcome, (int)WifiPairingOutcome.Pending);
        _decision.TrySetResult(false);
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
    }

    // Five invalid attempts close the invitation inside WifiPairingInvitation; mirror that into the outcome.
    private void CheckInvitationClosed()
    {
        if (Outcome == WifiPairingOutcome.Pending && _invitation.IsClosed)
            Finish(ExpiresInSeconds == 0 ? WifiPairingOutcome.Expired : WifiPairingOutcome.TooManyInvalid);
    }

    // Runs until the invitation ends (approved, denied, expired, cancelled or five invalid attempts) and then
    // stops the listener. The caller owns the listener's creation (bind address and port) and starts it.
    public async Task RunAsync(TcpListener listener, CancellationToken token)
    {
        using var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(TotalSeconds), _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token, expiry.Token);
        var workers = new List<Task>(4);
        using var stop = linked.Token.Register(listener.Stop);
        try
        {
            while (!linked.IsCancellationRequested && Outcome == WifiPairingOutcome.Pending)
            {
                var client = await listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !_allowsSource(remote.Address))
                { client.Dispose(); _counters?.Increment(WifiBenchEvent.OriginRefused); continue; }
                var lease = _admission.TryEnter();
                if (lease is null) { client.Dispose(); _counters?.Increment(WifiBenchEvent.AdmissionRefused); continue; }
                workers.RemoveAll(worker => worker.IsCompleted);
                workers.Add(ServeAsync(client, lease, linked));
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (SocketException) when (linked.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (linked.IsCancellationRequested) { }
        finally
        {
            if (Outcome == WifiPairingOutcome.Pending)
                Finish(expiry.IsCancellationRequested ? WifiPairingOutcome.Expired : WifiPairingOutcome.Cancelled);
            _decision.TrySetResult(false);
            linked.Cancel(); listener.Stop();
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(TcpClient client, IDisposable lease, CancellationTokenSource lifetime)
    {
        using (client)
        using (lease)
        using (lifetime.Token.Register(client.Dispose))
        {
            try { await HandleAsync(client.GetStream(), lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception) { _counters?.Increment(WifiBenchEvent.PairingFailed); /* never log the peer's data */ }
            finally { CheckInvitationClosed(); }
        }
    }

    private static byte[] Json(object message) => JsonSerializer.SerializeToUtf8Bytes(message);

    internal async Task HandleAsync(Stream physical, CancellationToken token)
    {
        await using var ssl = new SslStream(physical, leaveInnerStreamOpen: false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await ssl.AuthenticateAsServerAsync(WifiTls.ServerOptions(_server, _root, null, pairing: true), deadline.Token).ConfigureAwait(false);
            WifiTls.CheckNegotiated(ssl, pairing: true);
        }
        catch when (!token.IsCancellationRequested) { _counters?.Increment(WifiBenchEvent.TlsOther); throw; }

        var wire = new WifiPairingWire(ssl);
        using var attempt = _invitation.BeginAttempt();
        // PIN mode has no QR to carry the root, so the host sends it first; the app checks it against the typed host
        // id and validates the TLS server certificate against it BEFORE any secret leaves the device.
        if (_pinMode) await wire.WriteAsync(Json(new { type = "host_root", rootCa = WifiPairingOffer.B64(_root.RawData) }), token).ConfigureAwait(false);
        await wire.WriteAsync(Json(new { type = "challenge", challenge = WifiPairingOffer.B64(attempt.Challenge) }), token).ConfigureAwait(false);

        byte[] pairId, requestToken, spki, signature; string label;
        using (var request = await wire.ReadAsync(token).ConfigureAwait(false))
        {
            var fields = request.RootElement.EnumerateObject().ToArray();
            string[] names = ["type", "pairId", "token", "spki", "label", "signature"];
            if (fields.Length != names.Length || fields.Any(field => !names.Contains(field.Name)) ||
                request.RootElement.GetProperty("type").GetString() != "pair_request") throw new InvalidDataException("pairing_failed");
            pairId = Field(request, "pairId", 22); requestToken = Field(request, "token", 43);
            spki = Field(request, "spki", 683, 1, 512); signature = Field(request, "signature", 107, 8, 72);
            label = request.RootElement.GetProperty("label").GetString() ?? throw new InvalidDataException("pairing_failed");
        }
        if (!attempt.Reserve(pairId, requestToken, spki, signature, label))
        {
            await RefuseAsync(wire, token).ConfigureAwait(false);
            _counters?.Increment(WifiBenchEvent.PairingFailed);
            CheckInvitationClosed();
            return;
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(spki));
        lock (_gate) { _pendingLabel = _invitation.CandidateLabel!; _pendingFingerprint = fingerprint; _pendingSince = _clock.GetTimestamp(); }

        // After pair_request the app sends nothing until the result: a read that completes (EOF, reset or stray bytes)
        // means it gave up, and the operator must not be asked to approve a request nobody is waiting for. The pending
        // read is never cancelled (that could corrupt the stream the result is written on); closing the stream ends it.
        var appGaveUp = PeerClosedAsync(ssl);
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        var decision = _decision.Task.WaitAsync(TimeSpan.FromSeconds(ApprovalSeconds), _clock, stopWaiting.Token);
        bool approved; var timedOut = false; var abandoned = false;
        try
        {
            var first = await Task.WhenAny(decision, appGaveUp).ConfigureAwait(false);
            if (first == appGaveUp && !_decision.Task.IsCompleted) { approved = false; abandoned = true; stopWaiting.Cancel(); }
            else approved = await decision.ConfigureAwait(false);
        }
        catch (TimeoutException) { approved = false; timedOut = true; }
        X509Certificate2? leaf = null;
        var limitReached = false;
        if (approved)
        {
            // Issued only now, after the local approval: a failure here (60 s elapsed, pair limit, disk) issues nothing.
            try { leaf = _invitation.ApproveLocally(_root, _registry, PairPanelCapabilities); }
            catch (WifiPairLimitReachedException) { limitReached = true; }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or CryptographicException) { }
        }
        if (leaf is null)
        {
            _counters?.Increment(approved ? WifiBenchEvent.PairingFailed : WifiBenchEvent.PairingDenied);
            if (!abandoned) await RefuseAsync(wire, token).ConfigureAwait(false);
            Finish(abandoned ? WifiPairingOutcome.Abandoned : timedOut ? WifiPairingOutcome.Expired : limitReached ? WifiPairingOutcome.LimitReached :
                approved ? WifiPairingOutcome.Denied : token.IsCancellationRequested ? WifiPairingOutcome.Cancelled : WifiPairingOutcome.Denied);
            return;
        }
        using (leaf)
        {
            await wire.WriteAsync(Json(new { type = "pair_result", status = "approved",
                certificate = WifiPairingOffer.B64(leaf.RawData), rootCa = WifiPairingOffer.B64(_root.RawData) }), token).ConfigureAwait(false);
            try { await ssl.ShutdownAsync().ConfigureAwait(false); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        }
        // One credential per invitation: the listener closes as soon as the result has been sent.
        _counters?.Increment(WifiBenchEvent.PairingApproved);
        Finish(WifiPairingOutcome.Approved);
    }

    private static async Task<bool> PeerClosedAsync(Stream stream)
    {
        try { await stream.ReadAsync(new byte[1]).ConfigureAwait(false); return true; }
        catch (Exception) { return true; }
    }

    private static async Task RefuseAsync(WifiPairingWire wire, CancellationToken token)
    {
        try { await wire.WriteAsync(Json(new { type = "pair_result", status = "pairing_failed" }), token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static byte[] Field(JsonDocument document, string name, int maxText, int minBytes = 0, int maxBytes = int.MaxValue)
    {
        var text = document.RootElement.GetProperty(name).GetString() ?? throw new InvalidDataException("pairing_failed");
        if (text.Length == 0 || text.Length > maxText || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidDataException("pairing_failed");
        var bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
        if (WifiPairingOffer.B64(bytes) != text || bytes.Length < minBytes || bytes.Length > maxBytes) throw new InvalidDataException("pairing_failed");
        return bytes;
    }

    public void Dispose()
    {
        Finish(WifiPairingOutcome.Cancelled);
        _stop.Dispose();
        _invitation.Dispose();
        _offer.Dispose();
    }
}
