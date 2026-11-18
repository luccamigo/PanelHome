package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.protocol.CpuTelemetry
import com.panelos.panelhome.domain.protocol.TelemetryMessage
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TelemetryControllerTest {
    private val controller = TelemetryController()

    @Test
    fun absentSensorsRemainAbsentRatherThanBecomingZero() {
        controller.apply(TelemetryMessage())

        val state = controller.state.value
        assertNull(state.cpu.temperatureCelsius)
        assertNull(state.cpu.frequencyGhz)
        assertNull(state.gpu.usagePercent)
        assertNull(state.ram.usedGb)
        assertEquals(emptyList<Any>(), state.storage)
    }

    @Test
    fun suppliedMetricsAreMappedWithoutChangingTheirValues() {
        controller.apply(
            TelemetryMessage(
                cpu = CpuTelemetry(
                    name = "CPU",
                    temperatureC = 53.5,
                    usagePercent = 21.0,
                    clockGhz = 4.2,
                ),
            ),
        )

        assertEquals("CPU", controller.state.value.cpu.name)
        assertEquals(53.5, controller.state.value.cpu.temperatureCelsius!!, 0.0)
        assertEquals(4.2, controller.state.value.cpu.frequencyGhz!!, 0.0)
    }
}
