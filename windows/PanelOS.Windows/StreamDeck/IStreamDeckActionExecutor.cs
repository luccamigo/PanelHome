using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelOS.Windows.StreamDeck;

/// <summary>
/// Runs the action a configured button carries.
/// </summary>
/// <remarks>
/// <para>
/// Security model - the tablet never sends anything executable. The only thing
/// that crosses the cable is an identifier:
/// </para>
/// <code>
/// tablet -> streamdeck.invoke(buttonId) -> PanelHost
///        -> looks the id up in the PC-side configuration
///        -> runs the action the user configured on the PC
/// </code>
/// <para>
/// The rejected alternative is the tablet sending a command line, a script or
/// a shell string for the PC to run. That would turn the USB transport into
/// remote code execution on the desktop, so the executable path, arguments and
/// working directory of an action stay on the PC and are never serialized into
/// <see cref="Dto.StreamDeckLayoutDto"/>.
/// </para>
/// <para>
/// <see cref="ExecuteAsync"/> is therefore the whole remote surface: an id,
/// resolved against the local store.
/// <see cref="ExecuteButtonAsync"/> takes an already resolved button. Two local
/// callers use it: the Control Center's "test action", which runs a button the
/// user is editing and has not necessarily saved yet, and PanelHost, which
/// resolves the invoked id against the same snapshot whose revision the tablet
/// echoed back - so the button that runs is the button the tablet was drawing,
/// not whatever the file happens to say a moment later. Neither passes anything
/// that came off a wire except the id used for the lookup.
/// </para>
/// </remarks>
public interface IStreamDeckActionExecutor
{
    /// <summary>
    /// Resolves <paramref name="buttonId"/> in the local configuration and runs
    /// whatever it is configured to do.
    /// </summary>
    Task<StreamDeckActionResult> ExecuteAsync(
        string buttonId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs an already resolved button. Local callers only.
    /// </summary>
    Task<StreamDeckActionResult> ExecuteButtonAsync(
        StreamDeckButton button,
        CancellationToken cancellationToken);
}
