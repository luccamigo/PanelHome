using LibreHardwareMonitor.Hardware;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Services;

namespace PanelHost.Tests;

/// <summary>
/// The update contract behind every reading: sensor values are only meaningful
/// after <see cref="IHardware.Update"/> ran for that node in this cycle, so the
/// visitor must reach sub-hardware too and must not let a node that threw pass
/// its stale sensors off as fresh measurements.
/// </summary>
[TestClass]
public sealed class TelemetryUpdateVisitorTests
{
    [TestMethod]
    public void Every_hardware_node_including_sub_hardware_is_updated()
    {
        var child = new FakeHardware("Core Complex");
        var parent = new FakeHardware("AMD Ryzen 5 5600G", child);
        var visitor = new LibreHardwareTelemetryService.UpdateVisitor();

        visitor.BeginUpdate();
        parent.Accept(visitor);

        Assert.AreEqual(1, parent.UpdateCount);
        Assert.AreEqual(1, child.UpdateCount, "Sub-hardware must be updated too.");
        Assert.IsTrue(visitor.WasSuccessful(parent));
        Assert.IsTrue(visitor.WasSuccessful(child));
    }

    [TestMethod]
    public void Hardware_that_throws_is_marked_failed_and_reported_once()
    {
        var failing = new FakeHardware("AMD Ryzen 5 5600G")
        {
            UpdateException = new InvalidOperationException("ring0 unavailable"),
        };
        var visitor = new LibreHardwareTelemetryService.UpdateVisitor();

        visitor.BeginUpdate();
        failing.Accept(visitor);

        Assert.AreEqual(1, failing.UpdateCount);
        Assert.IsFalse(
            visitor.WasSuccessful(failing),
            "A node whose Update() threw must not be treated as sampled.");
    }

    [TestMethod]
    public void A_recovered_node_is_successful_again_on_the_next_cycle()
    {
        var hardware = new FakeHardware("AMD Ryzen 5 5600G")
        {
            UpdateException = new InvalidOperationException("ring0 unavailable"),
        };
        var visitor = new LibreHardwareTelemetryService.UpdateVisitor();

        visitor.BeginUpdate();
        hardware.Accept(visitor);
        Assert.IsFalse(visitor.WasSuccessful(hardware));

        hardware.UpdateException = null;
        visitor.BeginUpdate();
        hardware.Accept(visitor);

        Assert.AreEqual(2, hardware.UpdateCount);
        Assert.IsTrue(
            visitor.WasSuccessful(hardware),
            "Failure state must be per cycle, not sticky for the process.");
    }

    private sealed class FakeHardware : IHardware
    {
        private static int _instances;

        public FakeHardware(string name, params IHardware[] subHardware)
        {
            Name = name;
            SubHardware = subHardware;
            Identifier = new Identifier("fake", (_instances++).ToString());
        }

        public int UpdateCount { get; private set; }

        public Exception? UpdateException { get; set; }

        public string Name { get; set; }

        public Identifier Identifier { get; }

        public HardwareType HardwareType => HardwareType.Cpu;

        public IHardware? Parent => null;

        public ISensor[] Sensors => Array.Empty<ISensor>();

        public IHardware[] SubHardware { get; }

        public IDictionary<string, string> Properties =>
            new Dictionary<string, string>();

        public string GetReport() => string.Empty;

        public void Update()
        {
            UpdateCount++;
            if (UpdateException is { } exception)
            {
                throw exception;
            }
        }

        public void Accept(IVisitor visitor) => visitor.VisitHardware(this);

        public void Traverse(IVisitor visitor)
        {
            foreach (var subHardware in SubHardware)
            {
                subHardware.Accept(visitor);
            }
        }

        public event SensorEventHandler? SensorAdded;

        public event SensorEventHandler? SensorRemoved;

        /// <summary>
        /// The interface's events are part of its contract but nothing in these
        /// tests raises them; this keeps them observably unused rather than
        /// silently discarded.
        /// </summary>
        internal void RaiseSensorEvents(ISensor sensor)
        {
            SensorAdded?.Invoke(sensor);
            SensorRemoved?.Invoke(sensor);
        }
    }
}
