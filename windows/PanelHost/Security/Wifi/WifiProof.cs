using System.Formats.Asn1;
using System.Security.Cryptography;

namespace PanelHost.Security.Wifi;

internal static class WifiProof
{
    public static byte[] Message(byte[] challenge, byte[] hostId, byte[] spki, byte[] pairId, byte[] token)
    {
        if (challenge.Length != 32 || hostId.Length != 32 || pairId.Length != 16 || token.Length != 32)
            throw new InvalidDataException("Invalid pairing field length.");
        using var key = ReadKey(spki);
        return [.. "PanelOS WiFi Pair v1"u8, 0, .. challenge, .. hostId,
            .. SHA256.HashData(spki), .. pairId, .. SHA256.HashData(token)];
    }

    public static ECDsa ReadKey(byte[] spki)
    {
        if (spki.Length is < 1 or > 512) throw new InvalidDataException("Invalid public key length.");
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out var consumed);
            if (consumed != spki.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                !key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki))
                throw new InvalidDataException("Pairing requires canonical P-256 SPKI.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    public static bool Verify(byte[] spki, byte[] message, byte[] signature)
    {
        try
        {
            if (signature.Length is < 8 or > 72) return false;
            var reader = new AsnReader(signature, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            for (var i = 0; i < 2; i++)
            {
                var integer = sequence.ReadInteger();
                if (integer.Sign <= 0 || integer.GetBitLength() > 256) return false;
            }
            sequence.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
            using var key = ReadKey(spki);
            return key.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or AsnContentException)
        { return false; }
    }
}
