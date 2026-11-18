// Offline test data only. No private key is written. Requires the existing JDK.
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.io.ByteArrayOutputStream;
import java.security.*;
import java.security.spec.ECGenParameterSpec;
import java.util.HexFormat;

class GenerateWifiProofFixture {
    static byte[] range(int size, int start) {
        byte[] result = new byte[size];
        for (int i = 0; i < size; i++) result[i] = (byte)(start + i);
        return result;
    }
    static String field(String name, byte[] data) {
        return "  \"" + name + "\":\"" + HexFormat.of().formatHex(data) + "\"";
    }
    public static void main(String[] args) throws Exception {
        if (args.length != 1) throw new IllegalArgumentException("Explicit fixture output path required");
        var generator = KeyPairGenerator.getInstance("EC");
        generator.initialize(new ECGenParameterSpec("secp256r1"));
        var key = generator.generateKeyPair();
        byte[] spki = key.getPublic().getEncoded(), challenge = range(32, 0), hostId = range(32, 32),
            pairId = range(16, 64), token = range(32, 80);
        var hash = MessageDigest.getInstance("SHA-256");
        var message = new ByteArrayOutputStream();
        message.write("PanelOS WiFi Pair v1".getBytes(StandardCharsets.US_ASCII)); message.write(0);
        message.write(challenge); message.write(hostId); message.write(hash.digest(spki));
        message.write(pairId); message.write(hash.digest(token));
        var signer = Signature.getInstance("SHA256withECDSA");
        signer.initSign(key.getPrivate()); signer.update(message.toByteArray());
        var json = "{\n  \"description\":\"Synthetic offline fixture; Java JCA P-256, DER signature, no private key retained\",\n" +
            String.join(",\n", field("spki", spki), field("challenge", challenge), field("hostId", hostId),
                field("pairId", pairId), field("token", token), field("message", message.toByteArray()), field("signature", signer.sign())) + "\n}\n";
        Files.writeString(Path.of(args[0]), json, StandardCharsets.UTF_8, StandardOpenOption.CREATE_NEW);
    }
}
