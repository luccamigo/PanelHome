using System.Globalization;
using System.Resources;

namespace PanelOS.ControlCenter.Resources;

internal static class ControlCenterStrings
{
    private static readonly ResourceManager Manager = new(
        "PanelOS.ControlCenter.Resources.ControlCenterStrings",
        typeof(ControlCenterStrings).Assembly);

    public static string Get(string name) =>
        Manager.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing resource '{name}'.");

    public static string Format(string name, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(name), arguments);
}
