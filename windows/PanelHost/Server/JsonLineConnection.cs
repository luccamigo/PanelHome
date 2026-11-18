using System.Text;
using PanelHost.Protocol;
using PanelOS.Windows.Protocol;

namespace PanelHost.Server;

internal sealed class JsonLineConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly BoundedUtf8LineReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonLineConnection(Stream stream)
    {
        _stream = stream;
        _reader = new BoundedUtf8LineReader(
            _stream,
            PanelProtocol.MaxControlMessageBytes);
        _writer = new StreamWriter(
            _stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true);
    }

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        _reader.ReadLineAsync(cancellationToken);

    public async Task WriteAsync<T>(T message, CancellationToken cancellationToken) where T : class
    {
        var line = PanelJson.SerializeLine(message);
        if (Encoding.UTF8.GetByteCount(line.AsSpan(0, line.Length - 1)) >
            PanelProtocol.MaxControlMessageBytes)
        {
            throw new JsonLineProtocolException(
                "Outgoing JSONL message exceeds the protocol byte limit.");
        }
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteAsync(line.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _writer.DisposeAsync();
        }
        finally
        {
            await _stream.DisposeAsync();
            _writeLock.Dispose();
        }
    }
}
