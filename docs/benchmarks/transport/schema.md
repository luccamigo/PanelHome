# Result schema

Each JSON artifact is one complete benchmark session. The CSV contains one row
per latency or bulk repetition.

Core JSON fields:

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Result format version (currently 3) |
| `transport` | `adb-tcp` or `usb-native-experimental` |
| `scenario` | Dashboard/static/dynamic workload label |
| `durationSeconds` | Measured session wall time |
| `warmupSamples` | RTT samples discarded per size/repetition |
| `samplesPerPayload` | Retained RTT samples per size/repetition |
| `repetitions` | Number of complete short runs |
| `latency[]` | Payload size plus avg/p50/p95/p99/max RTT |
| `bulk[]` | Direction, payload size, and MB/s |
| `video` | Codec, encoder, resolution, the full FPS chain, MB/s, latencies, gaps/drops |
| `windows` | Process CPU average/p95 and start/end/maximum working set |
| `android` | Process CPU percentage, PSS, and Java heap start/end |
| `transportMetrics` | Payload/wire bytes, framing overhead, drops, reconnects, errors, queue depth |

The requested common comparison fields map as follows:

```text
transport, scenario, durationSeconds,
latency[].p50Milliseconds, latency[].p95Milliseconds,
latency[].p99Milliseconds, bulk[].megabytesPerSecond,
video.sendFps, video.megabytesPerSecond,
transportMetrics.videoDrops, windows.cpuPercent, android.cpuPercent,
windows.workingSetEndBytes, android.pssEndKilobytes,
transportMetrics.reconnects, transportMetrics.errors
```

Android render FPS and decoder errors remain in the paired sanitized Android log
until they are promoted into the binary stats response; do not silently replace
them with host send FPS.

## `video` fields

The host side of the pipeline is recorded stage by stage, so a shortfall can be
attributed instead of guessed. A run where `driverCaptureFps` is already below
target is a capture/pacing result; one where `driverCaptureFps` holds but
`sendFps` drops is an encoder or carrier result. Reading `sendFps` alone cannot
tell those apart.

Note on casing: the JSON artifacts serialize .NET property names verbatim, so
the on-disk keys are PascalCase (`Video.SendFps`, `SchemaVersion`). The
lowercase paths used elsewhere in this document are logical field names, not
literal JSON keys.

| Field | Meaning |
| --- | --- |
| `Codec` | Negotiated codec (`H264`, `JPEG`) |
| `Encoder` | Concrete MFT chosen, e.g. `H264 Encoder MFT` for the software path |
| `Width`, `Height` | Negotiated frame size |
| `ConfiguredFps` | FPS the host asked the driver for |
| `NegotiatedFps` | Effective session FPS after client capability negotiation |
| `DriverCaptureFps` | Frames per second the display driver actually published |
| `HostReceiveFps` | Frames per second the host actually dequeued from the driver |
| `SendFps` | Encoded frames per second handed to the carrier |
| `MegabytesPerSecond` | Video bandwidth on the wire |
| `EncodeLatencyMilliseconds` | Mean encode time |
| `CaptureToSendMilliseconds` | Mean capture-to-carrier latency |
| `DropsOrSequenceGaps` | Video sequence gap counter |

A Dashboard run has no video session, so `Video` serializes as `{}` — every
field is null and null fields are omitted. That is expected, not a capture
failure.

Android receive/render FPS are deliberately **not** here — see the note above.
Extract them from the paired Android log:

```powershell
Select-String -Path <run>-android.log -Pattern 'receiveFps=|renderFps='
```

## Schema history

| Version | Change |
| --- | --- |
| 1 | Initial result format |
| 2 | Per-phase CPU/IO attribution (`phases[]`) |
| 3 | `video` widened to the full host pipeline chain (encoder, configured FPS, driver capture FPS, host receive FPS, encode and capture-to-send latency) |
