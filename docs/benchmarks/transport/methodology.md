# Methodology

## Primary comparison

The primary result is the normal PanelOS application and PanelHost pipeline, not
a codec-versus-synthetic comparison. Both transports use:

| Setting | Fixed value |
| --- | --- |
| Tablet | same physical tablet |
| PC and display driver | same machine and installed driver |
| Resolution | 1280x800 |
| Codec | H.264 |
| Preferred FPS | 60 |
| Bitrate | 10,000,000 bit/s |
| Encoder selection | unchanged between transports |
| Workload | same Dashboard/Desktop content |

Only `--transport adb` versus `--transport usb` changes. USB is never selected
implicitly and remains experimental.

The scenarios are `dashboard`, `desktop-static`, and `desktop-dynamic`. For the
dynamic run, start the same local video at the same position before each run.
Do not compare different clips, codecs, frame rates, or driver pacing.

## Harness

`scripts/run-transport-benchmark.ps1` builds and installs PanelHome, configures
ADB reverse when applicable, launches the app's opt-in benchmark responder, and
starts PanelHost with `--transport-benchmark`. It captures JSON, CSV, host logs,
Android logs, and a sanitized hardware record under the ignored local directory
`.artifacts/benchmarks/transport/` (or the explicit `-OutputDirectory`). CI uploads
the same directory as a run artifact.

The benchmark responder uses the reserved logical Assets channel and its own
binary `PBMK` protocol. It does not add or change any Panel Protocol message.
With native USB, Assets, Control, and Video share the same PLMX physical stream;
with ADB they use separate reversed TCP streams over the same ADB USB link. The
normal Control and Video sessions remain active, so loaded tests exercise the
real H.264 pipeline concurrently.

### Latency

For each of 32, 128, 512, and 1024 bytes:

- discard 100 warm-up echoes by default;
- retain 2,000 RTT samples per repetition;
- execute three repetitions;
- report average, p50, p95, p99, and maximum for every repetition;
- compare the median of repetition summaries, never the best run.

Run once on Dashboard and again while the identical H.264 Desktop workload is
active. The latter is the control-under-video result. PLMX's bounded video queue
and control-first scheduler are measured through queue depth, drops, p95, and
p99 rather than assumed to work.

### Bulk throughput

Every repetition transfers deterministic 1 MiB, 8 MiB, and 32 MiB payloads in
both directions. Decimal MB/s is reported. These measurements are additional
synthetic carrier diagnostics; they do not replace the end-to-end video result.

### Video

PanelHost's existing structured runtime observer supplies codec, resolution,
negotiated/send FPS, H.264 bytes/s, and sequence gaps. Android's existing
`VideoClientMetrics` logs supply received packets, bytes, queued access units,
rendered frames, codec errors, and frame ages. Capture the steady-state windows
after encoder/decoder startup.

### CPU and memory

PanelHost records its own process CPU average, 250 ms CPU samples (including
p95), and working set at start/end/maximum. The Android responder returns `Process.getElapsedCpuTime()`,
`SystemClock.elapsedRealtime()`, proportional set size (`Debug.getPss()`), and
Java heap bytes directly from the app process. This makes Android CPU/memory
available even when ADB is absent. CPU percentage is core-equivalent: 100% is one
logical core fully busy, so a multithreaded process can exceed 100%. Treat the Desktop Android number as total app
CPU, including decoder/rendering; do not label it transport-only CPU.

**CPU percentage is not a cost comparison between transports.** It is CPU divided
by wall time, and the transports complete identical work in very different wall
times, so the faster carrier reports the higher percentage for the same amount of
work. The primary figures are therefore **CPU seconds** and **CPU per mebibyte**;
percentage is retained only for continuity with earlier runs.

ADB/TCP additionally splits its cost across `adb.exe` on the host and `adbd` on
the tablet, neither of which is counted. Native USB has no such off-book cost, so
like-for-like comparisons understate ADB/TCP.

### Phase decomposition

A single number across a whole session cannot separate carrier cost from harness
cost. The harness measures three phases independently and reports CPU seconds,
CPU per MiB, physical read/write rates, average transfer size, physical
operations per logical frame, allocation rate, GC counts and Android reader and
writer thread CPU for each:

- `idle` - link up, no benchmark traffic, length set by `--benchmark-idle-seconds`;
- `latency` - all RTT payload sizes and repetitions;
- `bulk` - all bulk sizes and directions, both ways.

Per-phase rows are written to `<run>-phases.csv` alongside the JSON.

The harness must not appear in its own measurement. Deterministic payloads are
generated once and sliced rather than regenerated per repetition, receive buffers
are reused, and each benchmark message is written once rather than as separate
header and payload writes. The last of these previously produced a ~42 ms p95
artifact on ADB/TCP through delayed-ACK interaction on loopback; ADB latency
figures from before that fix are not comparable with later ones.

### Desktop precondition

Desktop scenarios capture through PanelDisplayDriver, which PanelHost can only
open when elevated. The harness probes elevation and driver reachability before
accepting the benchmark connection and fails early with an explicit message
rather than silently degrading into a Dashboard-shaped result. Run these with
`-Elevated` (a UAC prompt appears) or from an elevated shell.

### Carrier tunables

`-FragmentBytes` and `-ReadBufferBytes` set `PANELOS_PLMX_FRAGMENT` and
`PANELOS_USB_READ_BUFFER` so a sweep runs against the same binary that produced
the other rows. Both are clamped to whole 512-byte max packets. The fragment size
is a scheduling knob, not a wire limit: the peer validates only against the
maximum payload, so any smaller fragment stays protocol compatible.

### PLMX overhead

Transport metrics count logical payload bytes and physical wire bytes. PLMX wire
bytes include the fixed 16-byte header for handshake, OPEN, FIN, Control, Video,
and Assets frames. ADB/TCP reports application stream bytes; TCP/USB lower-layer
packet overhead is outside this counter and must be called out when comparing
framing percentages.

### Stability and reconnect

After short runs pass, hold each scenario for at least 30 minutes and retain
disconnects, reconnects, protocol/transport/decoder errors, video drops, control
failures, unexpected FINs, and start/end memory. Then perform at least 30 cycles
of connect, Desktop, disconnect, and reconnect. Finally test cable removal:

- native USB: physically remove and reconnect the cable;
- ADB: remove/recreate all three reverse mappings or remove the cable.

Expected behavior is a clean disconnect, bounded reconnect, and no orphan
session. These long/cable tests require an operator and were not executed while
the WinUSB binding blocker remained.

## Interpretation

Classify each dimension independently as `better`, `equivalent`, `worse`, or
`not yet reliable`. Recommend native USB as a future default only if stability
is at least equivalent, control p95/p99 remain bounded under H.264 load,
reconnect works, throughput sustains Desktop, CPU does not regress materially,
and no critical device-specific error remains.
