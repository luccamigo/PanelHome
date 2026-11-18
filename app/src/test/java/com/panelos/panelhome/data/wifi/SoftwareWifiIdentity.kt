package com.panelos.panelhome.data.wifi

import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec

/** In-memory stand-in for the AndroidKeyStore identity: same contract, software keys. */
internal class SoftwareWifiIdentity : WifiIdentityPort {
    private class Slot(val pair: KeyPair, var chain: Array<X509Certificate>? = null, var metadata: ByteArray? = null)
    private val slots = LinkedHashMap<String, Slot>()
    private val staged = LinkedHashMap<String, Slot>()
    var forgets = 0; private set
    var creates = 0; private set

    private fun key(hostId: ByteArray) = hostId.joinToString("") { "%02x".format(it.toInt() and 255) }
    override fun create(hostId: ByteArray): ByteArray {
        require(key(hostId) !in slots) { "Explicit forget and new pairing required" }
        require(slots.size < 8)
        creates++
        val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        slots[key(hostId)] = Slot(pair)
        return pair.public.encoded
    }
    override fun stageNewKey(hostId: ByteArray): ByteArray {
        require(key(hostId) in slots || slots.size < 8)
        creates++
        val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        staged[key(hostId)] = Slot(pair)
        return pair.public.encoded
    }
    override fun signWithStagedKey(hostId: ByteArray, message: ByteArray) = WifiCrypto.sign(staged.getValue(key(hostId)).pair.private, message)
    override fun commitStaged(hostId: ByteArray, leaf: X509Certificate, root: X509Certificate, metadata: ByteArray) {
        val slot = staged.getValue(key(hostId))
        WifiTls.validateClientLeaf(leaf, root, hostId, slot.pair.public.encoded)
        slot.chain = arrayOf(leaf, root); slot.metadata = metadata.copyOf()
        slots[key(hostId)] = slot // the old identity is replaced only now
        staged.remove(key(hostId))
    }
    override fun discardStaged(hostId: ByteArray) { staged.remove(key(hostId)) }
    val stagedCount get() = staged.size
    override fun exists(hostId: ByteArray) = key(hostId) in slots
    override fun hostIds(): List<ByteArray> = slots.keys.map { hex -> ByteArray(32) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() } }
    override fun sign(hostId: ByteArray, message: ByteArray) = WifiCrypto.sign(slots.getValue(key(hostId)).pair.private, message)
    override fun entry(hostId: ByteArray): KeyStore.PrivateKeyEntry {
        val slot = slots[key(hostId)] ?: error("Pairing identity unavailable")
        checkNotNull(slot.metadata) { "Missing metadata must not resurrect trust" }
        return KeyStore.PrivateKeyEntry(slot.pair.private, checkNotNull(slot.chain))
    }
    override fun installCertificate(hostId: ByteArray, leaf: X509Certificate, root: X509Certificate) {
        val slot = slots.getValue(key(hostId))
        WifiTls.validateClientLeaf(leaf, root, hostId, slot.pair.public.encoded)
        slot.chain = arrayOf(leaf, root)
    }
    override fun writeMetadata(hostId: ByteArray, data: ByteArray) { slots.getValue(key(hostId)).metadata = data.copyOf() }
    override fun readMetadata(hostId: ByteArray): ByteArray = checkNotNull(slots.getValue(key(hostId)).metadata).copyOf()
    override fun forget(hostId: ByteArray) { forgets++; slots.remove(key(hostId)); staged.remove(key(hostId)) }
}