using System.Reflection;

namespace PanelOS.ControlCenter.Services;

/// <summary>
/// Version of this build, without the commit hash the SDK appends.
/// </summary>
public static class ControlCenterVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(ControlCenterVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(ControlCenterVersion).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
        }

        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
