package com.panelos.panelhome.data.transport

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class AdbTcpAuthenticationTest {
    @Test
    fun tokenParserAcceptsExactly256BitsOfHex() {
        val token = ByteArray(AdbTcpAuthentication.TOKEN_BYTES) { it.toByte() }
        val encoded = token.joinToString(separator = "") { "%02X".format(it) }

        assertArrayEquals(token, AdbTcpAuthentication.parseToken(encoded))
        assertNull(AdbTcpAuthentication.parseToken(encoded.dropLast(2)))
        assertNull(AdbTcpAuthentication.parseToken("z".repeat(encoded.length)))
    }

    @Test
    fun missingTokenCannotOpenAnyAdbTcpChannel() = runBlocking {
        val result = runCatching {
            AdbTcpTransport(authenticationToken = null).connect(LogicalChannel.CONTROL)
        }

        assertTrue(result.exceptionOrNull() is TransportDisconnectedException)
        assertTrue(result.exceptionOrNull()?.message?.contains("missing authentication") == true)
    }
}
