package com.panelos.panelhome.domain.protocol

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class MonitoringProtocolJsonTest {
    @Test fun clientAnnouncesBothAdditiveFeatures() {
        assertTrue(PanelProtocol.FEATURE_COMPUTER_STATS in HelloMessage().features)
        assertTrue(PanelProtocol.FEATURE_PROCESSES in HelloMessage().features)
        val oldHost = PanelProtocolJson.decode("""{"type":"hello_ack","protocolVersion":1,"hostName":"old"}""") as HelloAckMessage
        assertTrue(oldHost.features.isEmpty())
    }

    @Test fun networkContractPreservesNullAndAdapterNames() {
        val source = """{"type":"network_rates","sampledAtUnixMs":1800000000000,"downloadBytesPerSecond":null,"uploadBytesPerSecond":512.0,"adapters":[{"id":"opaque-id","name":"Ethernet","downloadBytesPerSecond":null,"uploadBytesPerSecond":512.0}]}"""
        val message = PanelProtocolJson.decode(source) as NetworkRatesMessage
        assertNull(message.downloadBytesPerSecond)
        assertEquals("Ethernet", message.adapters.single().name)
        assertEquals(JSONObject(source).toString(), JSONObject(PanelProtocolJson.encode(message)).toString())
    }

    @Test fun processContractDoesNotLoseLargeStartTimeTicksOrMissingMetrics() {
        val source = """{"type":"processes","sampledAtUnixMs":1800000000000,"sort":"cpu","grouped":false,"terminateEnabled":false,"items":[{"name":"editor","pid":123,"startTimeUtcTicks":638000000000000001,"cpuPercent":null,"memoryBytes":null,"owner":"unknown","protected":true,"count":1}]}"""
        val message = PanelProtocolJson.decode(source) as ProcessesMessage
        assertEquals(638000000000000001L, message.items.single().startTimeUtcTicks)
        assertFalse(message.items.single().readable)
        assertEquals(JSONObject(source).toString(), JSONObject(PanelProtocolJson.encode(message)).toString())
    }

    @Test fun selectionCommandContainsOnlyReadOnlyOptions() {
        val command = CommandMessage(ProcessesOptionsCommand(ProcessSort.MEMORY, true))
        val encoded = PanelProtocolJson.encode(command)
        val json = JSONObject(encoded)
        assertEquals(setOf("type", "command", "sort", "grouped"), json.keys().asSequence().toSet())
        assertEquals("processes.options", json.getString("command"))
        assertEquals(command, PanelProtocolJson.decode(encoded))
    }

    @Test fun malformedNewDataIsRejectedInsteadOfCoercedToZero() {
        val network = """{"type":"network_rates","sampledAtUnixMs":1,"downloadBytesPerSecond":"42","uploadBytesPerSecond":null,"adapters":[]}"""
        assertThrows(PanelProtocolException::class.java) { PanelProtocolJson.decode(network) }
        val command = """{"type":"command","command":"processes.options","sort":"cpu","grouped":"true"}"""
        assertThrows(PanelProtocolException::class.java) { PanelProtocolJson.decode(command) }
        assertThrows(PanelProtocolException::class.java) { PanelProtocolJson.decode(command.replace("cpu", "disk").replace("\"true\"", "true")) }
    }

    @Test fun oversizedProcessListsAreRejected() {
        val items = List(16) { ProcessItem("process-$it") }
        val encoded = PanelProtocolJson.encode(ProcessesMessage(1, items = items))
        assertThrows(PanelProtocolException::class.java) { PanelProtocolJson.decode(encoded) }
    }
}
