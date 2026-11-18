# Offline Wi-Fi test vectors

`proof-v1.json` is synthetic test data generated with Java JCA P-256 and a DER
SHA256withECDSA signature. The public SPKI, challenge, host hash, pair id, token,
exact signed transcript and signature are hex encoded. **No private key is
retained.** These values are not credentials for a running host.

Both .NET and Kotlin/JVM rebuild the binary transcript and verify the same
signature. Every transcript byte is mutated in negative tests. Regeneration
requires the existing JDK and an explicit new output path:

```powershell
java scripts/GenerateWifiProofFixture.java <new-fixture-path.json>
```

The approved QR delta adds a public root certificate (`rootCa`) and verifies it
against the full SPKI hash, but does not change this signed transcript. See
`docs/wifi-core.md` for the schema and limits. QR camera readability and native
Android Keystore/TLS are physical validation gates, not covered by this vector.

`certificates-v1.json` contains only synthetic public X.509 certificates generated
with the .NET BCL. Tests use its explicit `asOfUnixMillis` to avoid relying on the
machine clock. Both runtimes exercise the real QR/root/leaf validators against
swapped roots, MITM with the expected SAN, missing constraints, extra KeyUsage,
wrong EKU/SAN, invalid signatures and expired/future leaves. No private key is
retained and no certificate is imported into an OS store.

- `typed-code-v1.json`: synthetic typed pairing code (PIN mode): 62-byte payload (version, host id, pair id, IPv4, ports, PIN, CRC-16/CCITT-FALSE) as 100 Base32 characters in groups of five. Read by the .NET and Kotlin suites.
