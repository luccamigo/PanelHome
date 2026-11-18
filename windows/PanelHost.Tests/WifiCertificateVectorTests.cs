using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;

namespace PanelHost.Tests;

[TestClass]
public sealed class WifiCertificateVectorTests
{
    [TestMethod]
    public void Dotnet_and_android_use_the_same_public_certificate_negatives()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "protocol-fixtures/wifi/certificates-v1.json"))) directory = directory.Parent;
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory!.FullName, "protocol-fixtures/wifi/certificates-v1.json")));
        var data = document.RootElement;
        byte[] Bytes(string field) => Convert.FromBase64String(data.GetProperty(field).GetString()!);
        X509Certificate2 Certificate(string field) => new(Bytes(field));
        var now = DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("asOfUnixMillis").GetInt64()).UtcDateTime;
        using var root = Certificate("root"); var pin = Bytes("hostId");
        Assert.IsTrue(WifiCertificates.ValidateRoot(root, pin, now));
        foreach (var field in new[] { "rootB", "rootBadSignature", "rootNonCritical", "rootWrongUsage", "rootTooLarge" })
        { using var invalid = Certificate(field); Assert.IsFalse(WifiCertificates.ValidateRoot(invalid, pin, now), field); }
        using var server = Certificate("server");
        Assert.IsTrue(WifiCertificates.Validate(server, root, pin, true, now));
        foreach (var field in new[] { "mitmSameSan", "wrongSan", "client", "missingBasicConstraints", "extraKeyUsage", "missingKeyUsage", "expired", "future", "serverBadSignature" })
        {
            using var invalid = Certificate(field);
            if (field == "mitmSameSan") Assert.IsTrue(invalid.MatchesHostname(WifiCertificates.ServerName(root), false, false));
            Assert.IsFalse(WifiCertificates.Validate(invalid, root, pin, true, now), field);
        }
        using var otherRoot = Certificate("rootB");
        using var offer = new WifiPairingOffer { HostId = pin, RootDer = otherRoot.RawData, PairId = new byte[16], Token = new byte[32],
            Ip = "192.168.1.2", PairPort = 40001, SessionPort = 40002, ExpiresAt = 1780272120 };
        Assert.ThrowsException<InvalidDataException>(() => WifiPairingOffer.Decode(offer.Encode(), now));
    }
}
