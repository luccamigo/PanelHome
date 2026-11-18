using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Protocol;
using PanelHost.Services;

namespace PanelHost.Tests;

[TestClass]
public sealed class ProtocolSerializationTests
{
    [TestMethod]
    public void Hello_serialization_is_single_lf_terminated_json_line()
    {
        var message = new HelloMessage
        {
            ProtocolVersion = PanelProtocol.PROTOCOL_VERSION,
            DeviceName = "PanelOS",
        };

        var line = PanelJson.SerializeLine(message);

        Assert.IsTrue(line.EndsWith('\n'));
        Assert.IsFalse(line.Contains('\r'));
        Assert.AreEqual(1, line.Count(character => character == '\n'));
        StringAssert.Contains(line, "\"type\":\"hello\"");
        StringAssert.Contains(line, "\"protocolVersion\":1");
        StringAssert.Contains(line, "\"deviceName\":\"PanelOS\"");
    }

    [TestMethod]
    public void Hello_features_round_trip_without_changing_protocol_version()
    {
        var original = new HelloMessage
        {
            ProtocolVersion = PanelProtocol.PROTOCOL_VERSION,
            DeviceName = "PanelOS",
            Features = [ProtocolFeatures.MediaArtwork],
        };

        var restored = PanelJson.Deserialize<HelloMessage>(PanelJson.Serialize(original));

        Assert.AreEqual(1, restored.ProtocolVersion);
        Assert.IsTrue(restored.SupportsFeature(ProtocolFeatures.MediaArtwork));
    }

    [TestMethod]
    public void Hello_ack_advertises_desktop_feature_bilaterally()
    {
        var original = new HelloAckMessage
        {
            HostName = "PanelHost Test",
            Features = [ProtocolFeatures.Desktop],
        };

        var json = PanelJson.Serialize(original);
        var restored = PanelJson.Deserialize<HelloAckMessage>(json);

        Assert.AreEqual(PanelProtocol.PROTOCOL_VERSION, restored.ProtocolVersion);
        Assert.IsTrue(restored.SupportsFeature(ProtocolFeatures.Desktop));
        StringAssert.Contains(json, "\"features\":[\"desktop\"]");
    }

    [TestMethod]
    public void Telemetry_round_trips_without_losing_values()
    {
        var original = new TelemetryMessage
        {
            Cpu = new CpuTelemetry
            {
                Name = "AMD Ryzen 7",
                TemperatureC = 52.0,
                UsagePercent = 18.0,
                ClockGhz = 4.3,
                PowerW = 65.2,
            },
            Gpu = new GpuTelemetry
            {
                Name = "NVIDIA GeForce RTX",
                TemperatureC = 47.0,
                UsagePercent = 31.0,
                ClockGhz = 2.6,
                MemoryUsagePercent = 50.0,
                MemoryUsedGb = 4.0,
                MemoryTotalGb = 8.0,
                PowerW = 121.4,
            },
            Memory = new MemoryTelemetry
            {
                UsedGb = 11.4,
                TotalGb = 32.0,
                UsagePercent = 35.6,
            },
            Storage =
            [
                new StorageTelemetry
                {
                    Name = "NVMe SSD",
                    TemperatureC = 44.0,
                    UsagePercent = 62.0,
                },
            ],
        };

        var json = PanelJson.Serialize(original);
        var restored = PanelJson.Deserialize<TelemetryMessage>(json);

        Assert.AreEqual(MessageTypes.Telemetry, restored.Type);
        Assert.AreEqual("AMD Ryzen 7", restored.Cpu.Name);
        Assert.AreEqual(52.0, restored.Cpu.TemperatureC);
        Assert.AreEqual(65.2, restored.Cpu.PowerW);
        Assert.AreEqual("NVIDIA GeForce RTX", restored.Gpu.Name);
        Assert.AreEqual(31.0, restored.Gpu.UsagePercent);
        Assert.AreEqual(8.0, restored.Gpu.MemoryTotalGb);
        Assert.AreEqual(11.4, restored.Memory.UsedGb);
        Assert.AreEqual(32.0, restored.Memory.TotalGb);
        Assert.AreEqual(35.6, restored.Memory.UsagePercent);
        Assert.AreEqual(1, restored.Storage.Count);
        Assert.AreEqual("NVMe SSD", restored.Storage[0].Name);
        Assert.AreEqual(44.0, restored.Storage[0].TemperatureC);
    }

