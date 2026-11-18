using System.Globalization;
using System.Resources;

namespace PanelOS.ControlCenter.Resources;

internal static class ControlCenterStrings
{
    private static readonly ResourceManager Manager = new(
        "PanelOS.ControlCenter.Resources.ControlCenterStrings",
        typeof(ControlCenterStrings).Assembly);

    /// <summary>
    /// Culture used to format numbers and dates inside the UI text. The only resource file is Portuguese
    /// (pt-BR), so values are formatted the same way regardless of the machine's regional settings.
    /// Otherwise an en-US PC would show "59.4 FPS" in the middle of Portuguese text.
    /// </summary>
    public static CultureInfo FormatCulture { get; } = CreateFormatCulture();

    private static CultureInfo CreateFormatCulture()
    {
        try { return CultureInfo.GetCultureInfo("pt-BR"); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; } // invariant-globalization mode
    }

    public static string Get(string name) =>
        Manager.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing resource '{name}'.");

    public static string Format(string name, params object?[] arguments) =>
        string.Format(FormatCulture, Get(name), arguments);
}
