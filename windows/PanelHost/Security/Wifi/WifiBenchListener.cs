using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using PanelHost.Transport;

namespace PanelHost.Security.Wifi;

// Construction and option parsing never allocate a socket. RunAsync is an explicit bench-only action.
internal static class WifiBenchListener
{
    public static async Task RunAsync(WifiBenchOptions options, TextWriter log, CancellationToken token)
    {
        var address = options.Validate();
        using var account = WindowsIdentity.GetCurrent();
        WifiTls.RequireHost(options.Enabled, OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
            new WindowsPrincipal(account).IsInRole(WindowsBuiltInRole.Administrator));
        var selected = WifiWindowsNetwork.Capture(address);
        var store = new WifiIdentityStore();
        if (!store.Exists) throw new ArgumentException(WifiBenchAdmin.NoIdentityMessage);
        // Never create a new identity or silently recover a stale guard when enabling LAN.
        using var registry = new WifiPeerRegistry(store);
        var identity = store.Load();
        using var root = WifiIdentityStore.OpenCertificate(identity.RootDer, identity.RootKey);
        using var server = WifiIdentityStore.OpenCertificate(identity.ServerDer, identity.ServerKey);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(TimeSpan.FromSeconds(options.WindowSeconds));
        var counters = new WifiBenchCounters();
        var admission = new WifiHandshakeAdmission(); // one limiter for the session port and the pairing port
        var usedPeers = new ConcurrentDictionary<string, byte>();
        var listener = new TcpListener(address, options.SessionPort) { ExclusiveAddressUse = true };
        NetworkAddressChangedEventHandler changed = (_, _) => window.Cancel();
        var monitor = Task.CompletedTask;
        var adminTask = Task.CompletedTask;
        WifiAdminBackend? admin = null;
        try
        {
            NetworkChange.NetworkAddressChanged += changed;
            listener.Start(4);
            monitor = MonitorBindingAsync(selected, window);
            log.WriteLine("[WiFi bench] Session listener enabled for the explicit time window; pairing disabled.");
            if (options.PairPort > 0)
            {
                // The Control Center opens, approves and revokes through a user-only pipe; the pairing listener exists
                // only while an invitation is open.
                admin = new WifiAdminBackend(registry, root, server, admission, counters, address, options.SessionPort, options.PairPort,
                    options.WindowSeconds, () => new TcpListener(address, options.PairPort) { ExclusiveAddressUse = true }, selected.Allows, window.Token);
                adminTask = new WifiAdminPipeServer(admin).RunAsync(window.Token);
                log.WriteLine("[WiFi bench] Pairing administration enabled for this window (Control Center); no invitation is open yet.");
            }
            await ServeLoopAsync(listener, selected.Allows, admission, server, root, registry, counters,
                key => usedPeers.TryAdd(key, 0), window.Token).ConfigureAwait(false);
        }
        finally
        {
            window.Cancel(); listener.Stop();
            NetworkChange.NetworkAddressChanged -= changed;
            await monitor.ConfigureAwait(false);
            try { await adminTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            admin?.Dispose();
            foreach (var key in usedPeers.Keys) registry.Revoke(key);
            log.WriteLine("[WiFi bench] Listener and sessions closed.");
            log.WriteLine(counters.Format());
        }
    }

    // The real accept loop. The source filter is a parameter only so the test suite can drive it on a loopback
    // listener; RunAsync always passes the validated RFC1918 same-subnet filter and never loopback.
    internal static async Task ServeLoopAsync(TcpListener listener, Func<IPAddress, bool> allowsSource,
        WifiHandshakeAdmission admission, X509Certificate2 server, X509Certificate2 root, WifiPeerRegistry registry,
        WifiBenchCounters counters, Action<string> activated, CancellationToken token)
    {
        var workers = new List<Task>(4);
        using var stop = token.Register(listener.Stop);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !allowsSource(remote.Address))
                { client.Dispose(); counters.Increment(WifiBenchEvent.OriginRefused); continue; }
                var lease = admission.TryEnter();
                if (lease is null) { client.Dispose(); counters.Increment(WifiBenchEvent.AdmissionRefused); continue; }
                workers.RemoveAll(worker => worker.IsCompleted);
                // Four workers total, including authenticated sessions: stronger than the handshake limit.
                workers.Add(ServeAsync(client, lease, server, root, registry, counters, activated, token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
    }

    private static async Task MonitorBindingAsync(WifiLanBinding selected, CancellationTokenSource window)
    {
        try
        {
            while (!window.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), window.Token).ConfigureAwait(false);
                if (WifiWindowsNetwork.Capture(selected.Address) != selected) { window.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (window.IsCancellationRequested) { }
        catch { window.Cancel(); } // Profile/API failure closes the window; never trust a stale snapshot.
    }

    private static async Task ServeAsync(TcpClient client, IDisposable admission, X509Certificate2 server,
        X509Certificate2 root, WifiPeerRegistry registry, WifiBenchCounters counters, Action<string> activated, CancellationToken token)
    {
        using (client)
        using (admission)
        using (token.Register(client.Dispose))
        {
            try { await AuthenticateAndServeAsync(client.GetStream(), server, root, registry, token, activated, counters).ConfigureAwait(false); }
            catch { /* No exception/payload logging from an untrusted connection. */ }
        }
    }

    internal static async Task AuthenticateAndServeAsync(Stream physical, X509Certificate2 server,
        X509Certificate2 root, WifiPeerRegistry registry, CancellationToken token, Action<string>? activated = null,
        WifiBenchCounters? counters = null)
    {
        var classified = false;
        AuthorizedWifiConnection authenticated;
        try
        {
            authenticated = await WifiTls.AuthenticateSessionAsync(physical, server, root, registry,
                locallyEnabled: true, token, reason => { classified = true; counters?.Increment(reason); }).ConfigureAwait(false);
        }
        catch when (!token.IsCancellationRequested)
        {
            // Wrong protocol/ALPN, aborted or stalled handshake (the 5 s deadline) and every callback-less failure.
            if (!classified) counters?.Increment(WifiBenchEvent.TlsOther);
            throw;
        }
        using (authenticated)
        {
            counters?.Increment(WifiBenchEvent.Authenticated);
            activated?.Invoke(authenticated.Peer.KeyId);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, authenticated.Peer.Revoked);
            using var close = lifetime.Token.Register(authenticated.Dispose);
            await using var session = new MultiplexedTransportSession(authenticated.Stream, limits: CarrierLimits.WifiV1, log: TextWriter.Null);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(5));
            try { await session.StartAsync(handshake.Token).ConfigureAwait(false); }
            catch when (!token.IsCancellationRequested)
            { counters?.Increment(WifiBenchEvent.ProtocolRefused); throw; }
            try { await WifiBenchSession.RunAsync(session, authenticated.Peer, authenticated.Dispose, lifetime.Token, counters: counters).ConfigureAwait(false); }
            finally { counters?.Increment(WifiBenchEvent.Completed); }
        }
    }
}
