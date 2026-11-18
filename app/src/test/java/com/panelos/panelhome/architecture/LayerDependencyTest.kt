package com.panelos.panelhome.architecture

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import kotlin.io.path.extension
import kotlin.io.path.isDirectory
import kotlin.io.path.readText
import org.junit.Assert.assertTrue
import org.junit.Test

class LayerDependencyTest {
    @Test
    fun `desktop UI does not import data layer`() {
        assertNoImports(
            sourceDirectory().resolve("com/panelos/panelhome/ui/desktop"),
            "com.panelos.panelhome.data.",
        )
    }

    @Test
    fun `presentation does not import UI layer`() {
        assertNoImports(
            sourceDirectory().resolve("com/panelos/panelhome/presentation"),
            "com.panelos.panelhome.ui.",
        )
    }

    private fun assertNoImports(directory: Path, forbiddenPackage: String) {
        val violations = Files.walk(directory).use { paths ->
            paths
                .filter { !it.isDirectory() && it.extension == "kt" }
                .filter { source ->
                    source.readText().lineSequence().any {
                        it.trimStart().startsWith("import $forbiddenPackage")
                    }
                }
                .map(directory::relativize)
                .toList()
        }
        assertTrue(
            "Forbidden imports of $forbiddenPackage found in: $violations",
            violations.isEmpty(),
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
}
