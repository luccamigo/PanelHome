namespace PanelOS.Windows.StreamDeck.Dto;

/// <summary>
/// The only shape of the Stream Deck configuration that may ever be sent to
/// the tablet: what to draw, plus the ids needed to invoke a button back.
/// </summary>
/// <remarks>
/// There is deliberately no member here for an executable path, arguments,
/// working directory, URL or script. Those stay on the PC - the tablet asks
/// for a button by id and PanelHost decides what that means. Adding a field
/// that carries PC-side action configuration would break the model described
/// on <see cref="IStreamDeckActionExecutor"/>.
/// </remarks>
public sealed record StreamDeckLayoutDto(
    int Version,
    string ProfileId,
    string ProfileName,
    IReadOnlyList<StreamDeckPageDto> Pages);

public sealed record StreamDeckPageDto(
    string Id,
    string Name,
    int Rows,
    int Columns,
    IReadOnlyList<StreamDeckButtonDto> Buttons);

public sealed record StreamDeckButtonDto(
    string Id,
    int Row,
    int Column,
    string Label,
    StreamDeckIconDto Icon,

    // Whether the PC can currently run this button. The tablet uses it to draw
    // the button as unavailable; it says nothing about what would run.
    bool Enabled);

public enum StreamDeckIconReferenceKind
{
    None,

    /// <summary>A glyph the tablet resolves from its own asset catalog.</summary>
    Glyph,

    /// <summary>
    /// An image the PC holds in its icon cache. Only the opaque id travels -
    /// never the path - so a future protocol message can fetch the bytes by id.
    /// </summary>
    Image,
}

public sealed record StreamDeckIconDto(
    StreamDeckIconReferenceKind Kind,
    string? Glyph = null,
    string? IconId = null);
