using System.Security.Cryptography;
using System.Text;
using PanelOS.Windows.StreamDeck.Serialization;

namespace PanelOS.Windows.StreamDeck;

/// <summary>
/// Identity of one published Stream Deck configuration.
/// </summary>
/// <remarks>
/// <para>
/// The revision travels to the tablet and comes back with every invocation, so
/// PanelHost can refuse a button pressed against a layout that no longer
/// exists. It is derived from the whole canonical document rather than from the
/// projected layout on purpose: repointing a button at a different application
/// changes nothing the tablet can see, yet it is exactly the case the staleness
/// check exists for - the same button id would suddenly mean another action.
/// </para>
/// <para>
/// The value is a digest, so nothing about the configuration can be read back
/// out of it. Labels, paths and URLs are hashed here and never transmitted.
/// </para>
/// </remarks>
public static class StreamDeckLayoutRevision
{
    /// <summary>Revision of "there is no configuration to show".</summary>
    public const string None = "0";

    public static string Compute(StreamDeckConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The serializer is the canonical form: it already knows every action
        // kind, including one written by a newer build, so no field can change
        // behaviour without changing these bytes.
        var canonical = StreamDeckJson.Serialize(configuration);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
