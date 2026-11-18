using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PanelOS.Windows.Management;

/// <summary>One file the installer copies into the stable hook folder, with the SHA-256 shown in the preview.</summary>
public sealed record ClaudeHookFilePreview(string Name, string Sha256, long Length);
public sealed record ClaudeHookInstallPreview(string SettingsPath, string BackupPath, string Text,
    string ExpectedHash, bool Existed, string Command, string InstallDirectory = "",
    IReadOnlyList<ClaudeHookFilePreview>? Files = null);
/// <param name="Installed">The manifest exists.</param>
/// <param name="Changed">settings.json differs from what the installer wrote.</param>
/// <param name="HookFilesChanged">A copied hook file is missing or differs from the previewed hash.</param>
/// <param name="Outdated">The build next to the Control Center differs from the installed copy; reinstall to update.</param>
public sealed record ClaudeHookStatus(bool Installed, bool Changed, string? BackupPath, bool HookFilesChanged = false, bool Outdated = false);
public sealed record ClaudeHookRemovalPreview(string Mode, string? BackupPath);

/// <summary>
/// Only explicit Install/Remove calls write. Callers can inspect the exact preview first.
/// settings.json points at a stable copy of the hook (default %LOCALAPPDATA%\PanelOS\hook), never at the build folder,
/// so a rebuild or branch switch cannot change what runs on every permission request (P2).
/// </summary>
public sealed class ClaudeHookInstaller(string settingsPath, string hookExecutable, string? installDirectory = null)
{
    public string SettingsPath { get; } = Path.GetFullPath(settingsPath);
    /// <summary>Where the hook is built or shipped; only read, never executed from settings.json.</summary>
    private readonly string _source = Path.GetFullPath(hookExecutable);
    public string InstallDirectory { get; } = Path.GetFullPath(installDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanelOS", "hook"));
    private string Stable => Path.Combine(InstallDirectory, Path.GetFileName(_source));
    private string ManifestPath => SettingsPath + ".panelos-hook.json";
    private sealed record Manifest(bool Existed, string BackupPath, string InstalledHash, string Command,
        Dictionary<string, string>? Files = null, string? InstallDirectory = null);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private string Command => "\"" + Stable + "\"";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // .NET apphost: the exe alone does not run; it needs its dll and the two json files next to it.
    private IReadOnlyList<string> FileNames
    {
        get
        {
            var stem = Path.GetFileNameWithoutExtension(_source);
            return [stem + ".exe", stem + ".dll", stem + ".deps.json", stem + ".runtimeconfig.json"];
        }
    }

    private IReadOnlyList<ClaudeHookFilePreview> SourceFiles()
    {
        var folder = Path.GetDirectoryName(_source)!;
        var files = new List<ClaudeHookFilePreview>();
        foreach (var name in FileNames)
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) throw new FileNotFoundException("Arquivo do hook não encontrado ao lado de " + _source + ": " + name, path);
            var bytes = File.ReadAllBytes(path);
            files.Add(new(name, Hash(bytes), bytes.Length));
        }
        return files;
    }

    public ClaudeHookInstallPreview Preview()
    {
        if (File.Exists(ManifestPath)) throw new InvalidOperationException("O hook já está instalado. Remova antes de reinstalar.");
        if (Stable.Contains('"') || Stable.Contains('\n') || _source.Contains('\n')) throw new ArgumentException("Invalid hook path");
        var files = SourceFiles();
        var existed = File.Exists(SettingsPath);
        var before = existed ? File.ReadAllBytes(SettingsPath) : [];
        var root = ReadRoot(before);
        var hooks = root["hooks"] as JsonObject;
        if (root["hooks"] is not null && hooks is null) throw new InvalidDataException("hooks deve ser um objeto JSON.");
        if (hooks is null) root["hooks"] = hooks = new JsonObject();
        var requests = hooks["PermissionRequest"] as JsonArray;
        if (hooks["PermissionRequest"] is not null && requests is null) throw new InvalidDataException("PermissionRequest deve ser uma lista.");
        if (requests is null) hooks["PermissionRequest"] = requests = new JsonArray();
        requests.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject {
            ["type"] = "command", ["command"] = Command, ["timeout"] = 130 }) });
        var backup = SettingsPath + ".panelos-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
        return new(SettingsPath, backup, root.ToJsonString(JsonOptions) + "\n", Hash(before), existed, Command, InstallDirectory, files);
    }

    public void Install(ClaudeHookInstallPreview preview)
    {
        if (preview.SettingsPath != SettingsPath || preview.Command != Command || !ValidBackup(preview.BackupPath) || File.Exists(ManifestPath) ||
            preview.InstallDirectory != InstallDirectory || preview.Files is null)
            throw new InvalidOperationException("A prévia não corresponde a esta instalação.");
        var existed = File.Exists(SettingsPath);
        var original = existed ? File.ReadAllBytes(SettingsPath) : [];
        if (existed != preview.Existed || Hash(original) != preview.ExpectedHash) throw new IOException("O arquivo mudou. Confira uma nova prévia antes de instalar.");
        // Rebuild the expected content instead of accepting arbitrary text from a stale/mutated preview.
        if (Preview().Text != preview.Text) throw new IOException("A prévia mudou.");
        var installed = new UTF8Encoding(false).GetBytes(preview.Text);
        // The bytes copied are the bytes whose hash the user saw: read once, compare with the preview, write, read back.
        var payload = new List<(string Name, byte[] Bytes)>();
        var folder = Path.GetDirectoryName(_source)!;
        if (preview.Files.Count != FileNames.Count) throw new IOException("A prévia dos arquivos do hook mudou.");
        foreach (var file in preview.Files)
        {
            if (!FileNames.Contains(file.Name)) throw new IOException("A prévia dos arquivos do hook mudou.");
            var bytes = File.ReadAllBytes(Path.Combine(folder, file.Name));
            if (!string.Equals(Hash(bytes), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("O hook mudou desde a prévia: " + file.Name + ". Confira uma nova prévia.");
            payload.Add((file.Name, bytes));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        using (var backup = new FileStream(preview.BackupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) backup.Write(original);
        var copied = new List<string>();
        try
        {
            Directory.CreateDirectory(InstallDirectory);
            foreach (var (name, bytes) in payload)
            {
                var target = Path.Combine(InstallDirectory, name);
                AtomicWrite(target, bytes);
                copied.Add(target);
                if (Hash(File.ReadAllBytes(target)) != Hash(bytes)) throw new IOException("A cópia do hook não confere: " + name);
            }
            var files = preview.Files.ToDictionary(x => x.Name, x => x.Sha256.ToUpperInvariant());
            AtomicWrite(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(new Manifest(existed, preview.BackupPath, Hash(installed), Command, files, InstallDirectory)));
            AtomicWrite(SettingsPath, installed);
        }
        catch
        {
            // Nothing half-installed: settings.json was not touched if we got here before its write; undo copies and manifest.
            if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
            RemoveCopies(copied);
            throw;
        }
    }

    private void RemoveCopies(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { if (Directory.Exists(InstallDirectory) && !Directory.EnumerateFileSystemEntries(InstallDirectory).Any()) Directory.Delete(InstallDirectory); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public ClaudeHookStatus Status()
    {
        if (!File.Exists(ManifestPath)) return new(false, false, null);
        var manifest = ReadManifest();
        var filesChanged = false;
        var outdated = false;
        foreach (var (name, hash) in manifest.Files ?? [])
        {
            var installedPath = Path.Combine(InstallDirectory, name);
            if (!File.Exists(installedPath) || Hash(File.ReadAllBytes(installedPath)) != hash) filesChanged = true;
            var sourcePath = Path.Combine(Path.GetDirectoryName(_source)!, name);
            if (!File.Exists(sourcePath) || Hash(File.ReadAllBytes(sourcePath)) != hash) outdated = true;
        }
        return new(true, !File.Exists(SettingsPath) || Hash(File.ReadAllBytes(SettingsPath)) != manifest.InstalledHash, manifest.BackupPath,
            filesChanged, outdated);
    }

    public ClaudeHookRemovalPreview PreviewRemoval()
    {
        var manifest = ReadManifest();
        var same = File.Exists(SettingsPath) && Hash(File.ReadAllBytes(SettingsPath)) == manifest.InstalledHash;
        return new(same && File.Exists(manifest.BackupPath) ? "restore_backup" : "remove_entry", manifest.BackupPath);
    }

    public void Remove(ClaudeHookRemovalPreview preview)
    {
        var manifest = ReadManifest();
        var actual = PreviewRemoval();
        if (preview != actual) throw new IOException("O arquivo mudou. Confira novamente antes de remover.");
        if (actual.Mode == "restore_backup")
        {
            if (manifest.Existed) AtomicWrite(SettingsPath, File.ReadAllBytes(manifest.BackupPath));
            else if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
        }
        else if (File.Exists(SettingsPath))
        {
            var root = ReadRoot(File.ReadAllBytes(SettingsPath));
            if (root["hooks"] is JsonObject hooks && hooks["PermissionRequest"] is JsonArray requests)
            {
                foreach (var group in requests.OfType<JsonObject>().ToArray())
                {
                    if (group["hooks"] is not JsonArray handlers) continue;
                    foreach (var handler in handlers.OfType<JsonObject>().ToArray())
                        if (handler["type"]?.GetValue<string>() == "command" && handler["command"]?.GetValue<string>() == manifest.Command) handlers.Remove(handler);
                    if (handlers.Count == 0) requests.Remove(group);
                }
                if (requests.Count == 0) hooks.Remove("PermissionRequest");
                if (hooks.Count == 0) root.Remove("hooks");
            }
            AtomicWrite(SettingsPath, Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions) + "\n"));
        }
        File.Delete(ManifestPath);
        // Delete only the copies this installer wrote and that are still unchanged; anything else in the folder stays.
        RemoveCopies((manifest.Files ?? []).Where(x => FileNames.Contains(x.Key) && File.Exists(Path.Combine(InstallDirectory, x.Key)) &&
            Hash(File.ReadAllBytes(Path.Combine(InstallDirectory, x.Key))) == x.Value).Select(x => Path.Combine(InstallDirectory, x.Key)));
        // Backups are retained for the user's inspection; removal never deletes them.
    }

    private Manifest ReadManifest()
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(ManifestPath)) ?? throw new InvalidDataException();
        if (!ValidBackup(manifest.BackupPath) || manifest.Command != Command ||
            (manifest.InstallDirectory is not null && manifest.InstallDirectory != InstallDirectory)) throw new InvalidDataException("Manifesto do hook inválido.");
        return manifest;
    }
    private bool ValidBackup(string path) => Path.GetFullPath(path).StartsWith(SettingsPath + ".panelos-backup-", StringComparison.OrdinalIgnoreCase) &&
        Path.GetDirectoryName(Path.GetFullPath(path)) == Path.GetDirectoryName(SettingsPath);

    private static JsonObject ReadRoot(byte[] bytes)
    {
        if (bytes.Length == 0) return new JsonObject();
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("O arquivo de configurações é grande demais.");
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        using var document = JsonDocument.Parse(text);
        RejectDuplicates(document.RootElement);
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("As configurações devem ser um objeto JSON.");
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Campo JSON duplicado."); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var entry in value.EnumerateArray()) RejectDuplicates(entry);
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
