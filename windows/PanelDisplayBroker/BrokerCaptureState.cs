using System.Runtime.InteropServices;
using PanelOS.DisplayBroker.Protocol;
using PanelOS.DriverInterop;

namespace PanelDisplayBroker;

/// <summary>
/// Serializes enable/reset/publication so a frame already in flight cannot be
/// published after DISCONNECT invalidates the shared generation.
/// </summary>
internal sealed class BrokerCaptureState(SharedFrameMailbox mailbox)
{
    private readonly SharedFrameMailbox _mailbox =
        mailbox ?? throw new ArgumentNullException(nameof(mailbox));
    private readonly object _sync = new();
    private int _enabled;

    internal bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    internal void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            Volatile.Write(ref _enabled, enabled ? 1 : 0);
            if (!enabled)
            {
                _mailbox.Reset();
            }
        }
    }

    internal void DisableAndReset()
    {
        lock (_sync)
        {
            Volatile.Write(ref _enabled, 0);
            _mailbox.Reset();
        }
    }

    internal void Reset()
    {
        lock (_sync)
        {
            _mailbox.Reset();
        }
    }

    internal bool TryPublish(PanelDisplayDriverFrame frame)
    {
        lock (_sync)
        {
            if (!IsEnabled)
            {
                return false;
            }

            _mailbox.Publish(frame);
            return true;
        }
    }
}

/// <summary>Per-process 1 ms timer period, held only by active capture.</summary>
internal sealed class BrokerTimerResolutionScope : IDisposable
{
    private const uint TimerrNoError = 0;
    private const uint RequestedPeriodMilliseconds = 1;
    private readonly bool _raised;
    private int _disposed;

    private BrokerTimerResolutionScope(bool raised)
    {
        _raised = raised;
    }

    internal static BrokerTimerResolutionScope Acquire() =>
        new(TimeBeginPeriod(RequestedPeriodMilliseconds) == TimerrNoError);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _raised)
        {
            _ = TimeEndPeriod(RequestedPeriodMilliseconds);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
}
