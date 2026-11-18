using PanelOS.Windows.StreamDeck.Serialization;

namespace PanelOS.Windows.StreamDeck;

/// <summary>
/// Deep copies for the editor's "duplicate" commands.
/// </summary>
/// <remarks>
/// The copy goes through the serializer instead of a hand-written clone per
/// action type: it is the same code that already has to understand every
/// action, including one written by a newer build, so a new action kind cannot
/// silently lose its configuration when duplicated.
/// </remarks>
public static class StreamDeckCloner
{
    public static StreamDeckProfile CloneProfile(
        StreamDeckProfile profile,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var copy = RoundTrip(new StreamDeckConfiguration
        {
            ActiveProfileId = profile.Id,
            Profiles = [profile],
        }).Profiles[0];

        copy.Id = NewProfileId();
        copy.Name = name ?? profile.Name;
        foreach (var page in copy.Pages)
        {
            AssignNewIds(page);
        }

        return copy;
    }

    public static StreamDeckPage ClonePage(StreamDeckPage page, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var copy = RoundTrip(new StreamDeckConfiguration
        {
            ActiveProfileId = "clone",
            Profiles =
            [
                new StreamDeckProfile
                {
                    Id = "clone",
                    Name = "clone",
                    Pages = [page],
                },
            ],
        }).Profiles[0].Pages[0];

        AssignNewIds(copy);
        copy.Name = name ?? page.Name;
        return copy;
    }

    public static string NewProfileId() => $"profile-{Guid.NewGuid():n}";

    public static string NewPageId() => $"page-{Guid.NewGuid():n}";

    public static string NewButtonId() => $"btn-{Guid.NewGuid():n}";

    /// <summary>
    /// Gives a page and its buttons fresh identities. Ids are unique across the
    /// whole configuration, so a copy may never keep the original's.
    /// </summary>
    private static void AssignNewIds(StreamDeckPage page)
    {
        page.Id = NewPageId();
        foreach (var button in page.Buttons)
        {
            button.Id = NewButtonId();
        }
    }

    private static StreamDeckConfiguration RoundTrip(
        StreamDeckConfiguration configuration)
    {
        configuration.Version = StreamDeckConfiguration.CurrentSchemaVersion;
        return StreamDeckJson.Deserialize(StreamDeckJson.Serialize(configuration));
    }
}
