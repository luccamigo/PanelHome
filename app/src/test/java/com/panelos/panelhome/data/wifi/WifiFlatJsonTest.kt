package com.panelos.panelhome.data.wifi

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class WifiFlatJsonTest {
    private val names = setOf("type", "status")

    @Test fun readsExactlyTheExpectedStrings() {
        assertEquals(mapOf("type" to "pair_result", "status" to "approved"),
            WifiFlatJson.read("""{"type":"pair_result","status":"approved"}""", names))
        assertEquals("é\n", WifiFlatJson.read("""{"type":"é\n","status":"x"}""", names)["type"])
    }

    @Test fun numericNamesAreCanonicalIntegers() {
        val read = WifiFlatJson.read("""{"type":"a","status":42}""", names, numeric = setOf("status"))
        assertEquals("42", read["status"])
        for (bad in listOf("007", "-1", "1.5", "1e3", "\"42\""))
            assertThrows(bad, IllegalArgumentException::class.java) { WifiFlatJson.read("""{"type":"a","status":$bad}""", names, setOf("status")) }
    }

    @Test fun refusesDuplicatesUnknownMissingNestedAndMalformed() {
        val bad = listOf(
            """{"type":"a","type":"b","status":"c"}""", """{"type":"a","status":"c","extra":"d"}""", """{"type":"a"}""",
            """{"type":{"x":"y"},"status":"c"}""", """{"type":["a"],"status":"c"}""", """{"type":"a","status":"c",}""",
            """{"type":"a","status":"c"} trailing""", """{"type":"a","status":"\q"}""", """{"type":"a","status":"\u12"}""",
            """{"type":"a","status":"unterminated}""", "", "[]", """{"type":"a","status":null}""",
        )
        for (text in bad) assertThrows(text, IllegalArgumentException::class.java) { WifiFlatJson.read(text, names) }
    }

    @Test fun quoteEscapesWhatAnUntrustedLabelCouldContain() {
        val quoted = WifiFlatJson.quote("Tablet \"x\"\n\\")
        assertEquals("Tablet \"x\"\n\\", WifiFlatJson.read("""{"type":$quoted,"status":"s"}""", names)["type"])
    }
}