    [TestMethod]
    public void Missing_telemetry_values_round_trip_as_real_json_nulls()
    {
        var original = new TelemetryMessage
        {
            Cpu = new CpuTelemetry
            {
                Name = "CPU",
                TemperatureC = null,
                UsagePercent = 18.0,
                ClockGhz = null,
                PowerW = null,
            },
            Gpu = new GpuTelemetry(),
            Memory = new MemoryTelemetry(),
            Storage = Array.Empty<StorageTelemetry>(),
        };

        var json = PanelJson.Serialize(original);
        var restored = PanelJson.Deserialize<TelemetryMessage>(json);

        StringAssert.Contains(json, "\"temperatureC\":null");
        StringAssert.Contains(json, "\"clockGhz\":null");
        StringAssert.Contains(json, "\"powerW\":null");
        StringAssert.Contains(json, "\"storage\":[]");
        Assert.IsNull(restored.Cpu.TemperatureC);
        Assert.AreEqual(18.0, restored.Cpu.UsagePercent);
        Assert.IsNull(restored.Cpu.ClockGhz);
        Assert.IsNull(restored.Gpu.Name);
        Assert.IsNull(restored.Memory.TotalGb);
        Assert.AreEqual(0, restored.Storage.Count);
    }

    [TestMethod]
    public void Telemetry_deserializes_when_optional_fields_are_absent()
    {
        const string json = """
            {
              "type":"telemetry",
              "cpu":{"usagePercent":18.0},
              "gpu":{},
              "memory":{"usedGb":11.4,"totalGb":32.0}
            }
            """;

        var restored = PanelJson.Deserialize<TelemetryMessage>(json);

        Assert.AreEqual(18.0, restored.Cpu.UsagePercent);
        Assert.IsNull(restored.Cpu.Name);
        Assert.IsNull(restored.Cpu.TemperatureC);
        Assert.IsNull(restored.Cpu.ClockGhz);
        Assert.IsNull(restored.Gpu.Name);
        Assert.IsNull(restored.Gpu.MemoryUsagePercent);
        Assert.AreEqual(11.4, restored.Memory.UsedGb);
        Assert.AreEqual(32.0, restored.Memory.TotalGb);
        Assert.IsNull(restored.Memory.UsagePercent);
        Assert.AreEqual(0, restored.Storage.Count);
    }

    [TestMethod]
    public void Command_deserialization_recognizes_wire_message()
    {
        const string json = "{\"type\":\"command\",\"command\":\"media.play_pause\"}";

        var command = PanelJson.Deserialize<CommandMessage>(json);

        Assert.AreEqual(MessageTypes.Command, command.Type);
        Assert.AreEqual(CommandNames.MediaPlayPause, command.Command);
    }

    [TestMethod]
    public void Existing_command_without_value_keeps_original_wire_shape()
    {
        var json = PanelJson.Serialize(new CommandMessage
        {
            Command = CommandNames.MediaPlayPause,
        });

        StringAssert.Contains(json, "\"command\":\"media.play_pause\"");
        Assert.IsFalse(json.Contains("\"value\"", StringComparison.Ordinal));
        Assert.AreEqual(PanelProtocol.PROTOCOL_VERSION, 1);
    }

    [TestMethod]
    public void Volume_state_round_trips_exact_wire_fields()
    {
        var original = new VolumeStateMessage
        {
            Available = true,
            VolumePercent = 72,
            IsMuted = false,
        };

        var json = PanelJson.Serialize(original);
        var restored = PanelJson.Deserialize<VolumeStateMessage>(json);

        Assert.AreEqual(original, restored);
        StringAssert.Contains(json, "\"type\":\"volume_state\"");
        StringAssert.Contains(json, "\"available\":true");
        StringAssert.Contains(json, "\"volumePercent\":72");
        StringAssert.Contains(json, "\"isMuted\":false");
    }

