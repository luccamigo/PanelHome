package com.panelos.panelhome.architecture

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import kotlin.io.path.extension
import kotlin.io.path.isDirectory
import kotlin.io.path.readText
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * PanelHome is the tablet's HOME app, so ending its process is not a recovery -
 * it is the home screen disappearing and an operator being asked to launch it
 * again.
 *
 * The app did exactly that for a while, and it worked: a fresh process was the
 * only thing that released the wedged accessory descriptor after a cable pull.
 * The descriptor is now released properly instead, by
 * `UsbLifecycleStateMachine` and `AccessoryHandle`, so the process no longer has
 * to die for a cable to be replugged. This test exists so that fix cannot be
 * quietly undone the next time a reconnect looks stuck.
 *
 * If a legitimate use ever appears, it belongs in tooling or a test - not in the
 * runtime this test scans - and it has to be documented in
 * `docs/native-usb-recovery.md` before this list is widened.
 */
class NoProcessExitTest {
    @Test
    fun `the Android runtime never ends its own process`() {
        val offenders = mutableListOf<String>()
        Files.walk(sourceDirectory()).use { paths ->
            paths
                .filter { !it.isDirectory() && it.extension == "kt" }
                .forEach { source ->
                    val relative = sourceDirectory().relativize(source).toString()
                    source.readText().lineSequence().forEachIndexed { index, line ->
                        val code = line.substringBefore("//")
                        FORBIDDEN.firstOrNull { it in code }?.let { found ->
                            offenders += "$relative:${index + 1} -> $found"
                        }
                    }
                }
        }
        assertTrue(
            "PanelHome is the HOME app; ending the process is not a USB recovery. " +
                "Found: $offenders",
            offenders.isEmpty(),
        )
    }

    private fun sourceDirectory(): Path {
        val workingDirectory = Paths.get(System.getProperty("user.dir"))
        val moduleSource = workingDirectory.resolve("src/main/java")
        return if (Files.isDirectory(moduleSource)) {
            moduleSource
        } else {
            workingDirectory.resolve("app/src/main/java")
        }
    }

    private companion object {
        val FORBIDDEN = listOf(
            "Runtime.getRuntime().exit",
            "Runtime.getRuntime().halt",
            "System.exit",
            "exitProcess",
            "killProcess",
            "finishAffinity",
            "finishAndRemoveTask",
        )
    }
}
