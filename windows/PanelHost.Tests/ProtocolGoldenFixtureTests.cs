using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Protocol;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class ProtocolGoldenFixtureTests
{
    [TestMethod]
    public void Every_shared_control_fixture_round_trips_canonically()
    {
        foreach (var path in ValidControlFixtures())
        {
            var source = File.ReadAllText(path);
            var envelope = PanelJson.Deserialize<MessageEnvelope>(source);
            var serialized = envelope.Type switch
            {
                MessageTypes.Hello => PanelJson.Serialize(PanelJson.Deserialize<HelloMessage>(source)),
                MessageTypes.HelloAck => PanelJson.Serialize(PanelJson.Deserialize<HelloAckMessage>(source)),
                MessageTypes.Error => PanelJson.Serialize(PanelJson.Deserialize<ErrorMessage>(source)),
                MessageTypes.Telemetry => PanelJson.Serialize(PanelJson.Deserialize<TelemetryMessage>(source)),
                MessageTypes.MediaState => PanelJson.Serialize(PanelJson.Deserialize<MediaStateMessage>(source)),
                MessageTypes.VolumeState => PanelJson.Serialize(PanelJson.Deserialize<VolumeStateMessage>(source)),
                MessageTypes.DesktopState => PanelJson.Serialize(PanelJson.Deserialize<DesktopStateMessage>(source)),
                MessageTypes.Command => PanelJson.Serialize(PanelJson.Deserialize<CommandMessage>(source)),
                MessageTypes.StreamDeckLayout => PanelJson.Serialize(PanelJson.Deserialize<StreamDeckLayoutMessage>(source)),
                MessageTypes.StreamDeckInvokeResult => PanelJson.Serialize(PanelJson.Deserialize<StreamDeckInvokeResultMessage>(source)),
                MessageTypes.StreamDeckIcon => PanelJson.Serialize(PanelJson.Deserialize<StreamDeckIconMessage>(source)),
                MessageTypes.LoopMediaState => PanelJson.Serialize(PanelJson.Deserialize<LoopMediaStateMessage>(source)),
                MessageTypes.LoopMediaChunk => PanelJson.Serialize(PanelJson.Deserialize<LoopMediaChunkMessage>(source)),
                _ => throw new AssertFailedException($"Unhandled fixture type {envelope.Type}"),
            };

            Assert.IsTrue(
                JsonNode.DeepEquals(JsonNode.Parse(source), JsonNode.Parse(serialized)),
                Path.GetFileName(path));
        }
    }

    [TestMethod]
    public void Shared_video_vectors_match_the_CSharp_wire_codec()
    {
        AssertVector("hello.json", PanelVideoProtocol.EncodeHello(
            new VideoHello(1280, 800, 60, (VideoCodecMask)3)));
        AssertVector("ack.json", PanelVideoProtocol.EncodeAck(
            new VideoAck(VideoCodec.H264, 1280, 800, 60)));
        AssertVector("reject.json", PanelVideoProtocol.EncodeReject(VideoRejectCode.Busy));
        AssertVector("h264_packet_header.json", PanelVideoProtocol.EncodeH264PacketHeader(
            new H264VideoPacket(
                1,
                1000,
                H264PacketFlags.KeyFrame | H264PacketFlags.EndOfAccessUnit,
                new byte[5],
                2500)));
    }

    [TestMethod]
    public void Every_shared_invalid_fixture_is_rejected()
    {
        foreach (var path in Directory.GetFiles(
                     Path.Combine(FixtureRoot(), "errors", "invalid"),
                     "*.json"))
        {
            var rejected = false;
            try
            {
                DeserializeAndValidateInvalid(File.ReadAllText(path));
            }
            catch (Exception)
            {
                rejected = true;
            }
            Assert.IsTrue(rejected, Path.GetFileName(path));
        }
    }

    private static void DeserializeAndValidateInvalid(string json)
    {
        var envelope = PanelJson.Deserialize<MessageEnvelope>(json);
        switch (envelope.Type)
        {
            case MessageTypes.Hello:
                var hello = PanelJson.Deserialize<HelloMessage>(json);
                if (!HandshakeValidator.Validate(hello).IsAccepted)
                {
                    throw new JsonException("Invalid hello.");
                }
                break;
            case MessageTypes.Telemetry:
                _ = PanelJson.Deserialize<TelemetryMessage>(json);
                break;
            case MessageTypes.DesktopState:
                var desktop = PanelJson.Deserialize<DesktopStateMessage>(json);
                if (desktop.State is not (DesktopStates.Stopped or DesktopStates.Starting or
                    DesktopStates.Running or DesktopStates.Stopping or DesktopStates.Error))
                {
                    throw new JsonException("Invalid desktop state.");
                }
                break;
            case MessageTypes.StreamDeckLayout:
                var layout = PanelJson.Deserialize<StreamDeckLayoutMessage>(json);
                if (layout.Pages.Any(page => page.Rows is < 2 or > StreamDeckProtocolLimits.MaximumRows ||
                    page.Columns is < 2 or > StreamDeckProtocolLimits.MaximumColumns))
                {
                    throw new JsonException("Invalid Stream Deck dimensions.");
                }
                break;
            default:
                throw new JsonException("Invalid fixture unexpectedly had another type.");
        }
    }

    private static IEnumerable<string> ValidControlFixtures() =>
        new[] { "control", "telemetry", "streamdeck", "loop_media", "errors" }
            .SelectMany(directory => Directory.GetFiles(
                Path.Combine(FixtureRoot(), directory),
                "*.json",
                SearchOption.TopDirectoryOnly));

    private static void AssertVector(string fileName, byte[] actual)
    {
        var json = JsonNode.Parse(File.ReadAllText(
            Path.Combine(FixtureRoot(), "video", fileName)))!;
        var expected = Convert.FromHexString(json["hex"]!.GetValue<string>());
        CollectionAssert.AreEqual(expected, actual, fileName);
    }

    private static string FixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "protocol-fixtures");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("protocol-fixtures was not found.");
    }
}
