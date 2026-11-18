package com.panelos.panelhome.data.wifi

import java.security.KeyStore
import java.security.cert.X509Certificate

/**
 * What the pairing and the session need from the device identity. [AndroidWifiIdentity] is the real one (AndroidKeyStore
 * P-256, non-exportable, one alias per paired PC); tests use an in-memory stand-in with the same contract.
 */
internal interface WifiIdentityPort {
    /** New P-256 key for this PC; returns its DER SubjectPublicKeyInfo. Refuses to overwrite an existing identity. */
    fun create(hostId: ByteArray): ByteArray
    /**
     * Pairing never touches a working identity until the new one is approved: [stageNewKey] makes a separate new key (the
     * current pairing, if any, keeps working), [signWithStagedKey] proves possession with it, and [commitStaged] validates the
     * certificate, writes the metadata, makes the new key the identity and only then removes the old one. [discardStaged]
     * drops a key that was never committed. Anything left staged by a crash is replaced by the next [stageNewKey].
     */
    fun stageNewKey(hostId: ByteArray): ByteArray
    fun signWithStagedKey(hostId: ByteArray, message: ByteArray): ByteArray
    fun commitStaged(hostId: ByteArray, leaf: X509Certificate, root: X509Certificate, metadata: ByteArray)
    fun discardStaged(hostId: ByteArray)
    fun exists(hostId: ByteArray): Boolean
    fun hostIds(): List<ByteArray>
    fun sign(hostId: ByteArray, message: ByteArray): ByteArray
    fun entry(hostId: ByteArray): KeyStore.PrivateKeyEntry
    fun installCertificate(hostId: ByteArray, leaf: X509Certificate, root: X509Certificate)
    fun writeMetadata(hostId: ByteArray, data: ByteArray)
    fun readMetadata(hostId: ByteArray): ByteArray
    /** Deletes the key and the metadata. Idempotent. */
    fun forget(hostId: ByteArray)
}