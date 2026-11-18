using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelOS.Windows.StreamDeck.Dto;

/// <summary>
/// Projects the PC-side configuration onto the tablet-facing layout. This is
/// the single place where the boundary is enforced: it copies identifiers and
/// visual metadata field by field and never touches action configuration.
/// </summary>
public static class StreamDeckLayoutProjector
{
    /// <summary>Layout schema announced to the tablet.</summary>
    public const int LayoutVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static StreamDeckLayoutDto Project(StreamDeckProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new StreamDeckLayoutDto(
            LayoutVersion,
            profile.Id,
            profile.Name,
            [.. profile.Pages.Select(ProjectPage)]);
    }

    /// <summary>
    /// Projects the configuration's active profile, or null when there is none.
    /// </summary>
    public static StreamDeckLayoutDto? Project(StreamDeckConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var profile = configuration.FindActiveProfile();
        return profile is null ? null : Project(profile);
    }

    public static string Serialize(StreamDeckLayoutDto layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return JsonSerializer.Serialize(layout, SerializerOptions);
    }

    private static StreamDeckPageDto ProjectPage(StreamDeckPage page) => new(
        page.Id,
        page.Name,
        page.Rows,
        page.Columns,
        [.. page.Buttons.Select(ProjectButton)]);

    private static StreamDeckButtonDto ProjectButton(StreamDeckButton button) => new(
        button.Id,
        button.Row,
        button.Column,
        button.Label,
        ProjectIcon(button),
        button.IsRunnable);

    private static StreamDeckIconDto ProjectIcon(StreamDeckButton button) =>
        button.Icon.Kind switch
        {
            // The image lives in the PC's icon cache. Only its id travels; the
            // source path the user picked never leaves this machine.
            StreamDeckIconKind.Custom when button.Icon.HasImage =>
                new StreamDeckIconDto(
                    StreamDeckIconReferenceKind.Image,
                    IconId: button.Icon.IconId),
            StreamDeckIconKind.ActionDefault when button.Action is not null =>
                new StreamDeckIconDto(
                    StreamDeckIconReferenceKind.Glyph,
                    Glyph: button.Action.DefaultGlyph),
            _ => new StreamDeckIconDto(StreamDeckIconReferenceKind.None),
        };
}
