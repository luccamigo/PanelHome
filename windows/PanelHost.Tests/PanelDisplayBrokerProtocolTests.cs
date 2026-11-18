using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.DisplayBroker.Protocol;
using PanelOS.DriverInterop;

namespace PanelHost.Tests;

[TestClass]
public sealed class PanelDisplayBrokerProtocolTests
{
    [TestMethod]
    public void V1_request_round_trips_only_typed_driver_operation()
    {
        var request = new PanelDisplayBrokerRequest(
            42,
            PanelDisplayBrokerProtocol.Version,
            PanelDisplayBrokerOperation.SetCaptureTarget,
            60);

        var json = PanelDisplayBrokerProtocol.Serialize(request);
        var restored = PanelDisplayBrokerProtocol.Deserialize<
            PanelDisplayBrokerRequest>(json);

        Assert.AreEqual(request, restored);
        StringAssert.Contains(json, "set_capture_target");
        Assert.IsFalse(json.Contains("command", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("shell", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("url", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Unknown_operation_is_rejected_by_protocol_parser()
    {
        const string json =
            "{\"requestId\":1,\"protocolVersion\":1,\"operation\":\"run_shell\"}";

        Assert.ThrowsException<System.Text.Json.JsonException>(() =>
            PanelDisplayBrokerProtocol.Deserialize<PanelDisplayBrokerRequest>(json));
    }

    [TestMethod]
    public void Status_and_metrics_response_round_trips_without_frame_payload()
    {
        var status = new PanelDisplayDriverStatus(
            PanelDisplayDriverContract.ProtocolVersion,
            PanelDisplayDriverState.Ready,
            PanelDisplayMonitorState.Connected,
            new PanelDisplayMode(1280, 800, 60),
            123,
            1,
            [new PanelDisplayMode(1280, 800, 60)]);
        var response = new PanelDisplayBrokerResponse(
            7,
            PanelDisplayBrokerProtocol.Version,
            true,
            Status: status);

        var json = PanelDisplayBrokerProtocol.Serialize(response);
        var restored = PanelDisplayBrokerProtocol.Deserialize<
            PanelDisplayBrokerResponse>(json);

        Assert.IsNotNull(restored?.Status);
        Assert.AreEqual(1280u, restored.Status.ActiveMode.Width);
        Assert.AreEqual(1, restored.Status.Modes.Count);
        Assert.IsFalse(json.Contains("pixels", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Authentication_rejects_wrong_nonce_and_consumes_launch_nonce()
    {
        var nonce = Convert.ToHexString(new byte[32]);
        var authenticator = new PanelDisplayBrokerAuthenticator(nonce);

        Assert.IsFalse(authenticator.TryAuthenticate(
            Convert.ToHexString(Enumerable.Repeat((byte)1, 32).ToArray())));
        Assert.IsFalse(authenticator.TryAuthenticate(nonce),
            "A failed attempt consumes the per-launch credential.");
    }

    [TestMethod]
    public void Authentication_rejects_stale_nonce_reuse()
    {
        var nonce = Convert.ToHexString(Enumerable.Repeat((byte)7, 32).ToArray());
        var authenticator = new PanelDisplayBrokerAuthenticator(nonce);

        Assert.IsTrue(authenticator.TryAuthenticate(nonce));
        Assert.IsFalse(authenticator.TryAuthenticate(nonce));
    }

    [TestMethod]
    public void Shared_mailbox_preserves_generation_and_latest_frame_wins()
    {
        var name = $"PanelOS.DisplayFrames.Tests.{Guid.NewGuid():N}";
        using var host = SharedFrameMailbox.CreateNew(name);
        using var broker = SharedFrameMailbox.OpenExisting(name);
        var first = Frame(generation: 10, sequence: 1, fill: 0x11);
        var second = Frame(generation: 11, sequence: 2, fill: 0x22);

        broker.Publish(first);
        var requestedFirst = host.GetFrameInfo(0);
        broker.Publish(second);

        var replaced = host.ReadFrame(requestedFirst);
        Assert.AreEqual(PanelDisplayFrameResult.FrameReplaced, replaced.Info.Result);
        Assert.AreEqual(0, replaced.Pixels.Length);

        var latestInfo = host.GetFrameInfo(0);
        var latest = host.ReadFrame(latestInfo);
        Assert.AreEqual(11ul, latest.Info.Generation);
        Assert.AreEqual(2ul, latest.Info.Sequence);
        Assert.IsTrue(latest.Pixels.All(value => value == 0x22));
        Assert.AreEqual(2ul, host.PublishedFrames);
        Assert.IsTrue(host.OverwrittenFrames >= 1);
        Assert.AreEqual(
            PanelDisplayFrameResult.NoNewFrame,
            host.GetFrameInfo(2).Result);
    }

    [TestMethod]
    public void Reset_removes_old_generation_without_queueing_stale_frame()
    {
        var name = $"PanelOS.DisplayFrames.Tests.{Guid.NewGuid():N}";
        using var host = SharedFrameMailbox.CreateNew(name);
        using var broker = SharedFrameMailbox.OpenExisting(name);
        broker.Publish(Frame(1, 9, 0x33));

        broker.Reset();

        Assert.AreEqual(
            PanelDisplayFrameResult.NoFrame,
            host.GetFrameInfo(0).Result);
    }

    [TestMethod]
    public async Task Host_disconnect_closes_the_broker_control_lease()
    {
        await using var closedHostEndpoint = new MemoryStream(Array.Empty<byte>());
        var brokerReader = new BrokerControlMessageReader(closedHostEndpoint);

        Assert.IsNull(await brokerReader.ReadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Concurrent_reset_and_publish_leave_a_complete_latest_frame()
    {
        var name = $"PanelOS.DisplayFrames.Tests.{Guid.NewGuid():N}";
        using var host = SharedFrameMailbox.CreateNew(name);
        using var broker = SharedFrameMailbox.OpenExisting(name);

        var publisher = Task.Run(() =>
        {
            for (ulong sequence = 1; sequence <= 100; sequence++)
            {
                broker.Publish(Frame(3, sequence, (byte)sequence));
            }
        });
        var resetter = Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
            {
                broker.Reset();
            }
        });
        await Task.WhenAll(publisher, resetter);
        broker.Publish(Frame(4, 101, 0x5A));

        var info = host.GetFrameInfo(0);
        var latest = host.ReadFrame(info);
        Assert.AreEqual(4ul, latest.Info.Generation);
        Assert.AreEqual(101ul, latest.Info.Sequence);
        Assert.IsTrue(latest.Pixels.All(value => value == 0x5A));
    }

    private static PanelDisplayDriverFrame Frame(
        ulong generation,
        ulong sequence,
        byte fill)
    {
        const uint width = 8;
        const uint height = 4;
        const uint stride = width * 4;
        var pixels = Enumerable.Repeat(fill, checked((int)(stride * height))).ToArray();
        return new PanelDisplayDriverFrame(
            new PanelDisplayFrameInfo(
                PanelDisplayFrameResult.Ok,
                generation,
                sequence,
                CaptureTimestampQpc: 1_000,
                QpcFrequency: 10_000_000,
                width,
                height,
                stride,
                PanelDisplayFrameFormat.Bgra8888,
                (uint)pixels.Length),
            pixels);
    }
}
