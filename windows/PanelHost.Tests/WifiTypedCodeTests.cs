using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;

namespace PanelHost.Tests;

[TestClass]
public sealed class WifiTypedCodeTests
{
    private static System.Text.Json.JsonDocument Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "protocol-fixtures", "wifi", "typed-code-v1.json"))) directory = directory.Parent;
        return System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory!.FullName, "protocol-fixtures", "wifi", "typed-code-v1.json")));
    }

    private static WifiPairingOffer OfferFor(byte[] hostId, byte[] pairId, string ip, int pairPort, int sessionPort) =>
        new() { HostId = hostId, RootDer = [], PairId = pairId, Token = new byte[32], Ip = ip, PairPort = pairPort, SessionPort = sessionPort, ExpiresAt = 0 };

    [TestMethod]
    public void Shared_fixture_has_the_exact_code_and_decodes_to_its_fields()
    {
        using var fixture = Fixture(); var root = fixture.RootElement;
        var hostId = Convert.FromHexString(root.GetProperty("hostId").GetString()!);
        var pairId = Convert.FromHexString(root.GetProperty("pairId").GetString()!);
        var code = WifiTypedCode.Encode(OfferFor(hostId, pairId, root.GetProperty("ip").GetString()!, root.GetProperty("pairPort").GetInt32(),
            root.GetProperty("sessionPort").GetInt32()), root.GetProperty("pin").GetString()!);
        Assert.AreEqual(root.GetProperty("code").GetString(), code);
        var decoded = WifiTypedCode.Decode(code);
        CollectionAssert.AreEqual(hostId, decoded.HostId); CollectionAssert.AreEqual(pairId, decoded.PairId);
        Assert.AreEqual("192.168.15.4", decoded.Ip); Assert.AreEqual(40001, decoded.PairPort); Assert.AreEqual(40002, decoded.SessionPort);
        Assert.AreEqual("004217", decoded.Pin, "Leading zeros survive.");
    }

    [TestMethod]
    public void Shape_is_one_hundred_base32_characters_in_twenty_groups_of_five()
    {
        var code = WifiTypedCode.Encode(OfferFor(new byte[32], new byte[16], "10.0.0.2", 40001, 40002), "123456");
        Assert.AreEqual(20, code.Split('-').Length);
        Assert.IsTrue(code.Split('-').All(group => group.Length == 5));
        Assert.AreEqual(WifiTypedCode.TextLength, code.Replace("-", "").Length);
        Assert.IsTrue(code.Replace("-", "").All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7'), "RFC 4648 alphabet: no 0, 1, 8 or 9 to confuse.");
    }

    [TestMethod]
    public void Typing_tolerates_case_spaces_and_missing_dashes_but_not_a_mistake()
    {
        var offer = OfferFor(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16), "192.168.1.5", 40001, 40002);
        var code = WifiTypedCode.Encode(offer, "000000");
        Assert.AreEqual("000000", WifiTypedCode.Decode(code.ToLowerInvariant()).Pin);
        Assert.AreEqual("000000", WifiTypedCode.Decode(code.Replace("-", " ")).Pin);
        Assert.AreEqual("000000", WifiTypedCode.Decode(code.Replace("-", "")).Pin);
        for (var index = 0; index < code.Length; index++)
        {
            if (code[index] == '-') continue;
            var changed = code[index] == 'A' ? 'B' : 'A';
            Assert.ThrowsException<InvalidDataException>(() => WifiTypedCode.Decode(code[..index] + changed + code[(index + 1)..]), $"typo at {index}");
        }
    }

    [TestMethod]
    public void Wrong_length_alphabet_version_address_and_ports_are_rejected()
    {
        var offer = OfferFor(new byte[32], new byte[16], "192.168.1.5", 40001, 40002);
        var code = WifiTypedCode.Encode(offer, "123456");
        foreach (var bad in new[] { "", code[..^1], code + "A", code.Replace('A', '0'), code.Replace('B', '1'), new string('A', 100) })
            Assert.ThrowsException<InvalidDataException>(() => WifiTypedCode.Decode(bad), bad);
        Assert.ThrowsException<InvalidDataException>(() => WifiTypedCode.Encode(offer, "12345"));
        Assert.ThrowsException<InvalidDataException>(() => WifiTypedCode.Encode(offer, "12345a"));
        // A valid-CRC code for a public address, equal ports or an excluded port must still be refused.
        foreach (var (ip, pairPort, sessionPort) in new[] { ("8.8.8.8", 40001, 40002), ("192.168.1.5", 40001, 40001), ("192.168.1.5", 8765, 40002), ("192.168.1.5", 40001, 80) })
            Assert.ThrowsException<InvalidDataException>(() => WifiTypedCode.Decode(WifiTypedCode.Encode(OfferFor(new byte[32], new byte[16], ip, pairPort, sessionPort), "123456")), ip);
    }

    [TestMethod]
    public void Crc16_matches_the_standard_check_value()
    {
        // CRC-16/CCITT-FALSE of "123456789" is 0x29B1; the Kotlin side must agree.
        Assert.AreEqual((ushort)0x29B1, WifiTypedCode.Crc16("123456789"u8));
    }

    [TestMethod]
    public void Pin_token_is_the_sha256_of_the_ascii_digits_and_keeps_leading_zeros()
    {
        CollectionAssert.AreEqual(SHA256.HashData("000123"u8.ToArray()), WifiPairingOffer.TokenFromPin("000123"));
        Assert.ThrowsException<InvalidDataException>(() => WifiPairingOffer.TokenFromPin("12345"));
        Assert.ThrowsException<InvalidDataException>(() => WifiPairingOffer.TokenFromPin("12 456"));
    }
}