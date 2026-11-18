package com.panelos.panelhome.data.wifi

import org.json.JSONTokener

/** Strict grammar for this flat QR object; JSON string decoding uses org.json. */
internal object WifiQrFields {
    fun read(text: String): Map<String, String> {
        var position = 0
        fun space() { while (position < text.length && text[position] in " \r\n\t") position++ }
        fun take(expected: Char) { space(); require(position < text.length && text[position++] == expected) }
        fun string(): String {
            space(); val start = position; take('"')
            while (position < text.length) {
                val value = text[position++]
                require(value >= ' ')
                if (value == '"') return JSONTokener(text.substring(start, position)).nextValue() as String
                if (value == '\\') {
                    require(position < text.length)
                    val escaped = text[position++]
                    require(escaped in "\"\\/bfnrtu")
                    if (escaped == 'u') repeat(4) {
                        require(position < text.length && text[position++] in "0123456789abcdefABCDEF")
                    }
                }
            }
            error("Unterminated JSON string")
        }
        val numeric = setOf("version", "pairPort", "sessionPort", "expiresAt")
        val names = numeric + setOf("type", "hostId", "rootCa", "ip", "pairId", "token")
        val fields = mutableMapOf<String, String>()
        take('{'); space()
        while (position < text.length && text[position] != '}') {
            val name = string(); require(name in names && name !in fields); take(':'); space()
            val value = if (name in numeric) {
                val start = position
                while (position < text.length && text[position] in '0'..'9') position++
                text.substring(start, position).also { require(it.matches(Regex("0|[1-9][0-9]*"))) }
            } else string()
            fields[name] = value; space()
            if (position < text.length && text[position] == ',') {
                position++; space(); require(position < text.length && text[position] != '}')
            } else break
        }
        take('}'); space(); require(position == text.length && fields.keys == names)
        return fields
    }
}
