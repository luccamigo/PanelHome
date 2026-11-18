using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Broker;
using PanelHost.Protocol;
using PanelHost.Services;
using PanelOS.DisplayBroker.Protocol;

namespace PanelHost.Tests;

[TestClass]
public sealed class RedesignTelemetryTests
{
    private sealed class Telemetry : ITelemetryService
    {
        public TelemetryMessage CurrentSnapshot { get; set; } = new() { Cpu = new() { UsagePercent = 35 } };
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [TestMethod]
    public async Task Missing_broker_does_not_call_the_privileged_reader()
    {
        var called = false;
        await using var service = new SupplementalTelemetryService(new Telemetry(), () => false,
            _ => { called = true; throw new InvalidOperationException(); });
        await service.RefreshAsync(default);
        Assert.IsFalse(called);
        Assert.IsNull(service.CurrentSnapshot.Cpu.TemperatureC);
        Assert.AreEqual(35, service.CurrentSnapshot.Cpu.UsagePercent);
    }

    [TestMethod]
    public async Task Privileged_temperature_expires_immediately_when_the_lease_ends()
    {
        var connected = true;
        await using var service = new SupplementalTelemetryService(new Telemetry(), () => connected,
            _ => Task.FromResult<double?>(71.5));
        await service.RefreshAsync(default);
        Assert.AreEqual(71.5, service.CurrentSnapshot.Cpu.TemperatureC);
        connected = false;
        Assert.IsNull(service.CurrentSnapshot.Cpu.TemperatureC);
    }

    [TestMethod]
    public async Task Last_good_temperature_stays_visible_while_the_next_read_is_in_flight()
    {
        var gate = new TaskCompletionSource<double?>();
        var first = true;
        await using var service = new SupplementalTelemetryService(new Telemetry(), () => true,
            _ => { if (first) { first = false; return Task.FromResult<double?>(70.2); } return gate.Task; });
        await service.RefreshAsync(default);
        var pending = service.RefreshAsync(default);
        Assert.AreEqual(70.2, service.CurrentSnapshot.Cpu.TemperatureC);
        gate.SetResult(70.8);
        await pending;
        Assert.AreEqual(70.8, service.CurrentSnapshot.Cpu.TemperatureC);
    }

    [TestMethod]
    public async Task A_failed_read_clears_the_previous_temperature()
    {
        var fail = false;
        await using var service = new SupplementalTelemetryService(new Telemetry(), () => true,
            _ => fail ? throw new IOException("lost") : Task.FromResult<double?>(70.2));
        await service.RefreshAsync(default);
        fail = true;
        await service.RefreshAsync(default);
        Assert.IsNull(service.CurrentSnapshot.Cpu.TemperatureC);
    }

    [TestMethod]
    public void Broker_cpu_read_never_blocks_and_is_null_without_a_sample()
    {
        using var sensor = new PanelDisplayBroker.BrokerCpuTemperature(startSampling: false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.IsNull(sensor.Read());
        Assert.IsTrue(clock.ElapsedMilliseconds < 200);
    }

    [TestMethod]
    public async Task A_working_normal_sensor_does_not_require_a_privileged_read()
    {
        var inner = new Telemetry { CurrentSnapshot = new() { Cpu = new() { TemperatureC = 55 } } };
        await using var service = new SupplementalTelemetryService(inner, () => true,
            _ => throw new AssertFailedException("Should use normal sensor"));
        await service.RefreshAsync(default);
        Assert.AreEqual(55, service.CurrentSnapshot.Cpu.TemperatureC);
    }

    [TestMethod]
    public async Task Sensor_failure_preserves_other_metrics()
    {
        await using var service = new SupplementalTelemetryService(new Telemetry(), () => true,
            _ => throw new IOException("broker missing"));
        await service.RefreshAsync(default);
        Assert.IsNull(service.CurrentSnapshot.Cpu.TemperatureC);
        Assert.AreEqual(35, service.CurrentSnapshot.Cpu.UsagePercent);
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(125.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public async Task Invalid_temperatures_are_absent(double value)
    {
        await using var service = new SupplementalTelemetryService(new Telemetry(), () => true,
            _ => Task.FromResult<double?>(value));
        await service.RefreshAsync(default);
        Assert.IsNull(service.CurrentSnapshot.Cpu.TemperatureC);
    }

    [TestMethod]
    public async Task Temperature_query_without_negotiated_lease_cannot_launch_a_broker()
    {
        await using var client = new PanelDisplayBrokerClient(executableLocator: () => throw new AssertFailedException("No launch"));
        Assert.IsNull(await client.ReadCpuTemperatureIfConnectedAsync(default));
        var oldHello = PanelDisplayBrokerProtocol.Deserialize<PanelDisplayBrokerHello>(
            "{\"protocolVersion\":1,\"nonce\":\"abc\",\"parentProcessId\":1,\"parentStartTimeUtcTicks\":1}");
        Assert.IsFalse(oldHello!.CpuTemperatureAvailable);
    }

    [TestMethod]
    public void Foreground_counter_never_attributes_the_old_app_window_to_the_new_app()
    {
        var counter = new ForegroundPresentsCounter();
        Assert.AreEqual("no_app", counter.Sample(10, "test", 0).State);
        for (var i = 0; i < 120; i++) { counter.Record(10); counter.Record(20); }
        var sample = counter.Sample(10, "test", 2);
        Assert.AreEqual(60, sample.PresentsPerSecond);
        Assert.AreEqual("test", sample.AppName);
        Assert.AreEqual("measuring", sample.State);
        counter.Record(10);
        Assert.AreEqual("no_app", counter.Sample(20, "other", 3).State);
        Assert.AreEqual("no_app", counter.Sample(20, "other", 4).State);
    }

    [TestMethod]
    public void No_events_or_invalid_time_does_not_invent_a_zero_rate()
    {
        var counter = new ForegroundPresentsCounter();
        counter.Sample(10, "test", 5);
        counter.Record(10);
        Assert.IsNull(counter.Sample(10, "test", 5).PresentsPerSecond);
        Assert.IsNull(counter.Sample(10, "test", 6).PresentsPerSecond);
    }
}
