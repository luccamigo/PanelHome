package com.panelos.panelhome.data.wifi

import android.content.Context
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import java.io.File
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Platform adapter over the AndroidKeyStore. The Service constructs it at start, but constructing it touches no Keystore: the
 * store is opened on first use, and every caller (restore, pairing, forget) uses it off the main thread. The cable flow itself
 * never uses it. Each paired PC has two possible Keystore aliases, "slot A" ([ALIAS_PREFIX] + host id)
 * and "slot B" (the same + ".b"). A key cannot be renamed inside the Keystore, so a new pairing is staged in the free
 * slot and becomes the identity by writing its metadata record; only then is the old slot deleted. The slot that has a
 * metadata record is the identity; an unfinished staging has none and is never used.
 */
internal class AndroidWifiIdentity(context: Context) : WifiIdentityPort {
    private val directory = File(context.noBackupFilesDir, "panelos-wifi")
    // Loaded on first use, which callers do off the main thread; constructing this object touches no Keystore.
    private val store by lazy { KeyStore.getInstance("AndroidKeyStore").apply { load(null) } }
    private fun alias(hostId: ByteArray): String {
        require(hostId.size == 32)
        return ALIAS_PREFIX + hostId.joinToString("") { "%02x".format(it.toInt() and 255) }
    }
    private fun slotA(hostId: ByteArray) = alias(hostId)
    private fun slotB(hostId: ByteArray) = alias(hostId) + SLOT_B

    /** The slot that holds the current identity: the one with a metadata record (the newer one if a crash left two). */
    private fun activeSlot(hostId: ByteArray): String? {
        val a = slotA(hostId); val b = slotB(hostId)
        val aOk = store.containsAlias(a) && File(directory, a).exists()
        val bOk = store.containsAlias(b) && File(directory, b).exists()
        return when {
            aOk && bOk -> if (File(directory, b).lastModified() > File(directory, a).lastModified()) b else a
            aOk -> a
            bOk -> b
            else -> null
        }
    }
    /** Where the identity's key lives: the active slot, or whatever slot exists while a legacy create() is not finished. */
    private fun holder(hostId: ByteArray): String =
        activeSlot(hostId) ?: slotA(hostId).takeIf { store.containsAlias(it) } ?: slotB(hostId).takeIf { store.containsAlias(it) } ?: slotA(hostId)
    private fun stagedSlot(hostId: ByteArray): String = if (activeSlot(hostId) == slotA(hostId)) slotB(hostId) else slotA(hostId)

