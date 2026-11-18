using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PanelHost.Transport;

/// <summary>
/// Development-carrier bootstrap. This framing is consumed before a connection
/// is exposed to Panel Protocol, video, or benchmark code.
/// </summary>
internal static class AdbTcpAuthentication
{
    internal const string EnvironmentVariable = "PANELOS_ADB_AUTH_TOKEN";
    internal const int TokenBytes = 32;
    internal const int HeaderBytes = 8;
    internal const int AckBytes = 8;
    internal const int MaximumCredentialBytes = 64;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private static ReadOnlySpan<byte> RequestMagic => "PADA"u8;
    private static ReadOnlySpan<byte> AckMagic => "PADO"u8;

    internal static byte[] ReadTokenFromEnvironment()
    {
        var encoded = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new ArgumentException(
                $"ADB/TCP requires a development-session token in {EnvironmentVariable}. " +
                "Start it through the development launcher or Control Center.");
        }

        try
        {
            var token = Convert.FromHexString(encoded);
            if (token.Length != TokenBytes) throw new FormatException();
            return token;
        }
        catch (FormatException)
        {
            throw new ArgumentException(
                $"{EnvironmentVariable} must contain a 256-bit development-session token.");
        }
    }

    internal static async ValueTask<bool> AuthenticateServerAsync(
        Stream stream,
        PanelChannelId expectedChannel,
        ReadOnlyMemory<byte> expectedToken,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var header = new byte[HeaderBytes];
        if (!await TryReadExactlyAsync(stream, header, timeout.Token).ConfigureAwait(false)) return false;

        var credentialLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2));
        if (!header.AsSpan(0, 4).SequenceEqual(RequestMagic) ||
            header[4] != 1 ||
            header[5] != (byte)expectedChannel ||
            credentialLength > MaximumCredentialBytes)
        {
            return false;
        }

        var supplied = new byte[credentialLength];
        if (!await TryReadExactlyAsync(stream, supplied, timeout.Token).ConfigureAwait(false)) return false;
        if (supplied.Length != expectedToken.Length ||
            !CryptographicOperations.FixedTimeEquals(supplied, expectedToken.Span))
        {
            CryptographicOperations.ZeroMemory(supplied);
            return false;
        }
        CryptographicOperations.ZeroMemory(supplied);

        var ack = new byte[AckBytes];
        AckMagic.CopyTo(ack);
        ack[4] = 1;
        ack[5] = (byte)expectedChannel;
        await stream.WriteAsync(ack, timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        return true;
    }

    internal static byte[] EncodeRequest(PanelChannelId channel, ReadOnlySpan<byte> token)
    {
        if (token.Length > MaximumCredentialBytes) throw new ArgumentOutOfRangeException(nameof(token));
        var message = new byte[HeaderBytes + token.Length];
        RequestMagic.CopyTo(message);
        message[4] = 1;
        message[5] = (byte)channel;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6, 2), checked((ushort)token.Length));
        token.CopyTo(message.AsSpan(HeaderBytes));
        return message;
    }

    internal static bool IsValidAck(ReadOnlySpan<byte> ack, PanelChannelId channel) =>
        ack.Length == AckBytes &&
        ack[..4].SequenceEqual(AckMagic) &&
        ack[4] == 1 &&
        ack[5] == (byte)channel &&
        ack[6] == 0 &&
        ack[7] == 0;

    private static async ValueTask<bool> TryReadExactlyAsync(
        Stream stream,
        Memory<byte> target,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        try
        {
            while (offset < target.Length)
            {
                var read = await stream.ReadAsync(target[offset..], cancellationToken).ConfigureAwait(false);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