    [TestMethod]
    public void Volume_set_command_round_trips_integer_value()
    {
        var original = new CommandMessage
        {
            Command = CommandNames.VolumeSet,
            Value = 72,
        };

        var json = PanelJson.Serialize(original);
        var restored = PanelJson.Deserialize<CommandMessage>(json);

        Assert.AreEqual(CommandNames.VolumeSet, restored.Command);
        Assert.AreEqual(72, restored.Value);
        StringAssert.Contains(json, "\"value\":72");
    }

    [TestMethod]
    public void Volume_toggle_mute_omits_unused_value()
    {
        var json = PanelJson.Serialize(new CommandMessage
        {
            Command = CommandNames.VolumeToggleMute,
        });

        Assert.IsFalse(json.Contains("\"value\"", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow(CommandNames.VolumeSet, VolumeCommand.Set)]
    [DataRow(CommandNames.VolumeToggleMute, VolumeCommand.ToggleMute)]
    public void Volume_commands_use_centralized_wire_names(
        string wireCommand,
        VolumeCommand expectedCommand)
    {
        Assert.IsTrue(CommandNames.TryParseVolume(wireCommand, out var command));
        Assert.AreEqual(expectedCommand, command);
    }

    [DataTestMethod]
    [DataRow(CommandNames.DesktopStart, DesktopCommand.Start)]
    [DataRow(CommandNames.DesktopStop, DesktopCommand.Stop)]
    public void Desktop_commands_round_trip_and_parse(
        string wireCommand,
        DesktopCommand expectedCommand)
    {
        var original = new CommandMessage { Command = wireCommand };

        var restored = PanelJson.Deserialize<CommandMessage>(
            PanelJson.Serialize(original));

        Assert.AreEqual(wireCommand, restored.Command);
        Assert.IsTrue(CommandNames.TryParseDesktop(restored.Command, out var command));
        Assert.AreEqual(expectedCommand, command);
    }

    [DataTestMethod]
    [DataRow(DesktopServiceState.Stopped, "stopped")]
    [DataRow(DesktopServiceState.Starting, "starting")]
    [DataRow(DesktopServiceState.Running, "running")]
    [DataRow(DesktopServiceState.Stopping, "stopping")]
    [DataRow(DesktopServiceState.Error, "error")]
    public void Desktop_state_uses_exact_wire_state(
        DesktopServiceState serviceState,
        string wireState)
    {
        var original = DesktopProtocolMapper.CreateStateMessage(
            new DesktopServiceSnapshot(
                serviceState,
                serviceState == DesktopServiceState.Error
                    ? DesktopErrorCodes.StartFailed
                    : null));

        var json = PanelJson.Serialize(original);
        var restored = PanelJson.Deserialize<DesktopStateMessage>(json);

        StringAssert.Contains(json, $"\"state\":\"{wireState}\"");
        Assert.AreEqual(wireState, restored.State);
        if (wireState == DesktopStates.Error)
        {
            Assert.AreEqual(DesktopErrorCodes.StartFailed, restored.ErrorCode);
        }
        else
        {
            Assert.IsFalse(json.Contains("errorCode", StringComparison.Ordinal));
            Assert.IsNull(restored.ErrorCode);
        }
    }

    [TestMethod]
    public void Desktop_error_exposes_only_stable_error_code()
    {
        var error = new DesktopStateMessage
        {
            State = DesktopStates.Error,
            ErrorCode = DesktopErrorCodes.StartFailed,
        };

        var errorJson = PanelJson.Serialize(error);
        var restored = PanelJson.Deserialize<DesktopStateMessage>(errorJson);

        StringAssert.Contains(errorJson, "\"state\":\"error\"");
        StringAssert.Contains(errorJson, "\"errorCode\":\"start_failed\"");
        Assert.IsFalse(errorJson.Contains("stack", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(errorJson.Contains("exception", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(DesktopStates.Error, restored.State);
        Assert.AreEqual(DesktopErrorCodes.StartFailed, restored.ErrorCode);
    }

    [TestMethod]
    public void Desktop_mapper_never_exposes_error_code_for_non_error_state()
    {
        var message = DesktopProtocolMapper.CreateStateMessage(
            new DesktopServiceSnapshot(
                DesktopServiceState.Running,
                "must_not_leak"));

        var json = PanelJson.Serialize(message);

        Assert.AreEqual(DesktopStates.Running, message.State);
        Assert.IsNull(message.ErrorCode);
        Assert.IsFalse(json.Contains("errorCode", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Touch_command_round_trips_normalized_coordinates_and_generation()
    {
        var original = new CommandMessage
        {
            Command = CommandNames.TouchDown,
            PointerId = 0,
            X = 0.42,
            Y = 0.67,
            Generation = 3,
        };

        var json = PanelJson.Serialize(original);
        var restored = PanelJson.Deserialize<CommandMessage>(json);

        Assert.AreEqual(original, restored);
        Assert.IsTrue(CommandNames.TryParseTouch(restored.Command, out var command));
        Assert.AreEqual(DesktopTouchCommand.Down, command);
        StringAssert.Contains(json, "\"pointerId\":0");
        StringAssert.Contains(json, "\"generation\":3");
    }

    [TestMethod]
    public void Running_desktop_exposes_touch_generation_but_stopped_omits_it()
    {
        var running = DesktopProtocolMapper.CreateStateMessage(
            new DesktopServiceSnapshot(DesktopServiceState.Running),
            touchGeneration: 7);
        var stopped = DesktopProtocolMapper.CreateStateMessage(
            new DesktopServiceSnapshot(DesktopServiceState.Stopped),
            touchGeneration: 7);

        StringAssert.Contains(PanelJson.Serialize(running), "\"generation\":7");
        Assert.AreEqual(7L, running.Generation);
        Assert.IsNull(stopped.Generation);
        Assert.IsFalse(
            PanelJson.Serialize(stopped).Contains("generation", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Media_state_round_trips_real_session_fields()
    {
        var original = new MediaStateMessage
        {
            Available = true,
            MediaId = "Spotify.exe|midnight-city",
            SourceApp = "Spotify.exe",
            Title = "Midnight City",
            Artist = "M83",
            Album = "Hurry Up, We're Dreaming",
            IsPlaying = true,
            PositionMs = 94_000,
            DurationMs = 243_000,
            CanPlayPause = true,
            CanPrevious = true,
            CanNext = false,
        };

        var restored = PanelJson.Deserialize<MediaStateMessage>(PanelJson.Serialize(original));

        Assert.AreEqual(original, restored);
    }

    [TestMethod]
    public void Unavailable_media_omits_unknown_metadata()
    {
        var json = PanelJson.Serialize(new MediaStateMessage { Available = false });
        var restored = PanelJson.Deserialize<MediaStateMessage>(json);

        StringAssert.Contains(json, "\"available\":false");
        Assert.IsFalse(json.Contains("\"mediaId\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"sourceApp\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"title\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"artist\"", StringComparison.Ordinal));
        Assert.IsFalse(restored.Available);
        Assert.IsNull(restored.MediaId);
        Assert.IsNull(restored.Title);
    }

    [TestMethod]
    public void Absent_artwork_has_no_mime_type_or_base64_payload()
    {
        var original = new MediaArtworkMessage
        {
            MediaId = "Spotify.exe|midnight-city",
            Available = false,
        };

        var json = PanelJson.Serialize(original);
        var restored = PanelJson.Deserialize<MediaArtworkMessage>(json);

        Assert.AreEqual(original, restored);
        Assert.IsFalse(json.Contains("\"mimeType\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"dataBase64\"", StringComparison.Ordinal));
    }
}
