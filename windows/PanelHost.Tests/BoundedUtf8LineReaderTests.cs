using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.Protocol;

namespace PanelHost.Tests;

[TestClass]
public sealed class BoundedUtf8LineReaderTests
{
    [TestMethod]
    public async Task Normal_partial_and_multiple_lines_are_preserved()
    {
        await using var stream = new MemoryStream("one\ntwÃ³\nthree\n"u8.ToArray());
        var reader = new BoundedUtf8LineReader(stream, 16, bufferSize: 3);

        Assert.AreEqual("one", await reader.ReadLineAsync(default));
        Assert.AreEqual("twÃ³", await reader.ReadLineAsync(default));
        Assert.AreEqual("three", await reader.ReadLineAsync(default));
        Assert.IsNull(await reader.ReadLineAsync(default));
    }

    [TestMethod]
    public async Task Exact_byte_limit_including_multibyte_utf8_is_accepted()
    {
        var bytes = Encoding.UTF8.GetBytes("\u00e9\u00e9\n");
        await using var stream = new MemoryStream(bytes);
        var reader = new BoundedUtf8LineReader(stream, maximumBytes: 4, bufferSize: 1);

        Assert.AreEqual("\u00e9\u00e9", await reader.ReadLineAsync(default));
    }

    [TestMethod]
    public async Task Limit_plus_one_is_rejected_before_deserialization()
    {
        await using var stream = new MemoryStream("12345\n"u8.ToArray());
        var reader = new BoundedUtf8LineReader(stream, maximumBytes: 4, bufferSize: 2);

        await Assert.ThrowsExceptionAsync<JsonLineProtocolException>(async () =>
            await reader.ReadLineAsync(default));
    }

    [TestMethod]
    public async Task Oversized_line_without_newline_is_rejected()
    {
        await using var stream = new MemoryStream(new byte[33]);
        var reader = new BoundedUtf8LineReader(stream, maximumBytes: 32, bufferSize: 7);

        await Assert.ThrowsExceptionAsync<JsonLineProtocolException>(async () =>
            await reader.ReadLineAsync(default));
    }

    [TestMethod]
    public async Task Invalid_utf8_is_rejected()
    {
        await using var stream = new MemoryStream([0xc3, 0x28, 0x0a]);
        var reader = new BoundedUtf8LineReader(stream, maximumBytes: 8);

        await Assert.ThrowsExceptionAsync<JsonLineProtocolException>(async () =>
            await reader.ReadLineAsync(default));
    }

    [TestMethod]
    public async Task Disconnect_during_line_is_truncation()
    {
        await using var stream = new MemoryStream("partial"u8.ToArray());
        var reader = new BoundedUtf8LineReader(stream, maximumBytes: 32);

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(async () =>
            await reader.ReadLineAsync(default));
    }

    [TestMethod]
    public async Task Ten_thousand_small_messages_do_not_grow_the_reader()
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Repeat("{}\n", 10_000)));
        await using var stream = new MemoryStream(bytes);
        var reader = new BoundedUtf8LineReader(stream, maximumBytes: 16, bufferSize: 31);

        for (var index = 0; index < 10_000; index++)
        {
            Assert.AreEqual("{}", await reader.ReadLineAsync(default));
        }
        Assert.IsNull(await reader.ReadLineAsync(default));
    }
}
