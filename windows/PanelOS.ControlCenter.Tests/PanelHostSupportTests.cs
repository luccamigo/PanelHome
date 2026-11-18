using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.Host;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class PanelHostLogBufferTests
{
    [TestMethod]
    public void TheBufferKeepsOnlyItsCapacity()
    {
        var buffer = new PanelHostLogBuffer(capacity: 10);

        for (var index = 0; index < 5000; index++)
        {
            buffer.Append(PanelHostLogStream.StandardOutput, $"linha {index}");
        }

        Assert.AreEqual(10, buffer.Count);
        var lines = buffer.Snapshot();
        Assert.AreEqual("linha 4990", lines[0].Text);
        Assert.AreEqual("linha 4999", lines[^1].Text);
    }

    [TestMethod]
    public void TheDefaultCapacityIsBounded()
    {
        Assert.AreEqual(1000, PanelHostLogBuffer.DefaultCapacity);
        Assert.AreEqual(1000, new PanelHostLogBuffer().Capacity);
    }

    [TestMethod]
    public void AppendingRaisesTheEvent()
    {
        var buffer = new PanelHostLogBuffer();
        PanelHostLogEntry? seen = null;
        buffer.Appended += (_, args) => seen = args.Entry;

        buffer.Append(PanelHostLogStream.StandardError, "falhou");

        Assert.IsNotNull(seen);
        Assert.AreEqual("falhou", seen.Text);
        Assert.AreEqual(PanelHostLogStream.StandardError, seen.Stream);
        StringAssert.Contains(seen.Display, "err falhou");
    }

    [TestMethod]
    public void ClearEmptiesTheBuffer()
    {
        var buffer = new PanelHostLogBuffer();
        buffer.Append(PanelHostLogStream.Manager, "olá");

        buffer.Clear();

        Assert.AreEqual(0, buffer.Count);
    }
}

[TestClass]
public sealed class PanelHostExecutableLocatorTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void CreateTemporaryTree()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "PanelOS.ControlCenter.Tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void RemoveTemporaryTree()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public void NothingIsFoundWhenTheAgentIsNotInstalled()
    {
        var locator = new PanelHostExecutableLocator(
            applicationDirectory: Path.Combine(_root, "app"));

        Assert.IsNull(locator.Locate());
    }

    [TestMethod]
    public void TheAgentBesideTheControlCenterWins()
    {
        var appDirectory = CreateDirectory("app");
        var beside = CreateFile(appDirectory, "PanelHost.exe");
        var locator = new PanelHostExecutableLocator(
            applicationDirectory: appDirectory);

        var executable = locator.Locate();

        Assert.IsNotNull(executable);
        Assert.AreEqual(beside, executable.Path);
        Assert.AreEqual(
            PanelHostExecutableSource.ApplicationFolder,
            executable.Source);
    }

    [TestMethod]
    public void AConfiguredPathOverridesEverything()
    {
        var appDirectory = CreateDirectory("app");
        CreateFile(appDirectory, "PanelHost.exe");
        var custom = CreateFile(CreateDirectory("custom"), "PanelHost.exe");
        var locator = new PanelHostExecutableLocator(
            () => custom,
            appDirectory);

        var executable = locator.Locate();

        Assert.AreEqual(custom, executable!.Path);
        Assert.AreEqual(PanelHostExecutableSource.Configured, executable.Source);
    }

    [TestMethod]
    public void AConfiguredPathThatNoLongerExistsIsIgnored()
    {
        var appDirectory = CreateDirectory("app");
        var beside = CreateFile(appDirectory, "PanelHost.exe");
        var locator = new PanelHostExecutableLocator(
            () => Path.Combine(_root, "sumiu", "PanelHost.exe"),
            appDirectory);

        Assert.AreEqual(beside, locator.Locate()!.Path);
    }

    [TestMethod]
    public void ADevelopmentBuildIsFoundByWalkingUpToTheRepository()
    {
        // repo/windows/PanelHost/PanelHost.csproj + bin/.../PanelHost.exe, with
        // the Control Center running from repo/windows/X/bin/Debug/net8.0.
        var project = CreateDirectory(Path.Combine("repo", "windows", "PanelHost"));
        CreateFile(project, "PanelHost.csproj");
        var buildOutput = CreateDirectory(
            Path.Combine("repo", "windows", "PanelHost", "bin", "Debug", "net8.0"));
        var executablePath = CreateFile(buildOutput, "PanelHost.exe");
        var appDirectory = CreateDirectory(
            Path.Combine("repo", "windows", "PanelOS.ControlCenter", "bin", "Debug"));

        var locator = new PanelHostExecutableLocator(
            applicationDirectory: appDirectory);
        var executable = locator.Locate();

        Assert.IsNotNull(executable);
        Assert.AreEqual(executablePath, executable.Path);
        Assert.AreEqual(
            PanelHostExecutableSource.DevelopmentBuild,
            executable.Source);
    }

    private string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "test");
        return path;
    }
}

[TestClass]
public sealed class PanelHostFileLogTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void CreateTemporaryDirectory() =>
        _directory = Path.Combine(
            Path.GetTempPath(),
            "PanelOS.ControlCenter.Tests",
            Guid.NewGuid().ToString("n"));

    [TestCleanup]
    public void RemoveTemporaryDirectory()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public void LinesAreWrittenToTheLogFile()
    {
        using (var log = new PanelHostFileLog(_directory))
        {
            log.Write(new PanelHostLogEntry(
                DateTimeOffset.Now,
                PanelHostLogStream.StandardOutput,
                "primeira linha"));
        }

        var file = Path.Combine(_directory, "panelhost.log");
        StringAssert.Contains(File.ReadAllText(file), "primeira linha");
    }

    [TestMethod]
    public void TheNumberOfFilesIsBounded()
    {
        Assert.AreEqual(5, PanelHostFileLog.MaximumFiles);
        Assert.AreEqual(5L * 1024 * 1024, PanelHostFileLog.MaximumFileBytes);
    }

    [TestMethod]
    public void AnUnwritableDirectoryDoesNotThrow()
    {
        // A path that cannot be created must not take the agent down.
        using var log = new PanelHostFileLog(
            Path.Combine(_directory, "arquivo.txt", "sub"));

        log.Write(new PanelHostLogEntry(
            DateTimeOffset.Now,
            PanelHostLogStream.Manager,
            "linha"));
    }
}
