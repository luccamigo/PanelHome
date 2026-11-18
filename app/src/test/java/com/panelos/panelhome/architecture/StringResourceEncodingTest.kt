package com.panelos.panelhome.architecture

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The loop media labels reached the tablet as "M?dia": a lossy conversion replaced the accented
 * letters with "?" in strings.xml. The XML is valid, so nothing but the screen showed it.
 */
class StringResourceEncodingTest {
    private val strings: String = Files.readString(stringsFile())

    @Test
    fun `loop media labels keep their accents`() {
        assertEquals("Mídia", value("loop_media_title"))
        assertEquals("Recebendo mídia… %1\$d%%", value("loop_media_receiving"))
        assertEquals("Não foi possível exibir a mídia", value("loop_media_error"))
    }

    @Test
    fun `no string has a question mark between two letters`() {
        // "?" replaces one accented letter; legitimate questions end a word or sentence.
        val offenders = Regex("""<string [^>]*>([^<]*)</string>""").findAll(strings)
            .map { it.groupValues[1] }
            .filter { Regex("""\p{L}\?\p{L}""").containsMatchIn(it) }
            .toList()
        assertTrue("Strings with a lossy '?' inside a word: $offenders", offenders.isEmpty())
    }

    private fun value(name: String): String =
        Regex("""<string name="$name">([^<]*)</string>""").find(strings)?.groupValues?.get(1)
            ?: error("String $name is missing")

    private fun stringsFile(): Path {
        val workingDirectory = Paths.get(System.getProperty("user.dir"))
        val moduleFile = workingDirectory.resolve("src/main/res/values/strings.xml")
        return if (Files.isRegularFile(moduleFile)) {
            moduleFile
        } else {
            workingDirectory.resolve("app/src/main/res/values/strings.xml")
        }
    }
}
