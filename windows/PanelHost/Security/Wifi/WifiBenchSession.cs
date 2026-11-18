using System.Text;
using System.Text.Json;
using PanelHost.Protocol;
using PanelHost.Server;
using PanelHost.Transport;

namespace PanelHost.Security.Wifi;

// Read-only bench dispatcher. No desktop, touch, shell, media or Stream Deck service is constructed.
internal static class WifiBenchSession
{
    public const int PanelReadCapability = 1;

    public static async Task RunAsync(MultiplexedTransportSession session, AuthorizedWifiPeer peer, Action disconnect, CancellationToken token,
        TimeProvider? clock = null, WifiBenchCounters? counters = null)
    {
        // Each connection is counted at most once, by the first reason that ended it.
        var counted = false;
        void Count(WifiBenchEvent kind) { if (!counted) { counted = true; counters?.Increment(kind); } }
        if ((peer.Capabilities & PanelReadCapability) == 0) { Count(WifiBenchEvent.ProtocolRefused); throw new InvalidDataException("WiFi panel capability required."); }
        using var heartbeat = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock ?? TimeProvider.System);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, peer.Revoked, heartbeat.Token);
        using var close = lifetime.Token.Register(disconnect);
        var closeVideo = RejectVideoAsync(session, lifetime.Token);
        try
        {
            await using var control = await session.AcceptChannelAsync(PanelChannelId.Control, lifetime.Token);
            await using var connection = new JsonLineConnection(control);
            var helloLine = await connection.ReadLineAsync(lifetime.Token);
            if (helloLine is null) return;
            var hello = PanelJson.Deserialize<HelloMessage>(helloLine);
            if (!HandshakeValidator.Validate(hello).IsAccepted) { Count(WifiBenchEvent.ProtocolRefused); throw new InvalidDataException("Invalid WiFi hello."); }
            heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
            await connection.WriteAsync(new HelloAckMessage { HostName = "PanelOS WiFi bench", Features = [] }, lifetime.Token);
            // Explicitly synthetic: no kernel driver, capture, device name or host state is exposed.
            await connection.WriteAsync(new TelemetryMessage { Cpu = new CpuTelemetry { Name = "Synthetic bench; sensors unavailable" } }, lifetime.Token);
            var rate = new ControlRate();
            while (await connection.ReadLineAsync(lifetime.Token) is { } line)
            {
                peer.Revoked.ThrowIfCancellationRequested();
                try { rate.Record(Encoding.UTF8.GetByteCount(line)); }
                catch (InvalidDataException) { Count(WifiBenchEvent.ControlRate); throw; }
                var message = PanelJson.Deserialize<MessageEnvelope>(line);
                // Unknown input and every command close before any side effect or heartbeat refresh.
                if (message.Type is not MessageTypes.Ping and not MessageTypes.Pong)
                { Count(WifiBenchEvent.CommandRefused); throw new InvalidDataException("WiFi bench operation refused."); }
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.EnumerateObject().Count() != 1) { Count(WifiBenchEvent.ProtocolRefused); throw new InvalidDataException("Invalid WiFi heartbeat."); }
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                if (message.Type == MessageTypes.Ping) await connection.WriteAsync(new PongMessage(), lifetime.Token);
            }
            // A closed stream can end the read loop as a clean end-of-file, so count the cause by state here too.
            if (peer.Revoked.IsCancellationRequested) Count(WifiBenchEvent.SessionRevoked);
            else if (heartbeat.IsCancellationRequested && !token.IsCancellationRequested) Count(WifiBenchEvent.HeartbeatTimeout);
        }
        catch (Exception)
        {
            // Classify by state, not by exception type: closing a stream under a blocked read surfaces as an
            // IOException or ObjectDisposedException as often as an OperationCanceledException. The caller's
            // token is also cancelled by a revocation, so revocation is tested first and the end of the window
            // (caller token cancelled for any other reason) is not counted as a refusal.
            if (peer.Revoked.IsCancellationRequested) Count(WifiBenchEvent.SessionRevoked);
            else if (!token.IsCancellationRequested)
                Count(heartbeat.IsCancellationRequested ? WifiBenchEvent.HeartbeatTimeout : WifiBenchEvent.ProtocolRefused);
            throw;
        }
        finally
        {
            lifetime.Cancel();
            try { await closeVideo; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private static async Task RejectVideoAsync(MultiplexedTransportSession session, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var video = await session.AcceptChannelAsync(PanelChannelId.Video, token);
            // Video requires capture/capability integration; FIN leaves Control alive in this bench.
        }
    }

    internal sealed class ControlRate(TimeProvider? clock = null)
    {
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;
        private long? _previous;
        private double _messages = 480, _bytes = 2 * 1024 * 1024;
        public void Record(int bytes)
        {
            var now = _clock.GetTimestamp();
            var seconds = _previous is { } previous ? _clock.GetElapsedTime(previous, now).TotalSeconds : 0;
            _previous = now;
            _messages = Math.Min(480, _messages + 240 * seconds);
            _bytes = Math.Min(2 * 1024 * 1024, _bytes + 2 * 1024 * 1024 * seconds);
            if (_messages < 1 || bytes < 0 || _bytes < bytes) throw new InvalidDataException("WiFi Control rate exceeded.");
            _messages--; _bytes -= bytes;
        }
    }
}
