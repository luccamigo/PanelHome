using System.Globalization;
using System.Text;

namespace PanelHost.Services;

internal sealed class LocalHttpError(int status, string reason) : Exception(reason)
{
    internal int Status { get; } = status;
}
internal sealed record LocalHttpRequest(string Path, string? Key, byte[] Body)
{
    internal static async Task<LocalHttpRequest> ReadAsync(Stream stream, int port, CancellationToken token)
    {
        var headers = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (headers.Count == LocalIntegrationLimits.HeaderBytes) throw new LocalHttpError(431, "headers_too_large");
            if (await stream.ReadAsync(one, token) == 0) throw new EndOfStreamException();
            if (one[0] > 127 || one[0] == 0) throw new LocalHttpError(400, "invalid_headers");
            headers.Add(one[0]);
            var n = headers.Count;
            if (n >= 4 && headers[n - 4] == 13 && headers[n - 3] == 10 && headers[n - 2] == 13 && headers[n - 1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[2] != "HTTP/1.1") throw new LocalHttpError(400, "invalid_request");
        if (request[0] != "POST") throw new LocalHttpError(405, "method_not_allowed");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(x => x.Length > 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new LocalHttpError(400, "invalid_headers");
            if (!fields.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new LocalHttpError(400, "duplicate_header");
        }
        if (!fields.TryGetValue("Host", out var host) || host != $"127.0.0.1:{port}" && host != $"localhost:{port}")
            throw new LocalHttpError(421, "invalid_host");
        if (fields.Keys.Any(x => x.Equals("Origin", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase)))
            throw new LocalHttpError(403, "browser_forbidden");
        if (request[1].Contains('?') || !request[1].StartsWith("/v1/", StringComparison.Ordinal)) throw new LocalHttpError(400, "invalid_path");
        if (fields.ContainsKey("Transfer-Encoding")) throw new LocalHttpError(400, "transfer_encoding_forbidden");
        if (!fields.TryGetValue("Content-Type", out var contentType) || contentType != "application/json") throw new LocalHttpError(415, "json_required");
        if (!fields.TryGetValue("Content-Length", out var lengthText)) throw new LocalHttpError(411, "length_required");
        if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0) throw new LocalHttpError(400, "invalid_length");
        if (length > LocalIntegrationLimits.BodyBytes) throw new LocalHttpError(413, "body_too_large");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        fields.TryGetValue("Authorization", out var authorization);
        return new(request[1], authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? authorization[7..] : null, body);
    }
}
