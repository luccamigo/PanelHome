using LibreHardwareMonitor.Hardware;

namespace PanelDisplayBroker;

/// <summary>CPU-only, read-only sensor access within the existing authenticated elevated lease.
/// Sensor I/O runs on its own thread so a stuck driver can never block the request loop or its watchdog;
/// <see cref="Read"/> only returns the cached value.</summary>
internal sealed class BrokerCpuTemperature : IDisposable
{
    private readonly Computer _computer = new() { IsCpuEnabled = true };
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private double _last = double.NaN;
    private long _lastTick;

    internal BrokerCpuTemperature(bool startSampling = true)
    {
        _thread = new Thread(Sample) { IsBackground = true, Name = "cpu-temperature" };
        if (startSampling) _thread.Start();
    }

    internal double? Read()
    {
        var value = Volatile.Read(ref _last);
        var fresh = Environment.TickCount64 - Interlocked.Read(ref _lastTick) < 3000;
        return fresh && double.IsFinite(value) ? value : null;
    }

    private void Sample()
    {
        var opened = false;
        while (!_stop.IsCancellationRequested)
        {
            double value = double.NaN;
            try
            {
                if (!opened) { _computer.Open(); opened = true; }
                foreach (var cpu in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
                {
                    cpu.Update();
                    var sensor = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature &&
                        s.Name is "Core (Tctl/Tdie)" or "CPU Package");
                    if (sensor?.Value is float v && float.IsFinite(v) && v > 0 && v < 125) { value = v; break; }
                }
            }
            catch (Exception)
            {
                // Missing/blocked sensor access must not interrupt the display request loop.
            }
            Volatile.Write(ref _last, value);
            Interlocked.Exchange(ref _lastTick, Environment.TickCount64);
            if (_stop.Token.WaitHandle.WaitOne(1000)) break;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        // A hung driver call must not hang shutdown; the thread is background.
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
    }
}
