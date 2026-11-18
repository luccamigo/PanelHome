package com.panelos.panelhome.data.wifi

import java.io.File
import org.json.JSONObject

/** Reads the shared corpus in protocol-fixtures/wifi, walking up from the test working directory. */
internal object WifiFixtures {
    fun json(name: String): JSONObject {
        var root: File? = File(System.getProperty("user.dir")!!)
        while (root != null && !File(root, "protocol-fixtures/wifi/$name").exists()) root = root.parentFile
        return JSONObject(File(root ?: error("fixture $name not found"), "protocol-fixtures/wifi/$name").readText())
    }

    fun hex(text: String): ByteArray = ByteArray(text.length / 2) { text.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}