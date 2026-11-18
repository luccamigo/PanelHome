package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.model.FpsState
import com.panelos.panelhome.domain.protocol.*
import org.junit.Assert.*
import org.junit.Test

class RedesignTelemetryTest {
    @Test fun absentAndInvalidSourcesStayHidden() {
        val controller = TelemetryController()
        controller.apply(TelemetryMessage(cpu = CpuTelemetry(temperatureC = 0.0)))
        assertNull(controller.state.value.cpu.temperatureCelsius)
        assertEquals(FpsState.Unavailable, controller.state.value.fps)
        controller.apply(TelemetryMessage(fps = FpsTelemetry("measuring", Double.NaN, "app")))
        assertEquals(FpsState.Unavailable, controller.state.value.fps)
    }

    @Test fun validPresentsMapToUiAndResetWithAppAndSession() {
        val controller = TelemetryController()
        controller.apply(TelemetryMessage(fps = FpsTelemetry("no_app")))
        assertEquals(FpsState.NoApp, controller.state.value.fps)
        controller.apply(TelemetryMessage(fps = FpsTelemetry("measuring", 59.8, "test")))
        controller.apply(TelemetryMessage(fps = FpsTelemetry("measuring", 62.0, "test")))
        val measured = controller.state.value.fps as FpsState.Measuring
        assertEquals(62, measured.current)
        assertEquals(listOf(59.8f, 62f), measured.series)
        controller.apply(TelemetryMessage(fps = FpsTelemetry("measuring", 50.0, "other")))
        assertEquals(listOf(50f), (controller.state.value.fps as FpsState.Measuring).series)
        controller.reset()
        assertEquals(FpsState.Unavailable, controller.state.value.fps)
    }
}