    private fun generate(name: String): ByteArray {
        WifiTls.requirePlatform(Build.VERSION.SDK_INT)
        val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
        generator.initialize(KeyGenParameterSpec.Builder(name, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            // SHA-256 for the proof of possession; NONE because the bundled Wi-Fi TLS provider (Conscrypt) signs the handshake
            // transcript through the key as a pre-hashed digest, which a Keystore key refuses unless it allows NONE.
            // Proven with the AndroidKeyStore on an API 28 emulator; the test tablet still needs physical validation.
            .setDigests(KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_NONE).build())
        // Hardware backing is provider/device-dependent and is not promised.
        return generator.generateKeyPair().public.encoded
    }

    private fun requireRoom(hostId: ByteArray) =
        require(exists(hostId) || hostIds().size < 8) { "Too many paired computers" }

    override fun create(hostId: ByteArray): ByteArray {
        require(!exists(hostId)) { "Explicit forget and new pairing required" }
        requireRoom(hostId)
        return generate(slotA(hostId))
    }
    override fun stageNewKey(hostId: ByteArray): ByteArray {
        requireRoom(hostId)
        val name = stagedSlot(hostId)
        deleteSlot(name) // a leftover from an interrupted pairing
        return generate(name)
    }
    override fun signWithStagedKey(hostId: ByteArray, message: ByteArray): ByteArray {
        val entry = store.getEntry(stagedSlot(hostId), null) as? KeyStore.PrivateKeyEntry ?: error("Staged key unavailable")
        return WifiCrypto.sign(entry.privateKey, message)
    }
    override fun commitStaged(hostId: ByteArray, leaf: X509Certificate, root: X509Certificate, metadata: ByteArray) {
        val old = activeSlot(hostId)
        val name = stagedSlot(hostId)
        val entry = store.getEntry(name, null) as? KeyStore.PrivateKeyEntry ?: error("Staged key unavailable")
        WifiTls.validateClientLeaf(leaf, root, hostId, entry.certificate.publicKey.encoded)
        store.setKeyEntry(name, entry.privateKey, null, arrayOf(leaf, root))
        require((store.getEntry(name, null) as KeyStore.PrivateKeyEntry).certificate.encoded.contentEquals(leaf.encoded))
        writeMetadataFor(name, metadata)     // from here on the new slot is the identity
        old?.let { deleteSlot(it) }          // and only now does the old pairing go away
    }
    override fun discardStaged(hostId: ByteArray) {
        val name = stagedSlot(hostId)
        if (name != activeSlot(hostId)) deleteSlot(name)
    }
    private fun deleteSlot(name: String) {
        AtomicFile(File(directory, name)).delete()
        if (store.containsAlias(name)) store.deleteEntry(name)
    }

    override fun exists(hostId: ByteArray): Boolean = store.containsAlias(slotA(hostId)) || store.containsAlias(slotB(hostId))
    override fun hostIds(): List<ByteArray> = store.aliases().toList()
        .filter { it.startsWith(ALIAS_PREFIX) }
        .mapNotNull { name -> name.removePrefix(ALIAS_PREFIX).removeSuffix(SLOT_B).takeIf { it.length == 64 && it.all { c -> c in "0123456789abcdef" } } }
        .distinct()
        .map { hex -> ByteArray(32) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() } }
    override fun sign(hostId: ByteArray, message: ByteArray): ByteArray = WifiCrypto.sign(rawEntry(hostId).privateKey, message)
    override fun entry(hostId: ByteArray): KeyStore.PrivateKeyEntry {
        readMetadata(hostId) // Missing/corrupt metadata must not resurrect trust.
        return rawEntry(hostId)
    }
    private fun rawEntry(hostId: ByteArray): KeyStore.PrivateKeyEntry =
        store.getEntry(holder(hostId), null) as? KeyStore.PrivateKeyEntry ?: error("Pairing identity unavailable")

    override fun installCertificate(hostId: ByteArray, leaf: X509Certificate, root: X509Certificate) {
        val entry = rawEntry(hostId)
        val name = holder(hostId)
        WifiTls.validateClientLeaf(leaf, root, hostId, entry.certificate.publicKey.encoded)
        store.setKeyEntry(name, entry.privateKey, null, arrayOf(leaf, root))
        require(rawEntry(hostId).certificate.encoded.contentEquals(leaf.encoded))
    }
    override fun writeMetadata(hostId: ByteArray, data: ByteArray) = writeMetadataFor(holder(hostId), data)
    private fun writeMetadataFor(name: String, data: ByteArray) {
        require(data.size <= 16384)
        directory.mkdirs()
        val keyName = "panelos.wifi.metadata"
        val key = (store.getKey(keyName, null) as? SecretKey) ?: KeyGenerator.getInstance("AES", "AndroidKeyStore").run {
            init(KeyGenParameterSpec.Builder(keyName, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256).build()); generateKey()
        }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.ENCRYPT_MODE, key); updateAAD(name.toByteArray(Charsets.US_ASCII))
        }
        val encoded = byteArrayOf(1) + cipher.iv + cipher.doFinal(data)
        val file = AtomicFile(File(directory, name))
        val output = file.startWrite()
        try { output.write(encoded); file.finishWrite(output) }
        catch (error: Throwable) { file.failWrite(output); throw error }
    }
    override fun readMetadata(hostId: ByteArray): ByteArray {
        val name = holder(hostId)
        val file = File(directory, name)
        require(file.length() in 29..16413)
        val encoded = AtomicFile(file).readFully()
        require(encoded[0] == 1.toByte())
        val key = store.getKey("panelos.wifi.metadata", null) as? SecretKey ?: error("Pairing metadata key unavailable")
        return Cipher.getInstance("AES/GCM/NoPadding").run {
            init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, encoded.copyOfRange(1, 13)))
            updateAAD(name.toByteArray(Charsets.US_ASCII)); doFinal(encoded, 13, encoded.size - 13)
        }
    }
    override fun forget(hostId: ByteArray) {
        deleteSlot(slotA(hostId))
        deleteSlot(slotB(hostId))
    }

    private companion object {
        const val ALIAS_PREFIX = "panelos.wifi.identity."
        const val SLOT_B = ".b"
    }
}
