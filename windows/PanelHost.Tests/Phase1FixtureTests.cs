using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Protocol;

namespace PanelHost.Tests;

[TestClass]
public sealed class Phase1FixtureTests
{
    [TestMethod]
    public void Shared_fixtures_preserve_null_rates_and_full_process_identity()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "protocol-fixtures"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        var root = Path.Combine(directory.FullName, "protocol-fixtures", "phase1");
        var rates = PanelJson.Deserialize<NetworkRatesMessage>(File.ReadAllText(Path.Combine(root, "network-rates.json")));
        Assert.IsNull(rates.UploadBytesPerSecond);
        Assert.AreEqual(1024, rates.Adapters.Single().DownloadBytesPerSecond);
        var processes = PanelJson.Deserialize<ProcessesMessage>(File.ReadAllText(Path.Combine(root, "processes.json")));
        Assert.AreEqual(638000000000000000L, processes.Items[0].StartTimeUtcTicks);
        Assert.IsNull(processes.Items[1].MemoryBytes);
        var json = PanelJson.Serialize(processes);
        StringAssert.Contains(json, "\"type\":\"processes\"");
        Assert.IsFalse(json.Contains("commandLine", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("imagePath", StringComparison.Ordinal));
    }
}
