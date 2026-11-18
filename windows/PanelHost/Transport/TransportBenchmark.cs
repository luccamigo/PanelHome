using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PanelHost.Management;

namespace PanelHost.Transport;

internal static class TransportBenchmarkProtocol
{
    internal const int Port = 8767;
    internal const uint Magic = 0x4B4D4250; // PBMK
    internal const byte Version = 1;
    internal const int HeaderSize = 12;
    internal const int MaximumPayload = 64 * 1024 * 1024;

    internal enum Command : byte
    {
        Echo = 1,
        EchoResponse = 2,
        Download = 3,
        Acknowledgement = 4,
        UploadRequest = 5,
        UploadData = 6,
        StatsRequest = 7,
        StatsResponse = 8,
        Finish = 9,
    }

    internal static void WriteHeader(Span<byte> target, Command command, int payloadLength)
    {
        if (payloadLength is < 0 or > MaximumPayload) throw new ArgumentOutOfRangeException(nameof(payloadLength));
        BinaryPrimitives.WriteUInt32LittleEndian(target, Magic);
        target[4] = Version;
        target[5] = (byte)command;
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(6), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(8), checked((uint)payloadLength));
    }

    internal static byte[] Header(Command command, int payloadLength)
    {
        var bytes = new byte[HeaderSize];
        WriteHeader(bytes, command, payloadLength);
        return bytes;
    }

    internal static (Command Command, int PayloadLength) Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException("Invalid transport benchmark header.");
        if (header[4] != Version) throw new InvalidDataException($"Unsupported benchmark protocol v{header[4]}.");
        var command = (Command)header[5];
        if (!Enum.IsDefined(command)) throw new InvalidDataException($"Unknown benchmark command {(byte)command}.");
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]));
        if (length > MaximumPayload) throw new InvalidDataException("Benchmark payload exceeds 64 MiB.");
        return (command, length);
    }
}

internal sealed record TransportBenchmarkOptions(
    string OutputDirectory,
    string Scenario,
    int Samples,
    int WarmupSamples,
    int Repetitions,
    int IdleSeconds)
{
    internal bool RequiresDesktop =>
        Scenario.StartsWith("desktop", StringComparison.OrdinalIgnoreCase);

}

