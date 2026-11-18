using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.Management;

namespace PanelHost.Tests;

/// <summary>
/// The fake-file installer tests cannot see a hook that installs fine and then dies on start. These tests install the
/// REAL build with the REAL installer into an isolated folder and execute the installed exe from there, so a missing
/// dependency (the P2 copy lists only exe/dll/deps/runtimeconfig) fails here and not on the user's first permission request.
/// </summary>
[TestClass]
public sealed class ClaudeHookDeploymentTests
{
    private static string BuiltHook()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var configuration = Path.GetFileName(Path.GetDirectoryName(baseDirectory))!;
        var windows = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", ".."));
        var exe = Path.Combine(windows, "PanelOS.ClaudeHook", "bin", configuration, "net8.0", "PanelOS.ClaudeHook.exe");
        Assert.IsTrue(File.Exists(exe), "Hook não foi compilado: " + exe);
        return exe;
    }

    private static (int Exit, string Out, string Err) RunInstalled(string exe, string stdin)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        using var process = Process.Start(start)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.IsTrue(process.WaitForExit(20000), "O hook instalado não terminou.");
        return (process.ExitCode, output.Result, error.Result);
    }

    private static void WithInstalledHook(Action<string, string> test)
    {
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PanelOS-HookDeployTest-"));
        var directory = prefix + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        try
        {
            var stable = Path.Combine(directory, "isolated-hook");
            var installer = new ClaudeHookInstaller(Path.Combine(directory, "settings.json"), BuiltHook(), stable);
            installer.Install(installer.Preview());
            test(stable, Path.Combine(stable, "PanelOS.ClaudeHook.exe"));
        }
        finally
        {
            Assert.IsTrue(Path.GetFullPath(directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void Installed_hook_folder_contains_every_runtime_asset_its_deps_json_names()
    {
        WithInstalledHook((stable, _) =>
        {
            using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(stable, "PanelOS.ClaudeHook.deps.json")));
            var missing = new List<string>();
            foreach (var target in deps.RootElement.GetProperty("targets").EnumerateObject())
                foreach (var library in target.Value.EnumerateObject())
                    foreach (var section in new[] { "runtime", "native" })
                        if (library.Value.TryGetProperty(section, out var assets))
                            foreach (var asset in assets.EnumerateObject())
                                if (!File.Exists(Path.Combine(stable, Path.GetFileName(asset.Name)))) missing.Add(asset.Name);
            Assert.AreEqual(0, missing.Count, "Faltam na pasta instalada: " + string.Join(", ", missing));
        });
    }

    [TestMethod]
    public void Installed_hook_starts_from_the_isolated_folder_and_leaves_the_decision_to_the_terminal()
    {
        WithInstalledHook((_, exe) =>
        {
            // Events/tools the hook must not answer: no pipe is opened, so no real request can reach a running PanelHost.
            foreach (var input in new[]
            {
                """{"hook_event_name":"PreToolUse","tool_name":"Bash","cwd":"C:\fake","tool_input":{"command":"echo teste"}}""",
                """{"hook_event_name":"PermissionRequest","tool_name":"AskUserQuestion","cwd":"C:\fake","tool_input":{}}""",
                "not json",
            })
            {
                var (exit, output, error) = RunInstalled(exe, input);
                Assert.AreEqual(0, exit, "O hook instalado falhou ao iniciar: " + error);
                Assert.AreEqual("", output);
                Assert.AreEqual("", error.Trim());
            }
        });
    }
}
