using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.Services;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class QrEncoderTests
{
    // Same shape and size as the real PanelOSPair/1 document (about 790 bytes), with synthetic content and no secret.
    private static string SyntheticOffer()
    {
        static string Field(char c, int length) => new string(Enumerable.Range(0, length).Select(i => (char)(c + i % 20)).ToArray());
        return "{\"type\":\"PanelOSPair/1\",\"version\":1,\"hostId\":\"" + Field('A', 43) + "\",\"rootCa\":\"" + Field('a', 520) +
               "\",\"ip\":\"192.168.15.4\",\"pairPort\":40001,\"sessionPort\":40002,\"pairId\":\"" + Field('B', 22) +
               "\",\"token\":\"" + Field('C', 43) + "\",\"expiresAt\":1794000000}";
    }

    private static string FixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "protocol-fixtures", "wifi"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "protocol-fixtures", "wifi", "qr-v1.json");
    }

    [TestMethod]
    public void The_matrix_is_square_has_no_quiet_zone_and_starts_with_the_finder_pattern()
    {
        var rows = new QrCoderEncoder().Encode(SyntheticOffer());
        Assert.IsTrue(rows.Count is >= 21 and <= 177 && (rows.Count - 17) % 4 == 0, $"a valid QR size, got {rows.Count}");
        Assert.IsTrue(rows.All(row => row.Length == rows.Count), "square");
        foreach (var (x, y) in new[] { (0, 0), (rows.Count - 7, 0), (0, rows.Count - 7) })
        {
            for (var i = 0; i < 7; i++)
            {
                Assert.AreEqual('1', rows[y][x + i], "finder top edge"); Assert.AreEqual('1', rows[y + 6][x + i], "finder bottom edge");
                Assert.AreEqual('1', rows[y + i][x], "finder left edge"); Assert.AreEqual('1', rows[y + i][x + 6], "finder right edge");
            }
            Assert.AreEqual('0', rows[y + 1][x + 1], "light ring"); Assert.AreEqual('1', rows[y + 3][x + 3], "dark centre");
        }
    }

    [TestMethod]
    public void The_real_size_of_a_pairing_document_stays_within_a_scannable_version()
    {
        var text = SyntheticOffer();
        Assert.IsTrue(Encoding.UTF8.GetByteCount(text) is > 700 and <= 1024, "same order as the real document, under the 1 KiB contract limit");
        var modules = new QrCoderEncoder().Encode(text).Count;
        // Version 25 is 117 modules; beyond that a tablet camera at 30 cm gets noticeably worse. Keep it visible if it ever grows.
        Assert.IsTrue(modules <= 117, $"{modules} modules");
    }

    [TestMethod]
    public void Encoding_is_deterministic_and_the_shared_fixture_is_current()
    {
        var text = SyntheticOffer(); var rows = new QrCoderEncoder().Encode(text);
        CollectionAssert.AreEqual(rows.ToArray(), new QrCoderEncoder().Encode(text).ToArray());
        var path = FixturePath();
        if (Environment.GetEnvironmentVariable("PANELOS_WRITE_QR_FIXTURE") == "1")
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                description = "Synthetic PanelOSPair/1-sized document encoded by the Control Center (QRCoder, byte mode, ECC M, no quiet zone). The Android suite decodes rows with ZXing and must get text back.",
                text, rows,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.AreEqual(text, fixture.RootElement.GetProperty("text").GetString());
        CollectionAssert.AreEqual(rows.ToArray(), fixture.RootElement.GetProperty("rows").EnumerateArray().Select(e => e.GetString()!).ToArray(),
            "Regenerate with PANELOS_WRITE_QR_FIXTURE=1 if QRCoder or the settings changed on purpose.");
    }

    [TestMethod]
    public void Empty_text_is_refused()
    {
        Assert.ThrowsException<ArgumentException>(() => new QrCoderEncoder().Encode(""));
    }
}