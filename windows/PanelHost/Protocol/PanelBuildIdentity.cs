using System.Reflection;

namespace PanelHost.Protocol;

/// <summary>Build diagnostics only. Compatibility is decided by protocol version.</summary>
public static class PanelBuildIdentity
{
    private static readonly Assembly HostAssembly = typeof(PanelBuildIdentity).Assembly;

    public static string Identity { get; } =
        HostAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? HostAssembly.GetName().Version?.ToString() ?? "unknown";

    public static long Number { get; } = ParseBuildNumber();

    private static long ParseBuildNumber()
    {
        var value = HostAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "PanelBuildNumber")?.Value;
        return long.TryParse(value, out var number) ? number : 0;
    }
}
