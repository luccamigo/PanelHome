using System.Runtime.InteropServices;
using System.Text;
using PanelHost.Protocol;

namespace PanelHost.Services;

internal sealed record MediaArtworkPayload(string? ContentType, byte[] Bytes);

internal static class MediaArtworkRefresh
{
    public const ulong MaximumArtworkBytes = 1024 * 1024;

    public static async Task RunAsync(
        MediaRefreshCoordinator coordinator,
        MediaRefreshLease lease,
        string mediaId,
        Func<CancellationToken, Task<MediaArtworkPayload?>> readThumbnailAsync,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(readThumbnailAsync);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            var payload = await readThumbnailAsync(cancellationToken);
            if (!coordinator.IsCurrent(lease))
            {
                return;
            }

            if (payload is null)
            {
                coordinator.TryPublishArtwork(
                    lease,
                    MediaRefreshCoordinator.CreateUnavailableArtwork(mediaId));
                return;
            }

            var artwork = CreateMessage(mediaId, payload, out var rejectionReason);
            if (rejectionReason is not null)
            {
                MediaDiagnostics.WriteNotice(
                    log,
                    "Artwork refresh unavailable",
                    "thumbnail validation",
                    rejectionReason);
            }

            coordinator.TryPublishArtwork(lease, artwork);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MediaDiagnostics.WriteFailure(
                log,
                "Artwork refresh",
                "thumbnail retrieval",
                exception);
            coordinator.TryPublishArtwork(
                lease,
                MediaRefreshCoordinator.CreateUnavailableArtwork(mediaId));
        }
    }

    internal static MediaArtworkMessage CreateMessage(
        string mediaId,
        MediaArtworkPayload payload,
        out string? rejectionReason)
    {
        rejectionReason = null;
        if (payload.Bytes.Length == 0)
        {
            rejectionReason = "Thumbnail stream is empty.";
            return MediaRefreshCoordinator.CreateUnavailableArtwork(mediaId);
        }

        if ((ulong)payload.Bytes.Length > MaximumArtworkBytes)
        {
            rejectionReason =
                $"Thumbnail exceeds the {MaximumArtworkBytes}-byte limit.";
            return MediaRefreshCoordinator.CreateUnavailableArtwork(mediaId);
        }

        var mimeType = ResolveMimeType(payload.ContentType, payload.Bytes);
        if (mimeType is null)
        {
            rejectionReason = string.IsNullOrWhiteSpace(payload.ContentType)
                ? "Thumbnail MIME type is unavailable and its signature is unknown."
                : $"Thumbnail MIME type '{payload.ContentType}' is unsupported or does not match its data.";
            return MediaRefreshCoordinator.CreateUnavailableArtwork(mediaId);
        }

        return new MediaArtworkMessage
        {
            MediaId = mediaId,
            Available = true,
            MimeType = mimeType,
            DataBase64 = Convert.ToBase64String(payload.Bytes),
        };
    }

    private static string? ResolveMimeType(string? contentType, byte[] bytes)
    {
        var normalizedContentType = contentType?
            .Split(';', 2)[0]
            .Trim()
            .ToLowerInvariant() switch
        {
            "image/jpg" => "image/jpeg",
            "image/x-png" => "image/png",
            { Length: > 0 } value => value,
            _ => null,
        };
        var detectedContentType = DetectMimeType(bytes);

        return normalizedContentType switch
        {
            "image/jpeg" or "image/png" or "image/webp"
                when normalizedContentType == detectedContentType => normalizedContentType,
            null => detectedContentType,
            _ when detectedContentType is not null &&
                normalizedContentType is not "image/jpeg" and
                not "image/png" and
                not "image/webp" => detectedContentType,
            _ => null,
        };
    }

    private static string? DetectMimeType(byte[] bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xff &&
            bytes[1] == 0xd8 &&
            bytes[2] == 0xff)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8 &&
            bytes.AsSpan(0, 8).SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return "image/png";
        }

        if (bytes.Length >= 12 &&
            bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }
}

internal static class MediaDiagnostics
{
    public static void WriteFailure(
        TextWriter writer,
        string operation,
        string stage,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(exception);

        var output = new StringBuilder()
            .Append("[Media] ").Append(operation).AppendLine(" failed")
            .Append("Stage: ").AppendLine(stage)
            .Append("Type: ").AppendLine(exception.GetType().Name);
        if (exception is COMException comException)
        {
            output.Append("HRESULT: 0x")
                .Append(unchecked((uint)comException.HResult).ToString("X8"))
                .AppendLine();
        }

        output.Append("Message: ")
            .AppendLine(string.IsNullOrEmpty(exception.Message)
                ? "<empty>"
                : exception.Message);
#if DEBUG
        output.AppendLine("Stack trace:")
            .Append(exception.StackTrace ?? "<unavailable>");
#endif
        writer.WriteLine(output.ToString());
    }

    public static void WriteNotice(
        TextWriter writer,
        string operation,
        string stage,
        string message)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(
            $"[Media] {operation}{Environment.NewLine}" +
            $"Stage: {stage}{Environment.NewLine}" +
            $"Message: {message}");
    }
}
