package com.panelos.panelhome.presentation

import android.view.Surface
import com.panelos.panelhome.data.connection.PanelConnection
import com.panelos.panelhome.data.connection.PanelConnectionEvent
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.data.video.DecodedVideoFrame
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.H264PacketHeader
import com.panelos.panelhome.data.video.H264VideoPacket
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.data.video.SilentVideoLogger
import com.panelos.panelhome.data.video.VideoConnection
import com.panelos.panelhome.data.video.VideoClientCloseReason
import com.panelos.panelhome.data.video.VideoProtocol
import com.panelos.panelhome.data.video.VideoSessionInfo
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.DesktopStatus
import com.panelos.panelhome.domain.model.DesktopVideoCodec
import com.panelos.panelhome.domain.model.DesktopVideoStatus
import com.panelos.panelhome.domain.model.DeviceProfile
import com.panelos.panelhome.domain.model.MediaArtwork
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.domain.protocol.CpuTelemetry
import com.panelos.panelhome.domain.protocol.DesktopCommand
import com.panelos.panelhome.domain.protocol.DesktopErrorCode
import com.panelos.panelhome.domain.protocol.DesktopStateMessage
import com.panelos.panelhome.domain.protocol.GpuTelemetry
import com.panelos.panelhome.domain.protocol.MediaCommand
import com.panelos.panelhome.domain.protocol.MediaStateMessage
import com.panelos.panelhome.domain.protocol.MemoryTelemetry
import com.panelos.panelhome.domain.protocol.StorageTelemetry
import com.panelos.panelhome.domain.protocol.TelemetryMessage
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.SetVolumeCommand
import com.panelos.panelhome.domain.protocol.ToggleVolumeMuteCommand
import com.panelos.panelhome.domain.protocol.TouchCommand
import com.panelos.panelhome.domain.protocol.TouchCommandAction
import com.panelos.panelhome.domain.protocol.VolumeStateMessage
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

internal class TelemetrySessionBehaviorTest : PanelSessionTestFixture() {
    @Test
    fun telemetryEventUpdatesDisplayedMetrics() {
        connection.emit(
            PanelConnectionEvent.TelemetryReceived(
                TelemetryMessage(
                    cpu = CpuTelemetry(
                        name = "AMD Ryzen 5 5600G",
                        temperatureC = 53.5,
                        usagePercent = 21.0,
                        clockGhz = 4.2,
                        powerW = 42.0,
                    ),
                    gpu = GpuTelemetry(
                        name = "NVIDIA GeForce RTX",
                        temperatureC = 48.0,
                        usagePercent = 35.5,
                        clockGhz = 2.7,
                        memoryUsagePercent = 40.0,
                        memoryUsedGb = 3.2,
                        memoryTotalGb = 8.0,
                        powerW = 71.0,
                    ),
                    memory = MemoryTelemetry(
                        usedGb = 12.6,
                        totalGb = 32.0,
                        usagePercent = 39.4,
                    ),
                    storage = listOf(
                        StorageTelemetry(
                            name = "NVMe",
                            temperatureC = 44.0,
                            usagePercent = 61.0,
                        ),
                    ),
                ),
            ),
        )

        val data = coordinator.uiState.value.panelData
        assertEquals("AMD Ryzen 5 5600G", data.cpu.name)
        assertMetricEquals(53.5, data.cpu.temperatureCelsius)
        assertMetricEquals(21.0, data.cpu.usagePercent)
        assertMetricEquals(4.2, data.cpu.frequencyGhz)
        assertMetricEquals(42.0, data.cpu.powerWatts)
        assertEquals("NVIDIA GeForce RTX", data.gpu.name)
        assertMetricEquals(48.0, data.gpu.temperatureCelsius)
        assertMetricEquals(35.5, data.gpu.usagePercent)
        assertMetricEquals(2.7, data.gpu.frequencyGhz)
        assertMetricEquals(40.0, data.gpu.memoryUsagePercent)
        assertMetricEquals(3.2, data.gpu.memoryUsedGb)
        assertMetricEquals(8.0, data.gpu.memoryTotalGb)
        assertMetricEquals(71.0, data.gpu.powerWatts)
        assertMetricEquals(12.6, data.ram.usedGb)
        assertMetricEquals(32.0, data.ram.totalGb)
        assertMetricEquals(39.4, data.ram.usagePercent)
        assertEquals("NVMe", data.storage.single().name)
        assertMetricEquals(44.0, data.storage.single().temperatureCelsius)
        assertMetricEquals(61.0, data.storage.single().usagePercent)
    }

    @Test
    fun optionalTelemetryFieldsRemainUnavailableInsteadOfBecomingZero() {
        connection.emit(
            PanelConnectionEvent.TelemetryReceived(TelemetryMessage()),
        )

        val data = coordinator.uiState.value.panelData
        assertNull(data.cpu.name)
        assertNull(data.cpu.temperatureCelsius)
        assertNull(data.cpu.usagePercent)
        assertNull(data.cpu.frequencyGhz)
        assertNull(data.cpu.powerWatts)
        assertNull(data.gpu.temperatureCelsius)
        assertNull(data.ram.usedGb)
        assertTrue(data.storage.isEmpty())
    }

    @Test
    fun missingCpuTemperatureRemainsNull() {
        connection.emit(
            PanelConnectionEvent.TelemetryReceived(
                TelemetryMessage(
                    cpu = CpuTelemetry(usagePercent = 27.0, clockGhz = 4.15),
                ),
            ),
        )

        val cpu = coordinator.uiState.value.panelData.cpu
        assertNull(cpu.temperatureCelsius)
        assertMetricEquals(27.0, cpu.usagePercent)
        assertMetricEquals(4.15, cpu.frequencyGhz)
    }

    @Test
    fun missingCpuClockRemainsNull() {
        connection.emit(
            PanelConnectionEvent.TelemetryReceived(
                TelemetryMessage(
                    cpu = CpuTelemetry(temperatureC = 52.0, usagePercent = 18.0),
                ),
            ),
        )

        assertNull(coordinator.uiState.value.panelData.cpu.frequencyGhz)
    }

    @Test
    fun absentGpuProducesUnavailableGpuMetrics() {
        connection.emit(
            PanelConnectionEvent.TelemetryReceived(
                TelemetryMessage(
                    cpu = CpuTelemetry(temperatureC = 52.0),
                    gpu = GpuTelemetry(),
                ),
            ),
        )

        val gpu = coordinator.uiState.value.panelData.gpu
        assertNull(gpu.name)
        assertNull(gpu.temperatureCelsius)
        assertNull(gpu.usagePercent)
        assertNull(gpu.frequencyGhz)
        assertNull(gpu.memoryUsedGb)
        assertNull(gpu.memoryTotalGb)
        assertNull(gpu.powerWatts)
    }

    @Test
    fun ramTelemetryIncludesUsageAndOptionalCapacity() {
        connection.emit(
            PanelConnectionEvent.TelemetryReceived(
                TelemetryMessage(
                    memory = MemoryTelemetry(
                        usedGb = 16.25,
                        totalGb = null,
                        usagePercent = 50.8,
                    ),
                ),
            ),
        )

        val ram = coordinator.uiState.value.panelData.ram
        assertMetricEquals(16.25, ram.usedGb)
        assertNull(ram.totalGb)
        assertMetricEquals(50.8, ram.usagePercent)
    }
}
