package com.panelos.panelhome.ui.wifi

/**
 * The typed pairing code as the tablet's field shows it: 100 Base32 characters, uppercase, in groups of five. Separators
 * (space, dash, line breaks) are accepted and dropped so a pasted or dictated code works; any other character outside
 * A-Z and 2-7 is reported at once instead of at submit.
 */
internal object WifiCodeInput {
    const val LENGTH = 100
    const val GROUP = 5
    private const val SEPARATORS = "- \t\r\n"

    fun clean(raw: String): String = raw.filter { it !in SEPARATORS }.uppercase().take(LENGTH)

    fun invalidCharacters(clean: String): List<Char> = clean.filter { it !in 'A'..'Z' && it !in '2'..'7' }.toList().distinct()

    fun format(clean: String): String = clean.take(LENGTH).chunked(GROUP).joinToString(" ")

    fun isComplete(clean: String): Boolean = clean.length == LENGTH && invalidCharacters(clean).isEmpty()
}