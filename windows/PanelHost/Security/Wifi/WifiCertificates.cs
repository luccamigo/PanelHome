using System.Net.Security;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PanelHost.Security.Wifi;

internal static class WifiCertificates
{
    public const string ServerEku = "1.3.6.1.5.5.7.3.1";
    public const string ClientEku = "1.3.6.1.5.5.7.3.2";
    public static byte[] HostId(X509Certificate2 root) => SHA256.HashData(root.PublicKey.ExportSubjectPublicKeyInfo());
    public static string ServerName(byte[] hostId) => $"panelos-{Convert.ToHexString(hostId)[..32].ToLowerInvariant()}.local";
    public static string ServerName(X509Certificate2 root) => $"panelos-{Convert.ToHexString(HostId(root))[..32].ToLowerInvariant()}.local";

    public static X509Certificate2 CreateRoot(ECDsa key, DateTimeOffset now)
    {
        var request = new CertificateRequest("CN=PanelOS private WiFi root", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(now.AddMinutes(-1), now.AddYears(5));
    }

    // No CSR extensions are copied from a peer: all constraints are host policy.
    public static X509Certificate2 Issue(X509Certificate2 root, byte[] spki, bool server, DateTimeOffset now)
    {
        using var key = WifiProof.ReadKey(spki);
        var request = new CertificateRequest(server ? "CN=PanelOS host" : "CN=PanelOS paired device", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root,
            includeKeyIdentifier: true, includeIssuerAndSerial: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(server ? ServerEku : ClientEku) }, true));
        if (server)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(ServerName(root));
            request.CertificateExtensions.Add(san.Build());
        }
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x7f) | 1);
        var expires = new DateTimeOffset(root.NotAfter.ToUniversalTime());
        return request.Create(root, now.AddMinutes(-1), now.AddYears(1) < expires ? now.AddYears(1) : expires, serial);
    }

    public static bool Validate(X509Certificate2 leaf, X509Certificate2 root, byte[] expectedHostId, bool server,
        DateTime? verificationTime = null)
    {
        try
        {
            if (!ValidateRoot(root, expectedHostId, verificationTime)) return false;
            using var rootKey = WifiProof.ReadKey(root.PublicKey.ExportSubjectPublicKeyInfo());
            using var leafKey = WifiProof.ReadKey(leaf.PublicKey.ExportSubjectPublicKeyInfo());
            var rootCa = root.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            var leafCa = leaf.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            if (rootCa is not { CertificateAuthority: true, HasPathLengthConstraint: true, PathLengthConstraint: 0 } ||
                leafCa is not { CertificateAuthority: false } || leaf.SignatureAlgorithm.Value != "1.2.840.10045.4.3.2" ||
                root.SignatureAlgorithm.Value != "1.2.840.10045.4.3.2") return false;
            if (root.NotAfter - root.NotBefore > TimeSpan.FromDays(366 * 5 + 1) ||
                leaf.NotAfter - leaf.NotBefore > TimeSpan.FromDays(367)) return false;
            var usage = leaf.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            var eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
            if (usage?.KeyUsages != X509KeyUsageFlags.DigitalSignature || eku is null ||
                eku.EnhancedKeyUsages.Count != 1 || eku.EnhancedKeyUsages[0].Value != (server ? ServerEku : ClientEku)) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            if (verificationTime is { } time) chain.ChainPolicy.VerificationTime = time;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(server ? ServerEku : ClientEku));
            if (!chain.Build(leaf) || chain.ChainElements.Count != 2 ||
                !chain.ChainElements[1].Certificate.RawData.AsSpan().SequenceEqual(root.RawData)) return false;
            return !server || leaf.MatchesHostname(ServerName(root), allowWildcards: false, allowCommonName: false);
        }
        catch (Exception error) when (error is CryptographicException or InvalidOperationException or InvalidDataException)
        { return false; }
    }

    public static bool ValidateRoot(X509Certificate2 root, byte[] hostId, DateTime? verificationTime = null)
    {
        try
        {
            var now = verificationTime ?? DateTime.UtcNow;
            if (hostId.Length != 32 || root.RawData.Length > 512 ||
                !CryptographicOperations.FixedTimeEquals(HostId(root), hostId) ||
                root.NotBefore.ToUniversalTime() > now || root.NotAfter.ToUniversalTime() <= now ||
                root.NotAfter - root.NotBefore > TimeSpan.FromDays(366 * 5 + 1) ||
                root.SignatureAlgorithm.Value != "1.2.840.10045.4.3.2" ||
                !root.SubjectName.RawData.AsSpan().SequenceEqual(root.IssuerName.RawData)) return false;
            if (root.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault() is not
                { CertificateAuthority: true, HasPathLengthConstraint: true, PathLengthConstraint: 0, Critical: true } ||
                root.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault()?.KeyUsages != X509KeyUsageFlags.KeyCertSign ||
                root.Extensions.Any(extension => extension.Critical && extension.Oid?.Value is not ("2.5.29.19" or "2.5.29.15"))) return false;
            var reader = new AsnReader(root.RawData, AsnEncodingRules.DER);
            var certificate = reader.ReadSequence();
            var signed = certificate.ReadEncodedValue(); certificate.ReadEncodedValue();
            var signature = certificate.ReadBitString(out var unused);
            certificate.ThrowIfNotEmpty(); reader.ThrowIfNotEmpty();
            return unused == 0 && WifiProof.Verify(root.PublicKey.ExportSubjectPublicKeyInfo(), signed.ToArray(), signature);
        }
        catch (Exception error) when (error is CryptographicException or InvalidOperationException or AsnContentException)
        { return false; }
    }
}
