package com.panelos.panelhome.data.connection

import java.io.BufferedWriter
import java.io.StringWriter
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ActiveWriterSessionTest {
    @Test
    fun onlyMatchingConnectionIdAndWriterIdentityCanWrite() {
        val oldWriter = BufferedWriter(StringWriter())
        val newWriter = BufferedWriter(StringWriter())
        val activeSession = ActiveWriterSession(
            connectionId = 2L,
            writer = newWriter,
        )

        assertFalse(
            activeSession.matches(
                expectedConnectionId = 1L,
                expectedWriter = oldWriter,
            ),
        )
        assertFalse(
            activeSession.matches(
                expectedConnectionId = 2L,
                expectedWriter = oldWriter,
            ),
        )
        assertTrue(
            activeSession.matches(
                expectedConnectionId = 2L,
                expectedWriter = newWriter,
            ),
        )
    }
}
