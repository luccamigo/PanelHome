using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.Development;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// ADB is the development transport only. These tests keep it honest about
/// what it found, and keep it from running anything through a shell.
/// </summary>
[TestClass]
public sealed class AdbDevelopmentTransportTests
{
    private const string DeviceList = """
        List of devices attached
        R58M12345XY	device
        """;

    private FakeAdbLocator _locator = new();
    private FakeProcessRunner _runner = new();

    [TestInitialize]
    public void Reset()
    {
        _locator = new FakeAdbLocator();
        _runner = new FakeProcessRunner();
    }

    [TestMethod]
    public void TheTransportKnowsTheControlAndVideoPorts()
    {
        CollectionAssert.AreEqual(new[] { 8765, 8766 }, Transport().Ports.ToArray());
    }

    [TestMethod]
    public async Task AMissingAdbIsReportedWithoutRunningAnything()
    {
        _locator.Path = null;

        var status = await Transport().ProbeAsync(CancellationToken.None);

        Assert.IsFalse(status.AdbFound);
        Assert.IsFalse(status.IsReady);
        StringAssert.Contains(status.Detail!, "adb.exe");
        Assert.AreEqual(0, _runner.Calls.Count);
    }

    [TestMethod]
    public async Task AConnectedDeviceIsReportedAsReady()
    {
        _runner.EnqueueDevices(DeviceList);

        var status = await Transport().ProbeAsync(CancellationToken.None);

        Assert.IsTrue(status.AdbFound);
        Assert.AreEqual(AdbDeviceState.Ready, status.Device);
        Assert.AreEqual("R58M12345XY", status.DeviceSerial);
        Assert.IsTrue(status.IsReady);
        CollectionAssert.AreEqual(
            new[] { "devices" },
            _runner.Calls[0].Arguments.ToArray());
    }

    [TestMethod]
    public async Task NoDeviceIsReported()
    {
        _runner.EnqueueDevices("List of devices attached\n");

        var status = await Transport().ProbeAsync(CancellationToken.None);

        Assert.AreEqual(AdbDeviceState.NoDevice, status.Device);
        Assert.IsFalse(status.IsReady);
    }

    [TestMethod]
    public async Task AnUnauthorizedDeviceIsReported()
    {
        _runner.EnqueueDevices("List of devices attached\nR58M12345XY\tunauthorized");

        var status = await Transport().ProbeAsync(CancellationToken.None);

        Assert.AreEqual(AdbDeviceState.Unauthorized, status.Device);
        StringAssert.Contains(status.Detail!, "Autorize");
    }

    [TestMethod]
    public async Task EmulatorsAndNetworkTargetsDoNotCount()
    {
        _runner.EnqueueDevices(
            """
            List of devices attached
            emulator-5554	device
            192.168.0.10:5555	device
            adb-ABC123-xyz._adb-tls-connect._tcp	device
            """);

        var status = await Transport().ProbeAsync(CancellationToken.None);

        Assert.AreEqual(AdbDeviceState.NoDevice, status.Device);
        StringAssert.Contains(status.Detail!, "USB");
    }

    [TestMethod]
    public async Task MoreThanOneDeviceIsReported()
    {
        _runner.EnqueueDevices(
            """
            List of devices attached
            R58M12345XY	device
            R58M99999ZZ	device
            """);

        var status = await Transport().ProbeAsync(CancellationToken.None);

        Assert.AreEqual(AdbDeviceState.MultipleDevices, status.Device);
    }

    [TestMethod]
    public async Task ReverseConfiguresBothPortsWithStructuredArguments()
    {
        _runner.EnqueueDevices(DeviceList);

        var result = await Transport().ConfigureReverseAsync(CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        CollectionAssert.AreEqual(
            new[] { "-s", "R58M12345XY", "reverse", "tcp:8765", "tcp:8765" },
            _runner.Calls[1].Arguments.ToArray());
        CollectionAssert.AreEqual(
            new[] { "-s", "R58M12345XY", "reverse", "tcp:8766", "tcp:8766" },
            _runner.Calls[2].Arguments.ToArray());
        CollectionAssert.AreEqual(
            new[] { "-s", "R58M12345XY", "reverse", "--list" },
            _runner.Calls[3].Arguments.ToArray());
        var launch = _runner.Calls[4].Arguments;
        Assert.IsTrue(launch.Contains("panelAdbAuthToken"));
        var token = launch[^1];
        Assert.AreEqual(64, token.Length);
        Assert.IsTrue(token.All(Uri.IsHexDigit));
        Assert.IsFalse(result.Message.Contains(token, StringComparison.OrdinalIgnoreCase));

        // adb is executed directly: no shell, and nothing is concatenated into
        // a command line.
        foreach (var call in _runner.Calls)
        {
            Assert.AreEqual(@"C:\Android\platform-tools\adb.exe", call.Executable);
            Assert.IsFalse(call.Arguments.Any(
                argument => argument.Contains("&&", StringComparison.Ordinal) ||
                    argument.Contains("cmd", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [TestMethod]
    public async Task ReverseWithoutADeviceFailsWithoutRunningReverse()
    {
        _runner.EnqueueDevices("List of devices attached\n");

        var result = await Transport().ConfigureReverseAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(1, _runner.Calls.Count);
    }

    [TestMethod]
    public async Task AFailingReverseIsReported()
    {
        _runner.EnqueueDevices(DeviceList);
        _runner.Enqueue(new ProcessRunResult(1, string.Empty, "cannot bind listener"));

        var result = await Transport().ConfigureReverseAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "cannot bind listener");
    }

    [TestMethod]
    public async Task AnAdbThatCannotRunIsReportedInsteadOfThrowing()
    {
        _runner.Failure = new TimeoutException("adb não respondeu");

        var status = await Transport().ProbeAsync(CancellationToken.None);

        Assert.IsTrue(status.AdbFound);
        Assert.IsFalse(status.IsReady);
        StringAssert.Contains(status.Detail!, "adb não respondeu");
    }

    private AdbDevelopmentTransport Transport() => new(_locator, _runner);
}
