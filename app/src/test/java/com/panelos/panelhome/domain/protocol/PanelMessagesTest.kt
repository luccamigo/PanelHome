package com.panelos.panelhome.domain.protocol

import com.panelos.panelhome.domain.model.DesktopStatus
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PanelMessagesTest {
    @Test
    fun helloAdvertisesSupportedFeaturesWithoutChangingProtocolVersion() {
        val hello = HelloMessage()

        assertEquals(1, hello.protocolVersion)
        assertTrue(PanelProtocol.FEATURE_MEDIA_ARTWORK in hello.features)
        assertTrue(PanelProtocol.FEATURE_DESKTOP in hello.features)
        assertTrue(PanelProtocol.FEATURE_VOLUME in hello.features)
        assertTrue(PanelProtocol.FEATURE_TOUCH in hello.features)
    }

    @Test
    fun legacyHelloAckWithoutFeaturesRemainsUnsupportedByDefault() {
        val acknowledgement = HelloAckMessage(
            protocolVersion = PanelProtocol.PROTOCOL_VERSION,
            hostName = "LEGACY-PC",
        )

        assertTrue(acknowledgement.features.isEmpty())
    }

    @Test
    fun panelCommandParserPreservesMediaAndDesktopWireNames() {
        assertEquals(MediaCommand.PREVIOUS, PanelCommand.fromWireValue("media.previous"))
        assertEquals(MediaCommand.PLAY_PAUSE, PanelCommand.fromWireValue("media.play_pause"))
        assertEquals(MediaCommand.NEXT, PanelCommand.fromWireValue("media.next"))
        assertEquals(DesktopCommand.START, PanelCommand.fromWireValue("desktop.start"))
        assertEquals(DesktopCommand.STOP, PanelCommand.fromWireValue("desktop.stop"))
        assertEquals(
            SetVolumeCommand(72),
            PanelCommand.fromWireValue("volume.set", commandValue = 72),
        )
        assertEquals(
            ToggleVolumeMuteCommand,
            PanelCommand.fromWireValue("volume.toggle_mute"),
        )
    }

    @Test(expected = PanelProtocolException::class)
    fun setVolumeCommandRequiresAValue() {
        PanelCommand.fromWireValue("volume.set")
    }

    @Test
    fun volumeMessagesUseExactWireValues() {
        val state = VolumeStateMessage(
            available = true,
            volumePercent = 72,
            isMuted = false,
        )

        assertEquals("volume_state", state.type.wireValue)
        assertEquals("volume.set", SetVolumeCommand.WIRE_VALUE)
        assertEquals("volume.toggle_mute", ToggleVolumeMuteCommand.WIRE_VALUE)
        assertEquals(72, state.volumePercent)
    }

    @Test(expected = PanelProtocolException::class)
    fun unknownPanelCommandIsRejected() {
        PanelCommand.fromWireValue("desktop.unknown")
    }

    @Test
    fun desktopCommandsUseExactWireValuesAndParserRoundTrips() {
        listOf(
            DesktopCommand.START to "desktop.start",
            DesktopCommand.STOP to "desktop.stop",
        ).forEach { (command, wireValue) ->
            assertEquals(wireValue, command.wireValue)
            assertEquals(command, PanelCommand.fromWireValue(wireValue))
        }
    }

    @Test
    fun desktopStatesUseExactWireValuesAndParserRoundTrips() {
        val expectedWireValues = mapOf(
            DesktopStatus.STOPPED to "stopped",
            DesktopStatus.STARTING to "starting",
            DesktopStatus.RUNNING to "running",
            DesktopStatus.STOPPING to "stopping",
            DesktopStatus.ERROR to "error",
        )

        expectedWireValues.forEach { (status, wireValue) ->
            assertEquals(wireValue, status.toWireValue())
            assertEquals(status, desktopStatusFromWireValue(wireValue))
        }
        assertEquals("desktop_state", PanelMessageType.DESKTOP_STATE.wireValue)
    }

    @Test
    fun desktopErrorStatePreservesOptionalErrorCode() {
        val message = DesktopStateMessage(
            status = DesktopStatus.ERROR,
            errorCode = DesktopErrorCode.INTERNAL_ERROR,
        )

        assertEquals(DesktopStatus.ERROR, message.status)
        assertEquals(DesktopErrorCode.INTERNAL_ERROR, message.errorCode)
    }

    @Test
    fun touchCommandsUseAdditiveWireNamesNormalizedFieldsAndGeneration() {
        val down = TouchCommand(
            action = TouchCommandAction.DOWN,
            pointerId = 0,
            x = 0.42,
            y = 0.67,
            generation = 3,
        )
        val cancel = TouchCommand(
            action = TouchCommandAction.CANCEL,
            pointerId = 0,
            generation = 3,
        )

        assertEquals("touch.down", down.wireValue)
        assertEquals(0.42, down.x ?: Double.NaN, 0.0)
        assertEquals(0.67, down.y ?: Double.NaN, 0.0)
        assertEquals(3L, down.generation)
        assertEquals("touch.cancel", cancel.wireValue)
        assertNull(cancel.x)
        assertNull(cancel.y)
    }

    @Test
    fun desktopStateCarriesOptionalHostTouchGeneration() {
        val message = DesktopStateMessage(
            status = DesktopStatus.RUNNING,
            generation = 9,
        )

        assertEquals(9L, message.generation)
    }

    @Test
    fun legacyMediaStateDefaultsRemainCompatible() {
        val message = MediaStateMessage(
            title = "Legacy title",
            artist = "Legacy artist",
            isPlaying = true,
        )

        assertTrue(message.available)
        assertNull(message.mediaId)
        assertNull(message.sourceApp)
        assertNull(message.album)
        assertEquals(0, message.positionMs)
        assertEquals(0, message.durationMs)
        assertTrue(message.canPlayPause)
        assertTrue(message.canPrevious)
        assertTrue(message.canNext)
    }

    @Test
    fun telemetryDefaultsRepresentMissingHardwareWithoutInventingZeroes() {
        val message = TelemetryMessage()

        assertNull(message.cpu.name)
        assertNull(message.cpu.temperatureC)
        assertNull(message.cpu.usagePercent)
        assertNull(message.cpu.clockGhz)
        assertNull(message.cpu.powerW)
        assertNull(message.gpu.name)
        assertNull(message.gpu.temperatureC)
        assertNull(message.gpu.memoryUsedGb)
        assertNull(message.memory.usedGb)
        assertNull(message.memory.totalGb)
        assertNull(message.memory.usagePercent)
        assertTrue(message.storage.isEmpty())
    }
}
