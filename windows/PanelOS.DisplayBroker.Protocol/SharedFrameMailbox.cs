using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using PanelOS.DriverInterop;

namespace PanelOS.DisplayBroker.Protocol;

/// <summary>
/// A bounded, single-slot shared-memory mailbox. The broker overwrites the
/// previous frame; readers either observe one complete generation or retry.
/// There is no queue and therefore no backlog-induced latency.
/// </summary>
public sealed unsafe class SharedFrameMailbox : IDisposable
{
    private const uint Magic = 0x314D4650; // PFM1
    private const uint LayoutVersion = 1;
    private const int HeaderBytes = 128;
    private const int SeqlockOffset = 16;
    private const int PayloadBytesOffset = 20;
    private const int WidthOffset = 24;
    private const int HeightOffset = 28;
    private const int StrideOffset = 32;
    private const int FormatOffset = 36;
    private const int GenerationOffset = 40;
    private const int SequenceOffset = 48;
    private const int CaptureTimestampQpcOffset = 56;
    private const int QpcFrequencyOffset = 64;
    private const int PublishedOffset = 72;
    private const int OverwrittenOffset = 80;
    private const int AcknowledgedGenerationOffset = 88;
    private const int AcknowledgedSequenceOffset = 96;
    private const int ValidOffset = 104;

    public const int PayloadCapacity =
        (int)PanelDisplayDriverContract.MaximumFramePayloadBytes;
    public const long MappingCapacity = HeaderBytes + (long)PayloadCapacity;

    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly object _writerLock = new();
    private byte* _pointer;
    private int _disposed;

    private SharedFrameMailbox(MemoryMappedFile mapping, bool initialize)
    {
        _mapping = mapping;
        _view = mapping.CreateViewAccessor(
            0,
            MappingCapacity,
            MemoryMappedFileAccess.ReadWrite);
        byte* acquired = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref acquired);
        _pointer = acquired + _view.PointerOffset;

