package com.panelos.panelhome.ui.desktop

import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.DesktopStatus
import com.panelos.panelhome.domain.protocol.DesktopErrorCode
import org.junit.Assert.assertEquals
import org.junit.Test

class DesktopScreenErrorMappingTest {
    @Test
    fun driverErrorsUseSpecificFriendlyMessages() {
        val expectedResources = mapOf(
            DesktopErrorCode.DRIVER_NOT_FOUND to R.string.desktop_error_driver_not_found,
            DesktopErrorCode.DRIVER_ACCESS_DENIED to R.string.desktop_error_driver_access_denied,
            DesktopErrorCode.DRIVER_NOT_READY to R.string.desktop_error_driver_not_ready,
            DesktopErrorCode.DRIVER_IO_ERROR to R.string.desktop_error_driver_io_error,
        )

        expectedResources.forEach { (errorCode, expectedResource) ->
            assertEquals(expectedResource, DesktopStatus.ERROR.messageResource(errorCode))
        }
    }

    @Test
    fun unknownDriverErrorKeepsGenericFallback() {
        assertEquals(
            R.string.desktop_error,
            DesktopStatus.ERROR.messageResource("unknown_driver_error"),
        )
    }
}
