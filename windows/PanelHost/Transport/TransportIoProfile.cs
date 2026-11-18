using System.Diagnostics;

namespace PanelHost.Transport;

/// <summary>
/// Physical-layer counters for the native carrier. They exist so transport CPU
/// can be attributed to concrete I/O behaviour - call rate, transfer size and
/// framing - instead of being guessed from a process-wide percentage.
/// Every counter is a plain interlocked add on the I/O threads; the cost is a
/// few nanoseconds per operation and is dwarfed by the syscall it describes.
/// </summary>
internal sealed class TransportIoProfile
{
    private long _readCalls;
    private long _readBytes;
    private long _readZeroLength;
    private long _readBlockedTicks;
    private long _readsCompletedInline;
    private long _bufferedReadHits;
    private long _stagedCopyBytes;

    private long _writeCalls;
    private long _writeBytes;
    private long _writeBlockedTicks;
    private long _writesCompletedInline;

    private long _framesTx;
    private long _framesRx;
    private long _frameWritesCoalesced;
    private long _frameWritesSplit;
    private long _fragmentsTx;
    private long _payloadRentals;
    private long _payloadReturns;

    public long ReadCalls => Interlocked.Read(ref _readCalls);
    public long ReadBytes => Interlocked.Read(ref _readBytes);
    public long ReadZeroLength => Interlocked.Read(ref _readZeroLength);
    public long ReadsCompletedInline => Interlocked.Read(ref _readsCompletedInline);
    public long BufferedReadHits => Interlocked.Read(ref _bufferedReadHits);
    public long StagedCopyBytes => Interlocked.Read(ref _stagedCopyBytes);
    public long WriteCalls => Interlocked.Read(ref _writeCalls);
    public long WriteBytes => Interlocked.Read(ref _writeBytes);
    public long WritesCompletedInline => Interlocked.Read(ref _writesCompletedInline);
    public long FramesTx => Interlocked.Read(ref _framesTx);
    public long FramesRx => Interlocked.Read(ref _framesRx);
    public long FrameWritesCoalesced => Interlocked.Read(ref _frameWritesCoalesced);
    public long FrameWritesSplit => Interlocked.Read(ref _frameWritesSplit);
    public long FragmentsTx => Interlocked.Read(ref _fragmentsTx);
    public long PayloadRentals => Interlocked.Read(ref _payloadRentals);
    public long PayloadReturns => Interlocked.Read(ref _payloadReturns);
    public TimeSpan ReadBlocked => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _readBlockedTicks));
    public TimeSpan WriteBlocked => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _writeBlockedTicks));

    internal void PipeRead(int bytes, long startedTimestamp, bool completedInline)
    {
        Interlocked.Increment(ref _readCalls);
        Interlocked.Add(ref _readBytes, bytes);
        Interlocked.Add(ref _readBlockedTicks, Stopwatch.GetTimestamp() - startedTimestamp);
        if (bytes == 0) Interlocked.Increment(ref _readZeroLength);
        if (completedInline) Interlocked.Increment(ref _readsCompletedInline);
    }

    internal void PipeWrite(int bytes, long startedTimestamp, bool completedInline)
    {
        Interlocked.Increment(ref _writeCalls);
        Interlocked.Add(ref _writeBytes, bytes);
        Interlocked.Add(ref _writeBlockedTicks, Stopwatch.GetTimestamp() - startedTimestamp);
        if (completedInline) Interlocked.Increment(ref _writesCompletedInline);
    }

    internal void BufferedReadHit(int bytes)
    {
        Interlocked.Increment(ref _bufferedReadHits);
        Interlocked.Add(ref _stagedCopyBytes, bytes);
    }

    internal void FrameSent(bool coalesced)
    {
        Interlocked.Increment(ref _framesTx);
        if (coalesced) Interlocked.Increment(ref _frameWritesCoalesced);
        else Interlocked.Increment(ref _frameWritesSplit);
    }

    internal void FrameReceived() => Interlocked.Increment(ref _framesRx);
    internal void FragmentSent() => Interlocked.Increment(ref _fragmentsTx);
    internal void PayloadRented() => Interlocked.Increment(ref _payloadRentals);
    internal void PayloadReturned() => Interlocked.Increment(ref _payloadReturns);

    internal TransportIoProfileSnapshot Snapshot() => new(
        ReadCalls,
        ReadBytes,
        ReadZeroLength,
        ReadsCompletedInline,
        BufferedReadHits,
        StagedCopyBytes,
        ReadBlocked.TotalMilliseconds,
        WriteCalls,
        WriteBytes,
        WritesCompletedInline,
        WriteBlocked.TotalMilliseconds,
        FramesTx,
        FramesRx,
        FrameWritesCoalesced,
        FrameWritesSplit,
        FragmentsTx,
        PayloadRentals,
        PayloadReturns);
}

