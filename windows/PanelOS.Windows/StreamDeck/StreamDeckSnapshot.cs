using PanelOS.Windows.StreamDeck.Dto;

namespace PanelOS.Windows.StreamDeck;

/// <summary>
/// One consistent view of the Stream Deck: the PC-side configuration, the
/// revision that names it and the tablet-facing projection of its active
/// profile.
/// </summary>
/// <remarks>
/// The three are produced together so a button resolved for execution and the
/// layout the tablet is drawing can never come from different reads of the
/// file. <see cref="Layout"/> is null when there is nothing to show; that is a
/// normal state - an empty configuration - and not an error.
/// </remarks>
public sealed record StreamDeckSnapshot(
    StreamDeckConfiguration Configuration,
    string Revision,
    StreamDeckLayoutDto? Layout)
{
    /// <summary>Nothing configured: the tablet shows its empty page.</summary>
    public static StreamDeckSnapshot Empty { get; } = new(
        new StreamDeckConfiguration(),
        StreamDeckLayoutRevision.None,
        null);

    public static StreamDeckSnapshot Create(StreamDeckConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new StreamDeckSnapshot(
            configuration,
            StreamDeckLayoutRevision.Compute(configuration),
            StreamDeckLayoutProjector.Project(configuration));
    }

    public string? ProfileId => Layout?.ProfileId;

    public string? ProfileName => Layout?.ProfileName;

    public int PageCount => Layout?.Pages.Count ?? 0;

    /// <summary>
    /// Resolves a button id against this exact configuration. Only ids that
    /// belong to the published profile are resolvable: a page the tablet cannot
    /// see is not a page it may invoke.
    /// </summary>
    public StreamDeckButton? FindPublishedButton(string? buttonId)
    {
        if (string.IsNullOrEmpty(buttonId) || Layout is null)
        {
            return null;
        }

        var profile = Configuration.Profiles.FirstOrDefault(
            candidate => string.Equals(
                candidate.Id,
                Layout.ProfileId,
                StringComparison.Ordinal));
        return profile?.Pages
            .SelectMany(page => page.Buttons)
            .FirstOrDefault(
                button => string.Equals(button.Id, buttonId, StringComparison.Ordinal));
    }

    /// <summary>Icon ids the published layout refers to.</summary>
    public IReadOnlyCollection<string> PublishedIconIds
    {
        get
        {
            if (Layout is null)
            {
                return [];
            }

            return
            [
                .. Layout.Pages
                    .SelectMany(page => page.Buttons)
                    .Select(button => button.Icon.IconId)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal),
            ];
        }
    }
}
