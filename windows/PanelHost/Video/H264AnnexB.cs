using System.Buffers.Binary;

namespace PanelHost.Video;

internal static class H264AnnexB
{
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    internal static byte[] NormalizeAccessUnit(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new InvalidDataException("H.264 access unit is empty.");
        }

        if (StartsWithStartCode(payload))
        {
            return payload.ToArray();
        }

        using var output = new MemoryStream(payload.Length + 32);
        var offset = 0;
        while (offset < payload.Length)
        {
            if (payload.Length - offset < sizeof(uint))
            {
                throw new InvalidDataException("Truncated H.264 NAL length.");
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
            offset += sizeof(uint);
            if (length == 0 || length > payload.Length - offset)
            {
                throw new InvalidDataException("Invalid H.264 NAL length.");
            }

            output.Write(StartCode);
            output.Write(payload.Slice(offset, checked((int)length)));
            offset += checked((int)length);
        }

        return output.ToArray();
    }

    internal static byte[] NormalizeSequenceHeader(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            throw new InvalidDataException("H.264 sequence header is empty.");
        }

        if (StartsWithStartCode(header))
        {
            return header.ToArray();
        }

        if (header.Length < 7 || header[0] != 1)
        {
            return NormalizeAccessUnit(header);
        }

        using var output = new MemoryStream(header.Length + 32);
        var offset = 5;
        var sequenceParameterSetCount = header[offset++] & 0x1F;
        ReadParameterSets(header, ref offset, sequenceParameterSetCount, output);
        if (offset >= header.Length)
        {
            throw new InvalidDataException("H.264 AVC configuration has no PPS count.");
        }

        var pictureParameterSetCount = header[offset++];
        ReadParameterSets(header, ref offset, pictureParameterSetCount, output);
        if (output.Length == 0)
        {
            throw new InvalidDataException("H.264 AVC configuration has no parameter sets.");
        }

        return output.ToArray();
    }

    internal static bool ContainsIdr(ReadOnlySpan<byte> annexB)
    {
        var offset = 0;
        while (TryFindNal(annexB, ref offset, out var nalStart, out var nalLength))
        {
            if (nalLength > 0 && (annexB[nalStart] & 0x1F) == 5)
            {
                return true;
            }
        }

        return false;
    }

    internal static byte[]? ExtractParameterSets(ReadOnlySpan<byte> annexB)
    {
        using var output = new MemoryStream();
        var offset = 0;
        while (TryFindNal(annexB, ref offset, out var nalStart, out var nalLength))
        {
            if (nalLength == 0)
            {
                continue;
            }

            var type = annexB[nalStart] & 0x1F;
            if (type is not (7 or 8))
            {
                continue;
            }

            output.Write(StartCode);
            output.Write(annexB.Slice(nalStart, nalLength));
        }

        return output.Length == 0 ? null : output.ToArray();
    }

    private static void ReadParameterSets(
        ReadOnlySpan<byte> header,
        ref int offset,
        int count,
        MemoryStream output)
    {
        for (var index = 0; index < count; index++)
        {
            if (header.Length - offset < sizeof(ushort))
            {
                throw new InvalidDataException("Truncated H.264 parameter-set length.");
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(header[offset..]);
            offset += sizeof(ushort);
            if (length == 0 || length > header.Length - offset)
            {
                throw new InvalidDataException("Invalid H.264 parameter-set length.");
            }

            output.Write(StartCode);
            output.Write(header.Slice(offset, length));
            offset += length;
        }
    }

    private static bool StartsWithStartCode(ReadOnlySpan<byte> payload) =>
        payload.StartsWith(StartCode) ||
        (payload.Length >= 3 && payload[0] == 0 && payload[1] == 0 && payload[2] == 1);

    private static bool TryFindNal(
        ReadOnlySpan<byte> payload,
        ref int offset,
        out int nalStart,
        out int nalLength)
    {
        nalStart = 0;
        nalLength = 0;
        var startCode = FindStartCode(payload, offset, out var startCodeLength);
        if (startCode < 0)
        {
            return false;
        }

        nalStart = startCode + startCodeLength;
        var next = FindStartCode(payload, nalStart, out _);
        nalLength = (next < 0 ? payload.Length : next) - nalStart;
        offset = next < 0 ? payload.Length : next;
        return true;
    }

    private static int FindStartCode(
        ReadOnlySpan<byte> payload,
        int offset,
        out int length)
    {
        for (var index = offset; index + 2 < payload.Length; index++)
        {
            if (payload[index] != 0 || payload[index + 1] != 0)
            {
                continue;
            }

            if (payload[index + 2] == 1)
            {
                length = 3;
                return index;
            }

            if (index + 3 < payload.Length &&
                payload[index + 2] == 0 && payload[index + 3] == 1)
            {
                length = 4;
                return index;
            }
        }

        length = 0;
        return -1;
    }
}
