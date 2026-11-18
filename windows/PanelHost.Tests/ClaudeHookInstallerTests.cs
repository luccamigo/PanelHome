using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.Management;

namespace PanelHost.Tests;

[TestClass]
public sealed class ClaudeHookInstallerTests
{
    private static readonly string[] HookNames = ["PanelOS.ClaudeHook.exe", "PanelOS.ClaudeHook.dll", "PanelOS.ClaudeHook.deps.json", "PanelOS.ClaudeHook.runtimeconfig.json"];

    /// <summary>The "build folder" holds fake hook files; the stable folder is where the installer must copy them.</summary>
    private static (ClaudeHookInstaller Installer, string Settings, string Source, string Stable) Create(string directory, string name = "settings.json")
    {
        var source = Path.Combine(directory, "build-output");
        Directory.CreateDirectory(source);
        foreach (var file in HookNames) File.WriteAllText(Path.Combine(source, file), "build-1 " + file);
        var stable = Path.Combine(directory, "stable-hook");
        var settings = Path.Combine(directory, name);
        return (new ClaudeHookInstaller(settings, Path.Combine(source, HookNames[0]), stable), settings, source, stable);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [TestMethod]
    public void Install_preserves_existing_hooks_previews_exact_text_and_removal_restores_original_bytes()
    {
        WithDirectory(directory =>
        {
            var (installer, path, _, stable) = Create(directory);
            var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\r\n \"env\":{\"A\":\"B\"},\"hooks\":{\"PermissionRequest\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"other\"}]}]}}\r\n")).ToArray();
            File.WriteAllBytes(path, original);
            var preview = installer.Preview();
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            Assert.IsFalse(Directory.Exists(stable), "A preview never writes the stable folder.");
            installer.Install(preview);
            Assert.AreEqual(preview.Text, File.ReadAllText(path));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(preview.BackupPath));
            Assert.IsTrue(installer.Status().Installed);
            Assert.IsFalse(installer.Status().Changed);
            StringAssert.Contains(preview.Text, "other");
            installer.Remove(installer.PreviewRemoval());
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            Assert.IsTrue(File.Exists(preview.BackupPath));
        });
    }

    [TestMethod]
    public void Changed_settings_and_missing_backup_remove_only_panelos_entry()
    {
        foreach (var deleteBackup in new[] { false, true }) WithDirectory(directory =>
        {
            var (installer, path, _, _) = Create(directory);
            var preview = installer.Preview(); installer.Install(preview);
            var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            settings["newPreference"] = "keep";
            File.WriteAllText(path, settings.ToJsonString());
            if (deleteBackup) File.Delete(preview.BackupPath);
            Assert.AreEqual("remove_entry", installer.PreviewRemoval().Mode);
            installer.Remove(installer.PreviewRemoval());
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual("keep", json.RootElement.GetProperty("newPreference").GetString());
            Assert.IsFalse(json.RootElement.TryGetProperty("hooks", out _));
        });
    }

    [TestMethod]
    public void Invalid_json_and_stale_preview_write_nothing()
    {
        WithDirectory(directory =>
        {
            var (installer, path, _, stable) = Create(directory);
            File.WriteAllText(path, "invalid");
            try { installer.Preview(); Assert.Fail("Invalid JSON accepted"); } catch (JsonException) { }
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
            File.WriteAllText(path, "{}");
            var preview = installer.Preview();
            File.WriteAllText(path, "{\"changed\":true}");
            Assert.ThrowsException<IOException>(() => installer.Install(preview));
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
            Assert.AreEqual("{\"changed\":true}", File.ReadAllText(path));
            Assert.IsFalse(Directory.Exists(stable));
        });
    }

    [TestMethod]
    public void The_installed_command_points_at_the_stable_copy_with_hashes_shown_in_the_preview_never_at_the_build_folder()
    {
        WithDirectory(directory =>
        {
            var (installer, path, source, stable) = Create(directory);
            var preview = installer.Preview();
            var stableExe = Path.Combine(stable, HookNames[0]);
            Assert.AreEqual("\"" + stableExe + "\"", preview.Command);
            Assert.AreEqual(stable, preview.InstallDirectory);
            Assert.AreEqual(4, preview.Files!.Count);
            foreach (var file in preview.Files) Assert.AreEqual(Sha(Path.Combine(source, file.Name)), file.Sha256);
            using (var json = JsonDocument.Parse(preview.Text))
                Assert.AreEqual(preview.Command, json.RootElement.GetProperty("hooks").GetProperty("PermissionRequest")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
            Assert.IsFalse(preview.Text.Contains("build-output"), "The preview text must not reference the build folder.");
            installer.Install(preview);
            Assert.IsFalse(File.ReadAllText(path).Contains("build-output"));
            foreach (var file in preview.Files) Assert.AreEqual(file.Sha256, Sha(Path.Combine(stable, file.Name)));
            Assert.IsFalse(installer.Status().HookFilesChanged);
            Assert.IsFalse(installer.Status().Outdated);
        });
    }

    [TestMethod]
    public void A_rebuild_after_install_changes_nothing_that_runs_and_is_reported_as_outdated()
    {
        WithDirectory(directory =>
        {
            var (installer, path, source, stable) = Create(directory);
            var preview = installer.Preview(); installer.Install(preview);
            var before = preview.Files!.ToDictionary(x => x.Name, x => x.Sha256);
            var settingsBefore = File.ReadAllBytes(path);
            foreach (var file in HookNames) File.WriteAllText(Path.Combine(source, file), "build-2 " + file); // rebuild or branch switch
            foreach (var file in HookNames) Assert.AreEqual(before[file], Sha(Path.Combine(stable, file)));
            CollectionAssert.AreEqual(settingsBefore, File.ReadAllBytes(path));
            var status = installer.Status();
            Assert.IsTrue(status.Outdated);
            Assert.IsFalse(status.HookFilesChanged);
            Assert.IsFalse(status.Changed);
        });
    }

    [TestMethod]
    public void A_build_that_changes_between_preview_and_install_is_refused_and_nothing_is_written()
    {
        WithDirectory(directory =>
        {
            var (installer, path, source, stable) = Create(directory);
            File.WriteAllText(path, "{}");
            var preview = installer.Preview();
            File.WriteAllText(Path.Combine(source, HookNames[1]), "swapped after the preview");
            var error = Assert.ThrowsException<IOException>(() => installer.Install(preview));
            StringAssert.Contains(error.Message, HookNames[1]);
            Assert.AreEqual("{}", File.ReadAllText(path));
            Assert.IsFalse(Directory.Exists(stable));
            Assert.AreEqual(1, Directory.GetFiles(directory).Length, "No backup and no manifest after a refused install.");
        });
    }

    [TestMethod]
    public void A_tampered_copy_is_reported_and_removal_keeps_anything_it_did_not_write()
    {
        WithDirectory(directory =>
        {
            var (installer, _, _, stable) = Create(directory);
            var preview = installer.Preview(); installer.Install(preview);
            File.WriteAllText(Path.Combine(stable, HookNames[1]), "tampered");
            File.WriteAllText(Path.Combine(stable, "unrelated.txt"), "mine");
            Assert.IsTrue(installer.Status().HookFilesChanged);
            installer.Remove(installer.PreviewRemoval());
            Assert.IsFalse(File.Exists(Path.Combine(stable, HookNames[0])), "Unchanged copies are removed.");
            Assert.IsTrue(File.Exists(Path.Combine(stable, HookNames[1])), "A copy that was changed after install is not deleted.");
            Assert.IsTrue(File.Exists(Path.Combine(stable, "unrelated.txt")));
        });
    }

    [TestMethod]
    public void Removal_deletes_the_copies_and_the_empty_stable_folder()
    {
        WithDirectory(directory =>
        {
            var (installer, _, _, stable) = Create(directory);
            installer.Install(installer.Preview());
            Assert.AreEqual(4, Directory.GetFiles(stable).Length);
            installer.Remove(installer.PreviewRemoval());
            Assert.IsFalse(Directory.Exists(stable));
            Assert.IsFalse(installer.Status().Installed);
        });
    }

    [TestMethod]
    public void A_missing_hook_file_stops_the_preview_and_the_default_stable_folder_is_under_local_app_data()
    {
        WithDirectory(directory =>
        {
            var (installer, _, source, stable) = Create(directory);
            File.Delete(Path.Combine(source, HookNames[3]));
            var error = Assert.ThrowsException<FileNotFoundException>(() => installer.Preview());
            StringAssert.Contains(error.Message, HookNames[3]);
            Assert.IsFalse(Directory.Exists(stable));
        });
        var defaults = new ClaudeHookInstaller(Path.Combine(Path.GetTempPath(), "never-written.json"), Path.Combine(Path.GetTempPath(), "PanelOS.ClaudeHook.exe"));
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanelOS", "hook"), defaults.InstallDirectory);
    }

    private static void WithDirectory(Action<string> test)
    {
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PanelOS-HookInstallerTest-"));
        var directory = prefix + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally
        {
            Assert.IsTrue(Path.GetFullPath(directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            Directory.Delete(directory, true);
        }
    }
}
