using System.Runtime.Versioning;
using System.Security.Principal;
using PanelOS.DriverInterop;

namespace PanelHost.Transport;

/// <summary>
/// Fails a Desktop benchmark before any measurement starts when the host cannot
/// reach PanelDisplayDriver. The previous loaded run produced a Dashboard-shaped
/// result because PanelHost was not elevated and the driver silently stayed
/// unavailable; a late, ambiguous failure is worse than an early, explicit one.
/// </summary>
internal sealed record TransportBenchmarkPrecondition(
    bool Elevated,
    bool DriverAccessible,
    string? DriverInterfacePath,
    string? Detail)
{
    internal bool SatisfiesDesktopScenario => Elevated && DriverAccessible;

    [SupportedOSPlatform("windows")]
    internal static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<TransportBenchmarkPrecondition> ProbeAsync(
        IPanelDisplayDriverClient? driverClient,
        CancellationToken cancellationToken)
    {
        var elevated = IsElevated();
        if (driverClient is null)
        {
            return new TransportBenchmarkPrecondition(
                elevated,
                DriverAccessible: false,
                DriverInterfacePath: null,
                Detail: "PanelHost is running without a PanelDisplayDriver client (development desktop service).");
        }
        try
        {
            var status = await driverClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return new TransportBenchmarkPrecondition(
                elevated,
                DriverAccessible: true,
                driverClient.InterfacePath,
                $"Driver reachable; status={status}.");
        }
        catch (Exception exception) when (exception is PanelDisplayDriverException or IOException or UnauthorizedAccessException)
        {
            return new TransportBenchmarkPrecondition(
                elevated,
                DriverAccessible: false,
                driverClient.InterfacePath,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    internal string DescribeFailure(string scenario) =>
        $"Scenario '{scenario}' needs a real Desktop capture path, but " +
        $"elevated={Elevated}, driverAccessible={DriverAccessible}, " +
        $"interface={DriverInterfacePath ?? "<none>"}. {Detail} " +
        "Start PanelHost elevated so the benchmark measures Desktop H.264 instead of an idle Dashboard.";
}
