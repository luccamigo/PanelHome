using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Services;

namespace PanelHost.Tests;

[TestClass]
public sealed class ForegroundProcessFamilyTests
{
    [TestMethod]
    public void Browser_capture_attributes_gpu_presents_to_foreground_browser()
    {
        using var roles = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "browser-process-roles.json")));
        var nodes = roles.RootElement.EnumerateArray().Select(e => new ForegroundProcessNode(
            e.GetProperty("pid").GetInt32(), e.GetProperty("parentPid").GetInt32(), e.GetProperty("name").GetString()!)).ToArray();
        var family = ForegroundProcessFamily.Select(4000, nodes, id => id == 4000 ? 1 : 2);
        var counter = new ForegroundPresentsCounter();
        counter.Sample(4000, "browser", 0, family);
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "browser-etw-pids.jsonl"));
        foreach (var line in lines.Skip(1))
        {
            using var doc = JsonDocument.Parse(line);
            var row = doc.RootElement;
            for (var i = 0; i < row.GetProperty("presentEvents").GetInt32(); i++) counter.Record(row.GetProperty("pid").GetInt32());
        }
        var sample = counter.Sample(4000, "browser", 15.1742285, family);
        Assert.AreEqual("measuring", sample.State);
        Assert.AreEqual("browser", sample.AppName);
        Assert.AreEqual(59.8, sample.PresentsPerSecond);
    }

    [TestMethod]
    public void Family_is_bounded_same_image_and_excludes_unrelated_or_reused_parent()
    {
        var nodes = new[] { new ForegroundProcessNode(1, 99, "browser.exe"), new(2, 1, "BROWSER.exe"),
            new(3, 2, "browser.exe"), new(4, 3, "browser.exe"), new(5, 4, "browser.exe"), new(6, 5, "browser.exe"),
            new(7, 1, "other.exe"), new(8, 99, "browser.exe"), new(9, 1, "browser.exe") };
        var family = ForegroundProcessFamily.Select(1, nodes, id => id == 9 ? 0 : id);
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5 }, family.Keys.ToArray());
    }

    [TestMethod]
    public void Foreground_child_can_use_gpu_sibling_but_not_another_browser_root()
    {
        var nodes = new[] { new ForegroundProcessNode(1, 99, "app.exe"), new(2, 1, "app.exe"),
            new(3, 1, "app.exe"), new(4, 99, "app.exe") };
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, ForegroundProcessFamily.Select(2, nodes, id => id).Keys.ToArray());
    }

    [TestMethod]
    public void Cycles_and_missing_identity_fail_closed()
    {
        var nodes = new[] { new ForegroundProcessNode(1, 2, "app"), new(2, 1, "app") };
        Assert.IsTrue(ForegroundProcessFamily.Select(1, nodes, _ => 1).Count <= 2);
        Assert.AreEqual(0, ForegroundProcessFamily.Select(1, nodes, _ => null).Count);
    }

    [TestMethod]
    public void Direct_presents_take_priority_and_departed_or_reused_children_are_excluded()
    {
        var counter = new ForegroundPresentsCounter();
        var family = new Dictionary<int, long> { [1] = 10, [2] = 20 };
        counter.Sample(1, "browser", 0, family);
        counter.Record(1); counter.Record(2); counter.Record(2);
        Assert.AreEqual(1, counter.Sample(1, "browser", 1, family).PresentsPerSecond);
        counter.Record(2);
        family[2] = 30;
        Assert.AreEqual("no_app", counter.Sample(1, "browser", 2, family).State);
        counter.Record(2);
        family.Remove(2);
        Assert.AreEqual("no_app", counter.Sample(1, "browser", 3, family).State);
    }

    [TestMethod]
    public void Member_limit_and_inaccessible_children_bound_collection()
    {
        var nodes = new[] { new ForegroundProcessNode(1, 0, "app") }
            .Concat(Enumerable.Range(2, 1000).Select(id => new ForegroundProcessNode(id, 1, "app")));
        var family = ForegroundProcessFamily.Select(1, nodes, id => id == 2 ? null : id);
        Assert.AreEqual(256, family.Count);
        Assert.IsFalse(family.ContainsKey(2));
    }

    [TestMethod]
    public void Fallback_sums_only_tracked_same_family_processes()
    {
        var counter = new ForegroundPresentsCounter();
        var family = new Dictionary<int, long> { [1] = 1, [2] = 2, [3] = 3 };
        counter.Sample(1, "browser", 0, family);
        counter.Record(2); counter.Record(3); counter.Record(99);
        Assert.AreEqual(2, counter.Sample(1, "browser", 1, family).PresentsPerSecond);
    }

    [TestMethod]
    public void Switching_foreground_or_reusing_its_pid_warms_up_instead_of_misattributing()
    {
        var counter = new ForegroundPresentsCounter();
        counter.Sample(1, "browser", 0, new Dictionary<int, long> { [1] = 10, [2] = 20 });
        counter.Record(2);
        Assert.AreEqual("no_app", counter.Sample(2, "browser", 1, new Dictionary<int, long> { [1] = 10, [2] = 20 }).State);
        counter.Record(2);
        Assert.AreEqual("no_app", counter.Sample(2, "browser", 2, new Dictionary<int, long> { [2] = 30 }).State);
    }
}
