package com.panelos.panelhome.domain.protocol

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class StreamDeckProtocolJsonTest {
    @Test
    fun theClientAnnouncesTheStreamDeckFeature() {
        assertTrue(PanelProtocol.FEATURE_STREAM_DECK in PanelProtocol.CLIENT_FEATURES)
        assertEquals("stream_deck", PanelProtocol.FEATURE_STREAM_DECK)
    }

    @Test
    fun aLayoutIsDecodedWithItsProfileAndPages() {
        val decoded = PanelProtocolJson.decode(
            """
            {"type":"streamdeck.layout","revision":"ab12",
             "profile":{"id":"profile-main","name":"Principal"},
             "pages":[{"id":"page-main","name":"Principal","rows":3,"columns":4,
               "buttons":[{"id":"button-1","row":0,"column":0,"label":"Spotify",
                 "iconId":"icon-aaaaaaaaaa","enabled":true}]}]}
            """.trimIndent().replace("\n", ""),
        ) as StreamDeckLayoutMessage

        assertEquals("ab12", decoded.revision)
        assertEquals("profile-main", decoded.profileId)
        assertEquals("Principal", decoded.profileName)
        assertEquals(1, decoded.pages.size)
        assertEquals(3, decoded.pages[0].rows)
        assertEquals(4, decoded.pages[0].columns)

        val button = decoded.pages[0].buttons.single()
        assertEquals("button-1", button.id)
        assertEquals("Spotify", button.label)
        assertEquals("icon-aaaaaaaaaa", button.iconId)
        assertNull(button.glyph)
        assertTrue(button.enabled)
    }

    @Test
    fun aLayoutWithoutAProfileIsAnEmptyConfigurationAndNotAnError() {
        val decoded = PanelProtocolJson.decode(
            """{"type":"streamdeck.layout","revision":"0","pages":[]}""",
        ) as StreamDeckLayoutMessage

        assertNull(decoded.profileId)
        assertTrue(decoded.pages.isEmpty())
    }

    @Test
    fun aButtonWithoutAnEnabledFlagIsTreatedAsUnavailable() {
        val decoded = PanelProtocolJson.decode(
            """
            {"type":"streamdeck.layout","revision":"r","pages":[
              {"id":"p","name":"P","rows":2,"columns":2,
               "buttons":[{"id":"b","row":0,"column":0,"label":"X"}]}]}
            """.trimIndent().replace("\n", ""),
        ) as StreamDeckLayoutMessage

        // Absence must never mean "runnable": that would draw a button the PC
        // has already said it cannot run.
        assertFalse(decoded.pages[0].buttons.single().enabled)
    }

    @Test
    fun anInvocationCarriesOnlyTheButtonIdAndTheRevision() {
        val json = JSONObject(
            PanelProtocolJson.encode(
                CommandMessage(
                    StreamDeckInvokeCommand(buttonId = "button-1", revision = "ab12"),
                ),
            ),
        )

        assertEquals("command", json.getString("type"))
        assertEquals("streamdeck.invoke", json.getString("command"))
        assertEquals("button-1", json.getString("buttonId"))
        assertEquals("ab12", json.getString("revision"))
        assertEquals(
            setOf("type", "command", "buttonId", "revision"),
            json.keys().asSequence().toSet(),
        )
    }

    @Test
    fun anIconRequestCarriesOnlyTheMissingIds() {
        val json = JSONObject(
            PanelProtocolJson.encode(
                CommandMessage(
                    StreamDeckIconsRequestCommand(
                        listOf("icon-aaaaaaaaaa", "icon-bbbbbbbbbb"),
                    ),
                ),
            ),
        )

        assertEquals("streamdeck.icons.request", json.getString("command"))
        assertEquals(2, json.getJSONArray("iconIds").length())
        assertEquals("icon-aaaaaaaaaa", json.getJSONArray("iconIds").getString(0))
    }

    @Test
    fun anInvokeResultCarriesTheOutcomeByCode() {
        val success = PanelProtocolJson.decode(
            """{"type":"streamdeck.invoke_result","buttonId":"button-1","success":true}""",
        ) as StreamDeckInvokeResultMessage
        assertTrue(success.success)
        assertNull(success.errorCode)

        val failure = PanelProtocolJson.decode(
            """
            {"type":"streamdeck.invoke_result","buttonId":"button-1","success":false,
             "errorCode":"application_not_found","message":"..."}
            """.trimIndent().replace("\n", ""),
        ) as StreamDeckInvokeResultMessage
        assertFalse(failure.success)
        assertEquals(StreamDeckErrorCode.APPLICATION_NOT_FOUND, failure.errorCode)
    }

    @Test
    fun anIconMessageIsDecodedWithItsTransportEncoding() {
        val decoded = PanelProtocolJson.decode(
            """
            {"type":"streamdeck.icon","iconId":"icon-aaaaaaaaaa","available":true,
             "mime":"image/png","data":"iVBORw0KGgo="}
            """.trimIndent().replace("\n", ""),
        ) as StreamDeckIconMessage

        assertEquals("icon-aaaaaaaaaa", decoded.iconId)
        assertTrue(decoded.available)
        assertEquals("image/png", decoded.mime)
        assertEquals("iVBORw0KGgo=", decoded.dataBase64)
    }

    @Test
    fun everyStreamDeckMessageSurvivesARoundTrip() {
        val layout = StreamDeckLayoutMessage(
            revision = "ab12",
            profileId = "profile-main",
            profileName = "Principal",
            pages = listOf(
                StreamDeckPageMessage(
                    id = "page-main",
                    name = "Principal",
                    rows = 3,
                    columns = 4,
                    buttons = listOf(
                        StreamDeckButtonMessage(
                            id = "button-1",
                            row = 0,
                            column = 0,
                            label = "Spotify",
                            iconId = "icon-aaaaaaaaaa",
                        ),
                        StreamDeckButtonMessage(
                            id = "button-2",
                            row = 1,
                            column = 2,
                            label = "Mute",
                            glyph = "volume-mute",
                            enabled = false,
                        ),
                    ),
                ),
            ),
        )

        assertEquals(layout, PanelProtocolJson.decode(PanelProtocolJson.encode(layout)))
    }

    @Test(expected = PanelProtocolException::class)
    fun aLayoutWithTooManyPagesIsRejectedBeforeModelCreation() {
        val pages = (0..PanelProtocolLimits.MAX_PAGES_PER_PROFILE).joinToString(",") { index ->
            """{"id":"p$index","name":"P","rows":2,"columns":2,"buttons":[]}"""
        }
        PanelProtocolJson.decode(
            """{"type":"streamdeck.layout","revision":"r","pages":[$pages]}""",
        )
    }

    @Test(expected = PanelProtocolException::class)
    fun anOversizedLabelIsRejected() {
        val label = "x".repeat(PanelProtocolLimits.MAX_LABEL_LENGTH + 1)
        PanelProtocolJson.decode(
            """{"type":"streamdeck.layout","revision":"r","pages":[{"id":"p","name":"P","rows":2,"columns":2,"buttons":[{"id":"b","row":0,"column":0,"label":"$label","enabled":true}]}]}""",
        )
    }
}
