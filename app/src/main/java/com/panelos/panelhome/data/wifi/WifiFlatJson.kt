package com.panelos.panelhome.data.wifi

import org.json.JSONObject
import org.json.JSONTokener

/**
 * Strict reader for the flat objects of the pairing wire: one level, string values (and non-negative integers for the
 * names the caller lists), no duplicates, no unknown names, no missing names. The pairing messages are tiny and fixed,
 * so a grammar this small is easier to trust than a general JSON tree; string decoding is still done by org.json.
 */
internal object WifiFlatJson {
    /** [names] are required, [optional] may be present; anything else, a duplicate or a missing required name is refused. */
    fun read(text: String, names: Set<String>, numeric: Set<String> = emptySet(), optional: Set<String> = emptySet()): Map<String, String> {
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
            throw IllegalArgumentException("Unterminated JSON string")
        }
        val fields = LinkedHashMap<String, String>()
        take('{'); space()
        while (position < text.length && text[position] != '}') {
            val name = string(); require((name in names || name in optional) && name !in fields); take(':'); space()
            fields[name] = if (name in numeric) {
                val start = position
                while (position < text.length && text[position] in '0'..'9') position++
                text.substring(start, position).also { require(it.matches(Regex("0|[1-9][0-9]*"))) }
            } else string()
            space()
            if (position < text.length && text[position] == ',') {
                position++; space(); require(position < text.length && text[position] != '}')
            } else break
        }
        take('}'); space()
        require(position == text.length && fields.keys.containsAll(names))
        return fields
    }

    fun quote(value: String): String = JSONObject.quote(value)
}