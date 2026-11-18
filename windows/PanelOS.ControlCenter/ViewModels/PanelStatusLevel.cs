namespace PanelOS.ControlCenter.ViewModels;

/// <summary>
/// Severity behind every status dot in the UI. Views map it to a colour; view
/// models never mention colours.
/// </summary>
public enum PanelStatusLevel
{
    Neutral,
    Good,
    Warning,
    Bad,
}
