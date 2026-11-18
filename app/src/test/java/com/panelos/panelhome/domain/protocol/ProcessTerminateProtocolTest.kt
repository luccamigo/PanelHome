package com.panelos.panelhome.domain.protocol

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ProcessTerminateProtocolTest {
    private val id = "0123456789abcdef0123456789abcdef"

    @Test fun commandRoundTripsWithLargeStartTicks() {
        val command = CommandMessage(ProcessTerminateCommand(id, 4242, 638000000000000001L))
        val encoded = PanelProtocolJson.encode(command)
        assertEquals("processes.terminate", JSONObject(encoded).getString("command"))
        assertEquals(command, PanelProtocolJson.decode(encoded))
    }

    @Test fun commandRejectsBadIdentity() {
        listOf(
            """{"type":"command","command":"processes.terminate","requestId":"short","pid":4,"startTimeUtcTicks":1}""",
            """{"type":"command","command":"processes.terminate","requestId":"$id","pid":0,"startTimeUtcTicks":1}""",
            """{"type":"command","command":"processes.terminate","requestId":"$id","pid":4,"startTimeUtcTicks":0}""",
        ).forEach { source -> assertThrows(Exception::class.java) { PanelProtocolJson.decode(source) } }
    }

    @Test fun everyResultFromTheHostDecodesAndRoundTrips() {
        ProcessTerminateResultMessage.RESULTS.forEach { result ->
            val source = """{"type":"processes.terminate_result","requestId":"$id","pid":4242,"result":"$result"}"""
            val message = PanelProtocolJson.decode(source) as ProcessTerminateResultMessage
            assertEquals(result, message.result)
            assertEquals(JSONObject(source).toString(), JSONObject(PanelProtocolJson.encode(message)).toString())
        }
    }

    @Test fun invalidRequestResultMayCarryNoIdentity() {
        val message = PanelProtocolJson.decode("""{"type":"processes.terminate_result","requestId":null,"pid":null,"result":"invalid_request"}""")
        assertNull((message as ProcessTerminateResultMessage).requestId)
    }

    @Test fun unknownResultIsRejected() {
        assertThrows(Exception::class.java) {
            PanelProtocolJson.decode("""{"type":"processes.terminate_result","requestId":"$id","pid":4,"result":"maybe"}""")
        }
    }
}
