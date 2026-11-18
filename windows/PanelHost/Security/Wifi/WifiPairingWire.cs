using System.Buffers.Binary;
using System.Text.Json;

namespace PanelHost.Security.Wifi;

// Only for the authenticated pairing stream; never dispatches PLMX or commands.
internal sealed class WifiPairingWire(Stream stream)
{
    private int _readMessages, _writtenMessages, _readBytes, _writtenBytes;
    private static void Check(ref int messages, ref int total, int length)
    {
        if (length is < 1 or > 16384 || messages >= 4 || length > 32768 - total)
            throw new InvalidDataException("pairing_failed");
        messages++; total += length;
    }
    private static JsonDocument Parse(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("pairing_failed");
            Validate(document.RootElement); return document;
        }
        catch { document.Dispose(); throw; }
        static void Validate(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in node.EnumerateObject())
                { if (!names.Add(property.Name)) throw new InvalidDataException("pairing_failed"); Validate(property.Value); }
            }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var element in node.EnumerateArray()) Validate(element);
        }
    }
    // The 5 s per-message deadline is the rule. Only the app, waiting for the operator's local approval, passes a
    // longer one for the single PairResult it expects.
    public async Task<JsonDocument> ReadAsync(CancellationToken cancellationToken, TimeSpan? deadlineOverride = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(deadlineOverride ?? TimeSpan.FromSeconds(5));
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, deadline.Token);
        var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (length > 16384) throw new InvalidDataException("pairing_failed");
        Check(ref _readMessages, ref _readBytes, (int)length);
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, deadline.Token);
        return Parse(bytes);
    }
    public async Task WriteAsync(byte[] json, CancellationToken cancellationToken)
    {
        Check(ref _writtenMessages, ref _writtenBytes, json.Length);
        using var parsed = Parse(json);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var prefix = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)json.Length);
        await stream.WriteAsync(prefix, deadline.Token); await stream.WriteAsync(json, deadline.Token);
    }
}
