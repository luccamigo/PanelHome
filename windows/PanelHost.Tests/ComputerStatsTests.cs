using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Services;

namespace PanelHost.Tests;

[TestClass]
public sealed class ComputerStatsTests
{
    [TestMethod]
    public void Network_uses_monotonic_deltas_and_missing_reset_or_new_adapter_never_becomes_zero()
    {
        var sampler = new NetworkRateSampler();
        NetworkCounter[] First() => [new("a", "Ethernet", 1000, 500)];
        Assert.IsNull(sampler.Sample(First(), 0, 100).DownloadBytesPerSecond);
        var next = sampler.Sample([new("a", "Ethernet", 3000, 1500)], 2, 200);
        Assert.AreEqual(1000, next.DownloadBytesPerSecond);
        Assert.AreEqual(500, next.UploadBytesPerSecond);
        Assert.AreEqual(200, next.SampledAtUnixMs);
        Assert.IsNull(sampler.Sample([new("a", "Ethernet", 1, 2)], 3, 300).DownloadBytesPerSecond);
        var missing = sampler.Sample([new("a", "Ethernet", null, null)], 4, 400);
        Assert.IsNull(missing.Adapters.Single().DownloadBytesPerSecond);
        Assert.IsNull(missing.DownloadBytesPerSecond);
        Assert.IsNull(sampler.Sample([], 5, 500).DownloadBytesPerSecond);
        Assert.IsNull(sampler.Sample(First(), 6, 600).DownloadBytesPerSecond);
    }

    [TestMethod]
    public void Network_totals_all_active_adapters_only_when_all_rates_are_known()
    {
        var sampler = new NetworkRateSampler();
        sampler.Sample([new("a", "A", 0, 0), new("b", "B", 0, 0)], 10, 0);
        var sample = sampler.Sample([new("a", "A", 100, 200), new("b", "B", 300, 400)], 11, 0);
        Assert.AreEqual(400, sample.DownloadBytesPerSecond);
        Assert.AreEqual(600, sample.UploadBytesPerSecond);
        Assert.IsNull(sampler.Sample([new("a", "A", 200, 300), new("c", "C", 10, 20)], 12, 0).DownloadBytesPerSecond);
    }

    [TestMethod]
    public void Process_cpu_is_normalized_and_pid_reuse_does_not_inherit_previous_cpu()
    {
        var sampler = new ProcessRateSampler(4);
        ProcessCounter Entry(long start, double cpu) => new("editor", 10, start, cpu, 1000, "current_user", false);
        Assert.IsNull(sampler.Sample([Entry(1, 4)], 10, 0, new()).Items.Single().CpuPercent);
        Assert.AreEqual(25, sampler.Sample([Entry(1, 6)], 12, 0, new()).Items.Single().CpuPercent);
        Assert.IsNull(sampler.Sample([Entry(2, 12)], 14, 0, new()).Items.Single().CpuPercent);
        sampler.Sample([], 16, 0, new());
        Assert.IsNull(sampler.Sample([Entry(2, 15)], 18, 0, new()).Items.Single().CpuPercent);
    }

    [TestMethod]
    public void Processes_sort_top_fifteen_group_and_keep_unreadable_values_absent()
    {
        var sampler = new ProcessRateSampler(1);
        var counters = Enumerable.Range(1, 20).Select(i => new ProcessCounter("same", i, i, i, i * 100, "current_user", false)).ToArray();
        var memory = sampler.Sample(counters, 0, 0, new("memory", false));
        Assert.AreEqual(15, memory.Items.Count);
        Assert.AreEqual(20, memory.Items[0].Pid);
        var grouped = sampler.Sample(counters, 1, 100, new("memory", true)).Items.Single();
        Assert.AreEqual(20, grouped.Count);
        Assert.AreEqual(21000, grouped.MemoryBytes);
        Assert.IsNull(grouped.Pid);
        Assert.IsTrue(grouped.Protected);
        var inaccessible = sampler.Sample([new("denied", 50, null, null, null, "unknown", true)], 2, 200, new()).Items.Single();
        Assert.IsNull(inaccessible.CpuPercent);
        Assert.IsNull(inaccessible.MemoryBytes);
        Assert.IsTrue(inaccessible.Protected);
        Assert.AreEqual("unknown", inaccessible.Owner);
    }

    [TestMethod]
    public void Options_allow_only_documented_sort_orders()
    {
        Assert.IsTrue(ProcessListOptions.TryCreate("cpu", true, out _));
        Assert.IsTrue(ProcessListOptions.TryCreate("memory", false, out _));
        Assert.IsFalse(ProcessListOptions.TryCreate("command", false, out _));
        Assert.IsFalse(ProcessListOptions.TryCreate("cpu", null, out _));
    }
}
