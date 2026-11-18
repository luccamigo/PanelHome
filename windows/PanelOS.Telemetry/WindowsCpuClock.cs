using System.Runtime.InteropServices;

namespace PanelOS.Telemetry;

/// <summary>OS estimate of aggregate frequency, not a per-core MSR measurement.</summary>
public sealed class WindowsCpuClock : IDisposable
{
    public const string Source = "WindowsProcessorPerformance";
    private IntPtr _query;
    private IntPtr _performance;
    private IntPtr _frequency;
    private bool _disposed;
    private readonly Func<uint> _openQuery;

    public string? UnavailableReason { get; private set; }

    public WindowsCpuClock() : this(null) { }

    internal WindowsCpuClock(Func<uint>? openQuery)
    {
        _openQuery = openQuery ?? (() => PdhOpenQueryW(null, UIntPtr.Zero, out _query));
    }

    // Called on the telemetry thread only. First call primes the rate counter;
    // no sleeping, cached values, or nominal-only substitute on failure.
    public double? ReadMhz()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_query == IntPtr.Zero)
        {
            if (!Check(_openQuery()) ||
                !Check(PdhAddEnglishCounterW(_query,
                    @"\Processor Information(_Total)\% Processor Performance", UIntPtr.Zero, out _performance)) ||
                !Check(PdhAddEnglishCounterW(_query,
                    @"\Processor Information(_Total)\Processor Frequency", UIntPtr.Zero, out _frequency)) ||
                !Check(PdhCollectQueryData(_query)))
            {
                CloseQuery();
                return null;
            }
            UnavailableReason = "Windows performance counter warming up (two samples required)";
            return null;
        }

        if (!Check(PdhCollectQueryData(_query)))
        {
            CloseQuery();
            return null;
        }
        UnavailableReason = null;
        var performance = ReadCounter(_performance);
        var frequency = ReadCounter(_frequency);
        var mhz = EstimateMhz(frequency, performance);
        if (mhz.HasValue)
            UnavailableReason = null;
        else
            UnavailableReason ??= "Windows frequency/performance unavailable or implausible";
        return mhz;
    }

    public static double? EstimateMhz(double? nominalMhz, double? performancePercent)
    {
        if (nominalMhz is not > 0 or > 10000 || performancePercent is not > 0 ||
            !double.IsFinite(nominalMhz.Value) || !double.IsFinite(performancePercent.Value))
            return null;
        var mhz = nominalMhz.Value * performancePercent.Value / 100;
        return double.IsFinite(mhz) && mhz is > 0 and <= 10000 ? mhz : null;
    }

    private double? ReadCounter(IntPtr counter)
    {
        // NOCAP100 preserves boost above nominal frequency.
        if (!Check(PdhGetFormattedCounterValue(counter, 0x200 | 0x8000, out _, out var value)) ||
            !Check(value.Status == 1 ? 0 : value.Status))
            return null;
        return value.Value;
    }

    private bool Check(uint status)
    {
        if (status == 0)
            return true;
        UnavailableReason = $"Windows PDH status 0x{status:X8}";
        return false;
    }

    private void CloseQuery()
    {
        if (_query != IntPtr.Zero)
            PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    /// <summary>Discard the interval when the preferred library source recovers.</summary>
    public void Reset() => CloseQuery();

    public void Dispose()
    {
        CloseQuery();
        _disposed = true;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