        if (initialize)
        {
            new Span<byte>(_pointer, HeaderBytes).Clear();
            WriteUInt32(0, Magic);
            WriteUInt32(4, LayoutVersion);
            WriteUInt32(8, PayloadCapacity);
            WriteUInt32(12, HeaderBytes);
        }
        else
        {
            ValidateLayout();
        }
    }

    public ulong PublishedFrames => ReadUInt64Stable(PublishedOffset);

    public ulong OverwrittenFrames => ReadUInt64Stable(OverwrittenOffset);

    public static SharedFrameMailbox CreateNew(string name)
    {
        ValidateName(name);
        return new SharedFrameMailbox(
            MemoryMappedFile.CreateNew(
                name,
                MappingCapacity,
                MemoryMappedFileAccess.ReadWrite),
            initialize: true);
    }

    public static SharedFrameMailbox OpenExisting(string name)
    {
        ValidateName(name);
        return new SharedFrameMailbox(
            MemoryMappedFile.OpenExisting(
                name,
                MemoryMappedFileRights.ReadWrite),
            initialize: false);
    }

    public void Publish(PanelDisplayDriverFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var info = frame.Info;
        if (info.Result != PanelDisplayFrameResult.Ok ||
            info.Generation == 0 || info.Sequence == 0 ||
            info.PayloadBytes != frame.Pixels.Length ||
            frame.Pixels.Length > PayloadCapacity)
        {
            throw new InvalidDataException("Cannot publish an invalid driver frame.");
        }

        lock (_writerLock)
        {
            BeginWrite();
            try
            {
                var previousGeneration = ReadUInt64(GenerationOffset);
                var previousSequence = ReadUInt64(SequenceOffset);
                var published = ReadUInt64(PublishedOffset);
                var overwritten = ReadUInt64(OverwrittenOffset);
                var acknowledgedGeneration = ReadUInt64Volatile(
                    AcknowledgedGenerationOffset);
                var acknowledgedSequence = ReadUInt64Volatile(
                    AcknowledgedSequenceOffset);
                if (ReadUInt32(ValidOffset) != 0 &&
                    (previousGeneration != acknowledgedGeneration ||
                        previousSequence != acknowledgedSequence))
                {
                    overwritten++;
                }

                frame.Pixels.AsSpan().CopyTo(
                    new Span<byte>(_pointer + HeaderBytes, frame.Pixels.Length));
                WriteUInt32(PayloadBytesOffset, info.PayloadBytes);
                WriteUInt32(WidthOffset, info.Width);
                WriteUInt32(HeightOffset, info.Height);
                WriteUInt32(StrideOffset, info.Stride);
                WriteUInt32(FormatOffset, (uint)info.Format);
                WriteUInt64(GenerationOffset, info.Generation);
                WriteUInt64(SequenceOffset, info.Sequence);
                WriteUInt64(CaptureTimestampQpcOffset, info.CaptureTimestampQpc);
                WriteUInt64(QpcFrequencyOffset, info.QpcFrequency);
                WriteUInt64(PublishedOffset, published + 1);
                WriteUInt64(OverwrittenOffset, overwritten);
                WriteUInt32(ValidOffset, 1);
            }
            finally
            {
                EndWrite();
            }
        }
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_writerLock)
        {
            BeginWrite();
            try
            {
                WriteUInt32(ValidOffset, 0);
                WriteUInt32(PayloadBytesOffset, 0);
                WriteUInt64(GenerationOffset, 0);
                WriteUInt64(SequenceOffset, 0);
            }
            finally
            {
                EndWrite();
            }
        }
    }

    public PanelDisplayFrameInfo GetFrameInfo(ulong lastSequence)
    {
        var snapshot = ReadSnapshot(copyPixels: false);
        if (snapshot is null)
        {
            return EmptyInfo(PanelDisplayFrameResult.NoFrame);
        }

        return snapshot.Value.Info with
        {
            Result = snapshot.Value.Info.Sequence == lastSequence
                ? PanelDisplayFrameResult.NoNewFrame
                : PanelDisplayFrameResult.Ok,
        };
    }

    public PanelDisplayDriverFrame ReadFrame(PanelDisplayFrameInfo requested)
    {
        if (requested.Result != PanelDisplayFrameResult.Ok)
        {
            throw new ArgumentException(
                "Only an available frame can be read.",
                nameof(requested));
        }

        var snapshot = ReadSnapshot(copyPixels: true);
        if (snapshot is null)
        {
            return new PanelDisplayDriverFrame(
                EmptyInfo(PanelDisplayFrameResult.NoFrame),
                []);
        }

        var value = snapshot.Value;
        if (value.Info.Generation != requested.Generation ||
            value.Info.Sequence != requested.Sequence)
        {
            return new PanelDisplayDriverFrame(
                value.Info with { Result = PanelDisplayFrameResult.FrameReplaced },
                []);
        }

        WriteUInt64Volatile(AcknowledgedGenerationOffset, value.Info.Generation);
        WriteUInt64Volatile(AcknowledgedSequenceOffset, value.Info.Sequence);
        return new PanelDisplayDriverFrame(value.Info, value.Pixels!);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _pointer = null;
        _view.Dispose();
        _mapping.Dispose();
    }

    private MailboxSnapshot? ReadSnapshot(bool copyPixels)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var before = ReadInt32Volatile(SeqlockOffset);
            if ((before & 1) != 0)
            {
                Thread.SpinWait(32 << Math.Min(attempt, 8));
                continue;
            }

            if (ReadUInt32(ValidOffset) == 0)
            {
                Thread.MemoryBarrier();
                if (before == ReadInt32Volatile(SeqlockOffset))
                {
                    return null;
                }
                continue;
            }

            var payloadBytes = ReadUInt32(PayloadBytesOffset);
            var info = new PanelDisplayFrameInfo(
                PanelDisplayFrameResult.Ok,
                ReadUInt64(GenerationOffset),
                ReadUInt64(SequenceOffset),
                ReadUInt64(CaptureTimestampQpcOffset),
                ReadUInt64(QpcFrequencyOffset),
                ReadUInt32(WidthOffset),
                ReadUInt32(HeightOffset),
                ReadUInt32(StrideOffset),
                (PanelDisplayFrameFormat)ReadUInt32(FormatOffset),
                payloadBytes);
            if (!IsValid(info))
            {
                throw new InvalidDataException(
                    "Shared frame mailbox contains invalid metadata.");
            }

            byte[]? pixels = null;
            if (copyPixels)
            {
                pixels = new byte[checked((int)payloadBytes)];
                new ReadOnlySpan<byte>(
                    _pointer + HeaderBytes,
                    pixels.Length).CopyTo(pixels);
            }

            Thread.MemoryBarrier();
            var after = ReadInt32Volatile(SeqlockOffset);
            if (before == after && (after & 1) == 0)
            {
                return new MailboxSnapshot(info, pixels);
            }
        }

        return null;
    }

    private void ValidateLayout()
    {
        if (ReadUInt32(0) != Magic ||
            ReadUInt32(4) != LayoutVersion ||
            ReadUInt32(8) != PayloadCapacity ||
            ReadUInt32(12) != HeaderBytes)
        {
            throw new InvalidDataException("Unsupported shared frame mailbox layout.");
        }
    }

    private static bool IsValid(PanelDisplayFrameInfo info) =>
        info.Generation != 0 && info.Sequence != 0 &&
        info.Width is > 0 and <= 16_384 &&
        info.Height is > 0 and <= 16_384 &&
        info.Stride >= info.Width * 4 &&
        info.PayloadBytes == (ulong)info.Stride * info.Height &&
        info.PayloadBytes <= PayloadCapacity &&
        info.QpcFrequency != 0 &&
        info.Format is PanelDisplayFrameFormat.Bgra8888 or
            PanelDisplayFrameFormat.Rgba8888;

    private static PanelDisplayFrameInfo EmptyInfo(PanelDisplayFrameResult result) =>
        new(result, 0, 0, 0, 0, 0, 0, 0, PanelDisplayFrameFormat.Unknown, 0);

    private void BeginWrite()
    {
        ref var value = ref Unsafe.AsRef<int>(_pointer + SeqlockOffset);
        var next = Interlocked.Increment(ref value);
        if ((next & 1) == 0)
        {
            throw new InvalidOperationException("Shared mailbox has multiple writers.");
        }
        Thread.MemoryBarrier();
    }

    private void EndWrite()
    {
        Thread.MemoryBarrier();
        ref var value = ref Unsafe.AsRef<int>(_pointer + SeqlockOffset);
        _ = Interlocked.Increment(ref value);
    }

    private int ReadInt32Volatile(int offset) =>
        Volatile.Read(ref Unsafe.AsRef<int>(_pointer + offset));

    private uint ReadUInt32(int offset) => Unsafe.ReadUnaligned<uint>(_pointer + offset);
    private ulong ReadUInt64(int offset) => Unsafe.ReadUnaligned<ulong>(_pointer + offset);
    private ulong ReadUInt64Stable(int offset)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var before = ReadInt32Volatile(SeqlockOffset);
            var value = ReadUInt64(offset);
            Thread.MemoryBarrier();
            if ((before & 1) == 0 && before == ReadInt32Volatile(SeqlockOffset))
            {
                return value;
            }
        }
        return 0;
    }
    private ulong ReadUInt64Volatile(int offset) =>
        (ulong)Volatile.Read(ref Unsafe.AsRef<long>(_pointer + offset));
    private void WriteUInt32(int offset, uint value) =>
        Unsafe.WriteUnaligned(_pointer + offset, value);
    private void WriteUInt64(int offset, ulong value) =>
        Unsafe.WriteUnaligned(_pointer + offset, value);
    private void WriteUInt64Volatile(int offset, ulong value) =>
        Volatile.Write(ref Unsafe.AsRef<long>(_pointer + offset), (long)value);

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 240 || name.Contains('\\') || name.Contains('/'))
        {
            throw new ArgumentException("Invalid local mapping name.", nameof(name));
        }
    }

    private readonly record struct MailboxSnapshot(
        PanelDisplayFrameInfo Info,
        byte[]? Pixels);
}
