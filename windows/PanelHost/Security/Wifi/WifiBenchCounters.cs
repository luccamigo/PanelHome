using System.Globalization;
using System.Text;

namespace PanelHost.Security.Wifi;

internal enum WifiBenchEvent
{
    Authenticated, Completed,
    OriginRefused, AdmissionRefused,
    TlsNoCertificate, TlsInvalidCertificate, TlsRevoked, TlsNotAuthorized, TlsOther,
    ProtocolRefused, ControlRate, CommandRefused, HeartbeatTimeout, SessionRevoked,
    PairingApproved, PairingDenied, PairingFailed,
}

// Aggregate refusal counters for the bench window. By construction they hold only counts: no address, key id,
// certificate, label or payload can reach the log through them.
internal sealed class WifiBenchCounters
{
    private static readonly WifiBenchEvent[] All = Enum.GetValues<WifiBenchEvent>();
    private readonly long[] _values = new long[All.Length];

    public void Increment(WifiBenchEvent kind) => Interlocked.Increment(ref _values[(int)kind]);
    public long Get(WifiBenchEvent kind) => Interlocked.Read(ref _values[(int)kind]);

    public void Increment(WifiTlsRefusal reason) => Increment(reason switch
    {
        WifiTlsRefusal.NoCertificate => WifiBenchEvent.TlsNoCertificate,
        WifiTlsRefusal.InvalidCertificate => WifiBenchEvent.TlsInvalidCertificate,
        WifiTlsRefusal.Revoked => WifiBenchEvent.TlsRevoked,
        _ => WifiBenchEvent.TlsNotAuthorized,
    });

    public static string Name(WifiBenchEvent kind)
    {
        var text = new StringBuilder();
        foreach (var c in kind.ToString())
        {
            if (char.IsUpper(c) && text.Length > 0) text.Append('_');
            text.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    public string Format() => "[WiFi bench] counters: " + string.Join(' ', All.Select(kind => $"{Name(kind)}={Get(kind)}"));
}