internal readonly record struct TransportIoProfileSnapshot(
    long ReadCalls,
    long ReadBytes,
    long ReadZeroLength,
    long ReadsCompletedInline,
    long BufferedReadHits,
    long StagedCopyBytes,
    double ReadBlockedMilliseconds,
    long WriteCalls,
    long WriteBytes,
    long WritesCompletedInline,
    double WriteBlockedMilliseconds,
    long FramesTx,
    long FramesRx,
    long FrameWritesCoalesced,
    long FrameWritesSplit,
    long FragmentsTx,
    long PayloadRentals,
    long PayloadReturns)
{
    public static TransportIoProfileSnapshot operator -(
        TransportIoProfileSnapshot end,
        TransportIoProfileSnapshot start) => new(
            end.ReadCalls - start.ReadCalls,
            end.ReadBytes - start.ReadBytes,
            end.ReadZeroLength - start.ReadZeroLength,
            end.ReadsCompletedInline - start.ReadsCompletedInline,
            end.BufferedReadHits - start.BufferedReadHits,
            end.StagedCopyBytes - start.StagedCopyBytes,
            end.ReadBlockedMilliseconds - start.ReadBlockedMilliseconds,
            end.WriteCalls - start.WriteCalls,
            end.WriteBytes - start.WriteBytes,
            end.WritesCompletedInline - start.WritesCompletedInline,
            end.WriteBlockedMilliseconds - start.WriteBlockedMilliseconds,
            end.FramesTx - start.FramesTx,
            end.FramesRx - start.FramesRx,
            end.FrameWritesCoalesced - start.FrameWritesCoalesced,
            end.FrameWritesSplit - start.FrameWritesSplit,
            end.FragmentsTx - start.FragmentsTx,
            end.PayloadRentals - start.PayloadRentals,
            end.PayloadReturns - start.PayloadReturns);

    /// <summary>Average bytes moved per physical read call.</summary>
    public double BytesPerRead => ReadCalls <= 0 ? 0 : (double)ReadBytes / ReadCalls;

    /// <summary>Average bytes moved per physical write call.</summary>
    public double BytesPerWrite => WriteCalls <= 0 ? 0 : (double)WriteBytes / WriteCalls;

    public double ReadsPerSecond(double seconds) => seconds <= 0 ? 0 : ReadCalls / seconds;
    public double WritesPerSecond(double seconds) => seconds <= 0 ? 0 : WriteCalls / seconds;
    public double FramesTxPerSecond(double seconds) => seconds <= 0 ? 0 : FramesTx / seconds;
    public double FramesRxPerSecond(double seconds) => seconds <= 0 ? 0 : FramesRx / seconds;

    /// <summary>Physical calls per logical frame; 1.0 means perfect coalescing.</summary>
    public double WritesPerFrame => FramesTx <= 0 ? 0 : (double)WriteCalls / FramesTx;
    public double ReadsPerFrame => FramesRx <= 0 ? 0 : (double)ReadCalls / FramesRx;
}

/// <summary>
/// Managed-heap counters. Allocation rate is the other half of the CPU story on
/// the Windows side, so it is captured next to the I/O counters.
/// </summary>
internal readonly record struct ManagedAllocationSnapshot(
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections)
{
    internal static ManagedAllocationSnapshot Capture() => new(
        GC.GetTotalAllocatedBytes(precise: false),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2));

    public static ManagedAllocationSnapshot operator -(
        ManagedAllocationSnapshot end,
        ManagedAllocationSnapshot start) => new(
            end.AllocatedBytes - start.AllocatedBytes,
            end.Gen0Collections - start.Gen0Collections,
            end.Gen1Collections - start.Gen1Collections,
            end.Gen2Collections - start.Gen2Collections);
}