internal sealed class TransportBenchmarkServer(
    IPanelConnectionListener listener,
    IPanelTransport transport,
    PanelHostRuntimeState runtimeState,
    TransportBenchmarkOptions options,
    TextWriter? log = null,
    TransportBenchmarkPrecondition? precondition = null)
{
    private static readonly int[] LatencyPayloadSizes = [32, 128, 512, 1024];
    private static readonly int[] BulkPayloadSizes = [1 * 1024 * 1024, 8 * 1024 * 1024, 32 * 1024 * 1024];
    private readonly TextWriter _log = log ?? Console.Out;

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        // The precondition is checked before the listener accepts anything so a
        // Desktop run cannot silently degrade into a Dashboard measurement.
        if (options.RequiresDesktop && precondition is { SatisfiesDesktopScenario: false })
            throw new InvalidOperationException(precondition.DescribeFailure(options.Scenario));

        await using var listenerLifetime = listener;
        await listener.StartAsync(cancellationToken).ConfigureAwait(false);
        _log.WriteLine(
            $"[Benchmark] Waiting on logical Assets channel; scenario={options.Scenario}, " +
            $"samples={options.Samples}, warmup={options.WarmupSamples}, repetitions={options.Repetitions}, " +
            $"idleSeconds={options.IdleSeconds}.");
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await RunSessionAsync(connection.Stream, cancellationToken).ConfigureAwait(false);
                await SaveAsync(result, cancellationToken).ConfigureAwait(false);
                PrintSummary(result);
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                transport.Metrics.Error();
                throw new IOException($"Benchmark session failed: {exception.Message}", exception);
            }
        }
    }

    private async Task<TransportBenchmarkResult> RunSessionAsync(Stream stream, CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var wall = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        using var processSamples = new ProcessSampleCollector(process);
        var processCpuStart = process.TotalProcessorTime;
        var workingSetStart = process.WorkingSet64;
        var metricsStart = transport.Metrics.Snapshot();
        var androidStart = await ReadAndroidStatsAsync(stream, cancellationToken).ConfigureAwait(false);
        var latencyRuns = new List<LatencyBenchmarkRun>();
        var bulkRuns = new List<BulkBenchmarkRun>();
        var phases = new List<BenchmarkPhase>();

        // Phases are measured separately because a single percentage over the
        // whole session cannot distinguish carrier cost from harness cost, and
        // because the two transports finish the same work in very different
        // wall times - which alone moves any percent-of-wall number.
        phases.Add(await MeasurePhaseAsync(
            "idle",
            stream,
            process,
            () => Task.Delay(TimeSpan.FromSeconds(options.IdleSeconds), cancellationToken),
            cancellationToken).ConfigureAwait(false));

        phases.Add(await MeasurePhaseAsync(
            "latency",
            stream,
            process,
            async () =>
            {
                for (var repetition = 1; repetition <= options.Repetitions; repetition++)
                {
                    foreach (var size in LatencyPayloadSizes)
                    {
                        latencyRuns.Add(await MeasureLatencyAsync(
                            stream,
                            repetition,
                            size,
                            cancellationToken).ConfigureAwait(false));
                    }
                }
            },
            cancellationToken).ConfigureAwait(false));

        phases.Add(await MeasurePhaseAsync(
            "bulk",
            stream,
            process,
            async () =>
            {
                for (var repetition = 1; repetition <= options.Repetitions; repetition++)
                {
                    foreach (var size in BulkPayloadSizes)
                    {
                        bulkRuns.Add(await MeasureDownloadAsync(stream, repetition, size, cancellationToken).ConfigureAwait(false));
                        bulkRuns.Add(await MeasureUploadAsync(stream, repetition, size, cancellationToken).ConfigureAwait(false));
                    }
                }
            },
            cancellationToken).ConfigureAwait(false));

        var androidEnd = await ReadAndroidStatsAsync(stream, cancellationToken).ConfigureAwait(false);
        await WriteFrameAsync(stream, TransportBenchmarkProtocol.Command.Finish, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        wall.Stop();
        await processSamples.StopAsync().ConfigureAwait(false);
        process.Refresh();
        var processCpu = process.TotalProcessorTime - processCpuStart;
        var transportDelta = transport.Metrics.Snapshot() - metricsStart;
        var video = runtimeState.Snapshot.Video;
        return new TransportBenchmarkResult(
            SchemaVersion: 3,
            Transport: transport.Name,
            Scenario: options.Scenario,
            StartedAtUtc: startedUtc,
            DurationSeconds: wall.Elapsed.TotalSeconds,
            WarmupSamples: options.WarmupSamples,
            SamplesPerPayload: options.Samples,
            Repetitions: options.Repetitions,
            Latency: latencyRuns,
            Bulk: bulkRuns,
            Phases: phases,
            Video: new VideoBenchmarkSnapshot(
                video.Codec,
                video.Encoder,
                video.Width,
                video.Height,
                video.ConfiguredFps,
                video.NegotiatedFps,
                video.DriverCaptureFps,
                video.HostReceiveFps,
                video.SendFps,
                video.BandwidthMegabytesPerSecond,
                video.EncodeLatencyMilliseconds,
                video.CaptureToSendMilliseconds,
                video.SequenceGap),
            Windows: new ProcessBenchmarkSnapshot(
                CpuPercent: CpuPercent(processCpu.TotalMilliseconds, wall.Elapsed.TotalMilliseconds),
                CpuSeconds: processCpu.TotalSeconds,
                CpuP95Percent: processSamples.CpuP95Percent,
                WorkingSetStartBytes: workingSetStart,
                WorkingSetEndBytes: process.WorkingSet64,
                WorkingSetMaximumBytes: processSamples.WorkingSetMaximumBytes),
            Android: AndroidProcessBenchmarkSnapshot.Between(androidStart, androidEnd),
            Precondition: precondition,
            TransportMetrics: new TransportBenchmarkMetrics(
                transportDelta.PayloadBytesTx,
                transportDelta.PayloadBytesRx,
                transportDelta.WireBytesTx,
                transportDelta.WireBytesRx,
                OverheadPercent(transportDelta.PayloadBytesTx, transportDelta.WireBytesTx),
                OverheadPercent(transportDelta.PayloadBytesRx, transportDelta.WireBytesRx),
                transportDelta.VideoDrops,
                transportDelta.Reconnects,
                transportDelta.Errors,
                transportDelta.VideoQueueDepth));
    }

    /// <summary>
    /// Brackets one workload with host CPU, Android CPU, physical I/O counters
    /// and managed allocation counters so each phase can be attributed on its
    /// own terms instead of through a session-wide average.
    /// </summary>
    private async Task<BenchmarkPhase> MeasurePhaseAsync(
        string name,
        Stream stream,
        Process process,
        Func<Task> body,
        CancellationToken cancellationToken)
    {
        var androidStart = await ReadAndroidStatsAsync(stream, cancellationToken).ConfigureAwait(false);
        process.Refresh();
        var hostCpuStart = process.TotalProcessorTime;
        var ioStart = transport.Metrics.Io.Snapshot();
        var payloadStart = transport.Metrics.Snapshot();
        var allocationStart = ManagedAllocationSnapshot.Capture();
        var videoStart = runtimeState.Snapshot.Video;
        var wall = Stopwatch.StartNew();
        await body().ConfigureAwait(false);
        wall.Stop();
        process.Refresh();
        var hostCpu = process.TotalProcessorTime - hostCpuStart;
        var androidEnd = await ReadAndroidStatsAsync(stream, cancellationToken).ConfigureAwait(false);
        var io = transport.Metrics.Io.Snapshot() - ioStart;
        var payload = transport.Metrics.Snapshot() - payloadStart;
        var allocations = ManagedAllocationSnapshot.Capture() - allocationStart;
        var androidIo = AndroidIoSnapshot.Between(androidStart, androidEnd);
        var seconds = wall.Elapsed.TotalSeconds;
        var mebibytes = (payload.PayloadBytesTx + payload.PayloadBytesRx) / 1048576d;
        var video = runtimeState.Snapshot.Video;
        return new BenchmarkPhase(
            Name: name,
            DurationSeconds: seconds,
            HostCpuSeconds: hostCpu.TotalSeconds,
            HostCpuPercent: CpuPercent(hostCpu.TotalMilliseconds, wall.Elapsed.TotalMilliseconds),
            AndroidCpuSeconds: (androidEnd.CpuMilliseconds - androidStart.CpuMilliseconds) / 1000d,
            AndroidCpuPercent: AndroidProcessBenchmarkSnapshot.Between(androidStart, androidEnd).CpuPercent,
            PayloadBytesTx: payload.PayloadBytesTx,
            PayloadBytesRx: payload.PayloadBytesRx,
            HostCpuMillisecondsPerMebibyte: mebibytes <= 0 ? 0 : hostCpu.TotalMilliseconds / mebibytes,
            AndroidCpuMillisecondsPerMebibyte: mebibytes <= 0
                ? 0
                : (androidEnd.CpuMilliseconds - androidStart.CpuMilliseconds) / mebibytes,
            HostReadsPerSecond: io.ReadsPerSecond(seconds),
            HostWritesPerSecond: io.WritesPerSecond(seconds),
            HostBytesPerRead: io.BytesPerRead,
            HostBytesPerWrite: io.BytesPerWrite,
            HostWritesPerFrame: io.WritesPerFrame,
            HostReadsPerFrame: io.ReadsPerFrame,
            HostFramesTxPerSecond: io.FramesTxPerSecond(seconds),
            HostFramesRxPerSecond: io.FramesRxPerSecond(seconds),
            HostIo: io,
            AndroidIo: androidIo,
            AndroidReadsPerSecond: seconds <= 0 ? 0 : androidIo.ReadCalls / seconds,
            AndroidWritesPerSecond: seconds <= 0 ? 0 : androidIo.WriteCalls / seconds,
            HostAllocatedBytes: allocations.AllocatedBytes,
            HostAllocatedBytesPerSecond: seconds <= 0 ? 0 : allocations.AllocatedBytes / seconds,
            HostGen0Collections: allocations.Gen0Collections,
            HostGen1Collections: allocations.Gen1Collections,
            HostGen2Collections: allocations.Gen2Collections,
            VideoSendFpsStart: videoStart.SendFps,
            VideoSendFpsEnd: video.SendFps,
            VideoSequenceGap: video.SequenceGap);
    }

    private async Task<LatencyBenchmarkRun> MeasureLatencyAsync(
        Stream stream,
        int repetition,
        int payloadSize,
        CancellationToken cancellationToken)
    {
        var payload = DeterministicPayload(payloadSize);
        for (var index = 0; index < options.WarmupSamples; index++)
            await EchoAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        var samples = new double[options.Samples];
        for (var index = 0; index < samples.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            await EchoAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(samples);
        return new LatencyBenchmarkRun(
            repetition,
            payloadSize,
            samples.Average(),
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99),
            samples[^1]);
    }

    private static async Task EchoAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await WriteFrameAsync(stream, TransportBenchmarkProtocol.Command.Echo, payload, cancellationToken).ConfigureAwait(false);
        var response = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (response.Command != TransportBenchmarkProtocol.Command.EchoResponse ||
            !response.Payload.Span.SequenceEqual(payload.Span))
            throw new InvalidDataException("Benchmark echo response mismatch.");
    }

    private static async Task<BulkBenchmarkRun> MeasureDownloadAsync(
        Stream stream,
        int repetition,
        int size,
        CancellationToken cancellationToken)
    {
        var payload = DeterministicPayload(size);
        var started = Stopwatch.GetTimestamp();
        await WriteFrameAsync(stream, TransportBenchmarkProtocol.Command.Download, payload, cancellationToken).ConfigureAwait(false);
        var response = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (response.Command != TransportBenchmarkProtocol.Command.Acknowledgement)
            throw new InvalidDataException("Expected benchmark download acknowledgement.");
        var elapsed = Stopwatch.GetElapsedTime(started);
        return new BulkBenchmarkRun(repetition, "host_to_android", size, MegabytesPerSecond(size, elapsed));
    }

    private static async Task<BulkBenchmarkRun> MeasureUploadAsync(
        Stream stream,
        int repetition,
        int size,
        CancellationToken cancellationToken)
    {
        var request = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(request, size);
        var started = Stopwatch.GetTimestamp();
        await WriteFrameAsync(stream, TransportBenchmarkProtocol.Command.UploadRequest, request, cancellationToken).ConfigureAwait(false);
        var response = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (response.Command != TransportBenchmarkProtocol.Command.UploadData || response.Payload.Length != size)
            throw new InvalidDataException("Benchmark upload response mismatch.");
        var elapsed = Stopwatch.GetElapsedTime(started);
        return new BulkBenchmarkRun(repetition, "android_to_host", size, MegabytesPerSecond(size, elapsed));
    }

    private static async Task<AndroidStats> ReadAndroidStatsAsync(Stream stream, CancellationToken cancellationToken)
    {
        await WriteFrameAsync(stream, TransportBenchmarkProtocol.Command.StatsRequest, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        var response = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (response.Command != TransportBenchmarkProtocol.Command.StatsResponse ||
            response.Payload.Length < AndroidStats.BaseBytes)
            throw new InvalidDataException("Invalid Android benchmark stats response.");
        return AndroidStats.Parse(response.Payload.Span);
    }

    private async Task SaveAsync(TransportBenchmarkResult result, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var safeTransport = result.Transport.Replace('-', '_');
        var safeScenario = string.Concat(result.Scenario.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        var stamp = result.StartedAtUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = $"{stamp}-{safeTransport}-{safeScenario}";
        var jsonPath = Path.Combine(options.OutputDirectory, baseName + ".json");
        var csvPath = Path.Combine(options.OutputDirectory, baseName + ".csv");
        await using (var json = File.Create(jsonPath))
        {
            await JsonSerializer.SerializeAsync(
                json,
                result,
                new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull },
                cancellationToken).ConfigureAwait(false);
        }
        var csv = new StringBuilder("transport,scenario,kind,direction,repetition,payloadBytes,avgMs,p50Ms,p95Ms,p99Ms,maxMs,megabytesPerSecond\n");
        foreach (var run in result.Latency)
            csv.AppendLine(FormattableString.Invariant($"{result.Transport},{result.Scenario},latency,,{run.Repetition},{run.PayloadBytes},{run.AverageMilliseconds:F6},{run.P50Milliseconds:F6},{run.P95Milliseconds:F6},{run.P99Milliseconds:F6},{run.MaximumMilliseconds:F6},"));
        foreach (var run in result.Bulk)
            csv.AppendLine(FormattableString.Invariant($"{result.Transport},{result.Scenario},bulk,{run.Direction},{run.Repetition},{run.PayloadBytes},,,,,,{run.MegabytesPerSecond:F6}"));
        var phaseCsv = new StringBuilder(
            "transport,scenario,phase,wallSeconds,hostCpuSeconds,hostCpuPercent,androidCpuSeconds,androidCpuPercent," +
            "payloadMiB,hostCpuMsPerMiB,androidCpuMsPerMiB,hostReadsPerSecond,hostWritesPerSecond,hostBytesPerRead," +
            "hostBytesPerWrite,hostWritesPerFrame,hostReadsPerFrame,hostAllocMiBPerSecond,hostGen0,hostGen1,hostGen2," +
            "androidReadsPerSecond,androidWritesPerSecond,androidBytesPerRead,androidBytesPerWrite,androidReadsPerFrame," +
            "androidShortReads,androidPayloadAllocations,androidReaderCpuSeconds,androidWriterCpuSeconds\n");
        foreach (var phase in result.Phases)
        {
            phaseCsv.AppendLine(string.Join(',', new[]
            {
                result.Transport,
                result.Scenario,
                phase.Name,
                Number(phase.DurationSeconds, "F4"),
                Number(phase.HostCpuSeconds, "F4"),
                Number(phase.HostCpuPercent, "F4"),
                Number(phase.AndroidCpuSeconds, "F4"),
                Number(phase.AndroidCpuPercent, "F4"),
                Number((phase.PayloadBytesTx + phase.PayloadBytesRx) / 1048576d, "F4"),
                Number(phase.HostCpuMillisecondsPerMebibyte, "F4"),
                Number(phase.AndroidCpuMillisecondsPerMebibyte, "F4"),
                Number(phase.HostReadsPerSecond, "F2"),
                Number(phase.HostWritesPerSecond, "F2"),
                Number(phase.HostBytesPerRead, "F2"),
                Number(phase.HostBytesPerWrite, "F2"),
                Number(phase.HostWritesPerFrame, "F4"),
                Number(phase.HostReadsPerFrame, "F4"),
                Number(phase.HostAllocatedBytesPerSecond / 1048576d, "F4"),
                phase.HostGen0Collections.ToString(CultureInfo.InvariantCulture),
                phase.HostGen1Collections.ToString(CultureInfo.InvariantCulture),
                phase.HostGen2Collections.ToString(CultureInfo.InvariantCulture),
                Number(phase.AndroidReadsPerSecond, "F2"),
                Number(phase.AndroidWritesPerSecond, "F2"),
                Number(phase.AndroidIo.BytesPerRead, "F2"),
                Number(phase.AndroidIo.BytesPerWrite, "F2"),
                Number(phase.AndroidIo.ReadsPerFrame, "F4"),
                phase.AndroidIo.ShortReads.ToString(CultureInfo.InvariantCulture),
                phase.AndroidIo.PayloadAllocations.ToString(CultureInfo.InvariantCulture),
                Number(phase.AndroidIo.ReaderCpuSeconds, "F4"),
                Number(phase.AndroidIo.WriterCpuSeconds, "F4"),
            }));
        }
        var phasePath = Path.Combine(options.OutputDirectory, baseName + "-phases.csv");
        await File.WriteAllTextAsync(phasePath, phaseCsv.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(csvPath, csv.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        _log.WriteLine($"[Benchmark] Raw artifacts: {jsonPath}; {csvPath}; {phasePath}");
    }

    private void PrintSummary(TransportBenchmarkResult result)
    {
        _log.WriteLine($"[Benchmark] Completed {result.Transport}/{result.Scenario} in {result.DurationSeconds:F1}s.");
        foreach (var group in result.Latency.GroupBy(run => run.PayloadBytes))
            _log.WriteLine($"[Benchmark] RTT {group.Key} B median-run p50={Median(group.Select(run => run.P50Milliseconds)):F3}ms p95={Median(group.Select(run => run.P95Milliseconds)):F3}ms p99={Median(group.Select(run => run.P99Milliseconds)):F3}ms.");
        foreach (var group in result.Bulk.GroupBy(run => (run.Direction, run.PayloadBytes)))
            _log.WriteLine($"[Benchmark] Bulk {group.Key.Direction} {group.Key.PayloadBytes / 1024 / 1024} MiB median={Median(group.Select(run => run.MegabytesPerSecond)):F2} MB/s.");
        foreach (var phase in result.Phases)
        {
            _log.WriteLine(
                $"[Benchmark] Phase {phase.Name} wall={phase.DurationSeconds:F2}s " +
                $"hostCpu={phase.HostCpuSeconds:F2}s ({phase.HostCpuPercent:F1}%) " +
                $"androidCpu={phase.AndroidCpuSeconds:F2}s ({phase.AndroidCpuPercent:F1}%) " +
                $"payload={(phase.PayloadBytesTx + phase.PayloadBytesRx) / 1048576d:F1} MiB.");
            _log.WriteLine(
                $"[Benchmark] Phase {phase.Name} hostIo reads/s={phase.HostReadsPerSecond:F0} " +
                $"writes/s={phase.HostWritesPerSecond:F0} bytes/read={phase.HostBytesPerRead:F0} " +
                $"bytes/write={phase.HostBytesPerWrite:F0} writes/frame={phase.HostWritesPerFrame:F2} " +
                $"reads/frame={phase.HostReadsPerFrame:F2} alloc={phase.HostAllocatedBytesPerSecond / 1048576d:F1} MiB/s " +
                $"gc={phase.HostGen0Collections}/{phase.HostGen1Collections}/{phase.HostGen2Collections}.");
            _log.WriteLine(
                $"[Benchmark] Phase {phase.Name} androidIo reads/s={phase.AndroidReadsPerSecond:F0} " +
                $"writes/s={phase.AndroidWritesPerSecond:F0} bytes/read={phase.AndroidIo.BytesPerRead:F0} " +
                $"bytes/write={phase.AndroidIo.BytesPerWrite:F0} reads/frame={phase.AndroidIo.ReadsPerFrame:F2} " +
                $"shortReads={phase.AndroidIo.ShortReads} payloadAllocs={phase.AndroidIo.PayloadAllocations} " +
                $"readerCpu={phase.AndroidIo.ReaderCpuSeconds:F2}s writerCpu={phase.AndroidIo.WriterCpuSeconds:F2}s.");
            if (phase.PayloadBytesTx + phase.PayloadBytesRx > 0)
            {
                _log.WriteLine(
                    $"[Benchmark] Phase {phase.Name} cost hostCpu={phase.HostCpuMillisecondsPerMebibyte:F2} ms/MiB " +
                    $"androidCpu={phase.AndroidCpuMillisecondsPerMebibyte:F2} ms/MiB.");
            }
        }
    }

    /// <summary>
    /// Header and payload go out as one logical write for small frames so the
    /// carrier sees one frame per benchmark message instead of two. Bulk
    /// payloads keep the header separate rather than staging tens of megabytes.
    /// </summary>
    private static readonly byte[] SendBuffer =
        new byte[TransportBenchmarkProtocol.HeaderSize + CoalesceLimit];
    private const int CoalesceLimit = 64 * 1024;

    private static async Task WriteFrameAsync(
        Stream stream,
        TransportBenchmarkProtocol.Command command,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        TransportBenchmarkProtocol.WriteHeader(SendBuffer, command, payload.Length);
        if (payload.Length <= CoalesceLimit)
        {
            payload.Span.CopyTo(SendBuffer.AsSpan(TransportBenchmarkProtocol.HeaderSize));
            await stream
                .WriteAsync(SendBuffer.AsMemory(0, TransportBenchmarkProtocol.HeaderSize + payload.Length), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await stream
                .WriteAsync(SendBuffer.AsMemory(0, TransportBenchmarkProtocol.HeaderSize), cancellationToken)
                .ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reuses one growing receive buffer. Allocating a fresh 32 MiB array per
    /// bulk frame charged the harness's own large-object-heap traffic to the
    /// transport under measurement.
    /// </summary>
    private static readonly ThreadLocal<byte[]> ReceiveBuffer = new(() => new byte[64 * 1024]);
    private static readonly byte[] HeaderBuffer = new byte[TransportBenchmarkProtocol.HeaderSize];

    private static async Task<(TransportBenchmarkProtocol.Command Command, ReadOnlyMemory<byte> Payload)> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        await ReadExactlyAsync(stream, HeaderBuffer, cancellationToken).ConfigureAwait(false);
        var parsed = TransportBenchmarkProtocol.Parse(HeaderBuffer);
        if (ReceiveBuffer.Value!.Length < parsed.PayloadLength)
            ReceiveBuffer.Value = new byte[parsed.PayloadLength];
        var payload = ReceiveBuffer.Value.AsMemory(0, parsed.PayloadLength);
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return (parsed.Command, payload);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Benchmark peer disconnected mid-frame.");
            offset += read;
        }
    }

    /// <summary>
    /// The payload depends only on its length, so it is generated once and
    /// sliced afterwards. Regenerating 32 MiB per repetition put the harness's
    /// own fill loop and large-object allocation inside the measured window.
    /// </summary>
    private static byte[] _deterministicPayload = [];

    private static ReadOnlyMemory<byte> DeterministicPayload(int length)
    {
        if (_deterministicPayload.Length < length)
        {
            var grown = new byte[length];
            for (var index = 0; index < grown.Length; index++) grown[index] = unchecked((byte)(index * 31 + 17));
            _deterministicPayload = grown;
        }
        return _deterministicPayload.AsMemory(0, length);
    }

    private static string Number(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 0
            ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2
            : sorted[sorted.Length / 2];
    }
    private static double MegabytesPerSecond(int bytes, TimeSpan elapsed) => bytes / 1_000_000d / elapsed.TotalSeconds;
    // Core-equivalent utilization: 100% means one logical core was fully busy.
    private static double CpuPercent(double cpuMs, double wallMs) => wallMs <= 0 ? 0 : cpuMs / wallMs * 100;
    private static double OverheadPercent(long payload, long wire) => wire <= 0 ? 0 : (wire - payload) * 100d / wire;
}

internal sealed record LatencyBenchmarkRun(int Repetition, int PayloadBytes, double AverageMilliseconds, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds, double MaximumMilliseconds);
internal sealed record BulkBenchmarkRun(int Repetition, string Direction, int PayloadBytes, double MegabytesPerSecond);
/// <summary>
/// The video pipeline as the benchmark saw it. Every stage of the chain is
/// recorded - driver publish, host receive, encoder send - because a single
/// send-FPS number cannot say whether a shortfall came from capture, from the
/// encoder or from the carrier, and that distinction is the whole point of
/// comparing two transports. Android receive/render FPS stay in the paired
/// Android log, as documented in docs/benchmarks/transport/schema.md.
/// </summary>
internal sealed record VideoBenchmarkSnapshot(
    string? Codec,
    string? Encoder,
    int? Width,
    int? Height,
    int? ConfiguredFps,
    int? NegotiatedFps,
    double? DriverCaptureFps,
    double? HostReceiveFps,
    double? SendFps,
    double? MegabytesPerSecond,
    double? EncodeLatencyMilliseconds,
    double? CaptureToSendMilliseconds,
    long? DropsOrSequenceGaps);
internal sealed record ProcessBenchmarkSnapshot(double CpuPercent, double CpuSeconds, double CpuP95Percent, long WorkingSetStartBytes, long WorkingSetEndBytes, long WorkingSetMaximumBytes);

/// <summary>
/// One measured workload window. CPU is reported as seconds as well as
/// core-equivalent percent: percent alone is not comparable between transports
/// that complete the same work in very different wall times.
/// </summary>
internal sealed record BenchmarkPhase(
    string Name,
    double DurationSeconds,
    double HostCpuSeconds,
    double HostCpuPercent,
    double AndroidCpuSeconds,
    double AndroidCpuPercent,
    long PayloadBytesTx,
    long PayloadBytesRx,
    double HostCpuMillisecondsPerMebibyte,
    double AndroidCpuMillisecondsPerMebibyte,
    double HostReadsPerSecond,
    double HostWritesPerSecond,
    double HostBytesPerRead,
    double HostBytesPerWrite,
    double HostWritesPerFrame,
    double HostReadsPerFrame,
    double HostFramesTxPerSecond,
    double HostFramesRxPerSecond,
    TransportIoProfileSnapshot HostIo,
    AndroidIoSnapshot AndroidIo,
    double AndroidReadsPerSecond,
    double AndroidWritesPerSecond,
    long HostAllocatedBytes,
    double HostAllocatedBytesPerSecond,
    int HostGen0Collections,
    int HostGen1Collections,
    int HostGen2Collections,
    double? VideoSendFpsStart,
    double? VideoSendFpsEnd,
    long? VideoSequenceGap);

internal readonly record struct AndroidStats(
    long WallMilliseconds,
    long CpuMilliseconds,
    long PssKilobytes,
    long JavaHeapBytes,
    long ReadCalls,
    long ReadBytes,
    long WriteCalls,
    long WriteBytes,
    long FramesTx,
    long FramesRx,
    long ShortReads,
    long PayloadAllocations,
    long ReaderCpuMilliseconds,
    long WriterCpuMilliseconds,
    long CoalescedFrames,
    long SplitFrames)
{
    /// <summary>Fields every responder version reports.</summary>
    internal const int BaseBytes = 32;
    private const int ExtendedFields = 16;

    internal static AndroidStats Parse(ReadOnlySpan<byte> payload)
    {
        // Older responders answer with the four base counters only; the
        // extended physical-I/O counters then read as zero rather than failing.
        Span<long> values = stackalloc long[ExtendedFields];
        var available = Math.Min(ExtendedFields, payload.Length / sizeof(long));
        for (var index = 0; index < available; index++)
            values[index] = BinaryPrimitives.ReadInt64LittleEndian(payload[(index * sizeof(long))..]);
        return new AndroidStats(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11],
            values[12], values[13], values[14], values[15]);
    }
}

/// <summary>Android physical I/O counters measured across one phase.</summary>
internal readonly record struct AndroidIoSnapshot(
    long ReadCalls,
    long ReadBytes,
    long WriteCalls,
    long WriteBytes,
    long FramesTx,
    long FramesRx,
    long ShortReads,
    long PayloadAllocations,
    long CoalescedFrames,
    long SplitFrames,
    double ReaderCpuSeconds,
    double WriterCpuSeconds)
{
    internal static AndroidIoSnapshot Between(AndroidStats start, AndroidStats end) => new(
        end.ReadCalls - start.ReadCalls,
        end.ReadBytes - start.ReadBytes,
        end.WriteCalls - start.WriteCalls,
        end.WriteBytes - start.WriteBytes,
        end.FramesTx - start.FramesTx,
        end.FramesRx - start.FramesRx,
        end.ShortReads - start.ShortReads,
        end.PayloadAllocations - start.PayloadAllocations,
        end.CoalescedFrames - start.CoalescedFrames,
        end.SplitFrames - start.SplitFrames,
        Math.Max(0, end.ReaderCpuMilliseconds - start.ReaderCpuMilliseconds) / 1000d,
        Math.Max(0, end.WriterCpuMilliseconds - start.WriterCpuMilliseconds) / 1000d);

    public double BytesPerRead => ReadCalls <= 0 ? 0 : (double)ReadBytes / ReadCalls;
    public double BytesPerWrite => WriteCalls <= 0 ? 0 : (double)WriteBytes / WriteCalls;
    public double ReadsPerFrame => FramesRx <= 0 ? 0 : (double)ReadCalls / FramesRx;
    public double WritesPerFrame => FramesTx <= 0 ? 0 : (double)WriteCalls / FramesTx;
}

internal sealed record AndroidProcessBenchmarkSnapshot(double CpuPercent, double CpuSeconds, long PssStartKilobytes, long PssEndKilobytes, long JavaHeapStartBytes, long JavaHeapEndBytes)
{
    internal static AndroidProcessBenchmarkSnapshot Between(AndroidStats start, AndroidStats end)
    {
        var wall = Math.Max(1, end.WallMilliseconds - start.WallMilliseconds);
        var cpuMilliseconds = end.CpuMilliseconds - start.CpuMilliseconds;
        return new AndroidProcessBenchmarkSnapshot(
            cpuMilliseconds * 100d / wall,
            cpuMilliseconds / 1000d,
            start.PssKilobytes,
            end.PssKilobytes,
            start.JavaHeapBytes,
            end.JavaHeapBytes);
    }
}
internal sealed record TransportBenchmarkMetrics(long PayloadBytesTx, long PayloadBytesRx, long WireBytesTx, long WireBytesRx, double FramingOverheadTxPercent, double FramingOverheadRxPercent, long VideoDrops, long Reconnects, long Errors, int VideoQueueDepth);
internal sealed record TransportBenchmarkResult(int SchemaVersion, string Transport, string Scenario, DateTimeOffset StartedAtUtc, double DurationSeconds, int WarmupSamples, int SamplesPerPayload, int Repetitions, IReadOnlyList<LatencyBenchmarkRun> Latency, IReadOnlyList<BulkBenchmarkRun> Bulk, IReadOnlyList<BenchmarkPhase> Phases, VideoBenchmarkSnapshot Video, ProcessBenchmarkSnapshot Windows, AndroidProcessBenchmarkSnapshot Android, TransportBenchmarkPrecondition? Precondition, TransportBenchmarkMetrics TransportMetrics);

internal sealed class ProcessSampleCollector : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private readonly Process _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<double> _cpu = [];
    private readonly Task _sampling;
    private long _workingSetMaximum;

    internal ProcessSampleCollector(Process process)
    {
        _process = process;
        _process.Refresh();
        _workingSetMaximum = process.WorkingSet64;
        _sampling = SampleAsync(_stop.Token);
    }

    internal double CpuP95Percent
    {
        get
        {
            var sorted = _cpu.Order().ToArray();
            return sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        }
    }
    internal long WorkingSetMaximumBytes => Interlocked.Read(ref _workingSetMaximum);

    internal async Task StopAsync()
    {
        _stop.Cancel();
        try { await _sampling.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        _process.Refresh();
        var previousCpu = _process.TotalProcessorTime;
        var previousWall = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            _process.Refresh();
            var now = Stopwatch.GetTimestamp();
            var cpu = _process.TotalProcessorTime;
            var wall = Stopwatch.GetElapsedTime(previousWall, now);
            _cpu.Add(wall.TotalMilliseconds <= 0
                ? 0
                : (cpu - previousCpu).TotalMilliseconds / wall.TotalMilliseconds * 100);
            previousCpu = cpu;
            previousWall = now;
            var workingSet = _process.WorkingSet64;
            long observed;
            do
            {
                observed = Interlocked.Read(ref _workingSetMaximum);
                if (workingSet <= observed) break;
            }
            while (Interlocked.CompareExchange(ref _workingSetMaximum, workingSet, observed) != observed);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
