using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Serialization;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// A configuration written by an earlier build has to keep working. v1 stored a
/// custom icon as a path on disk and had no per-button enable flag.
/// </summary>
[TestClass]
public sealed class StreamDeckSchemaMigrationTests
{
    private const string VersionOneDocument = """
        {
          "version": 1,
          "activeProfileId": "p",
          "profiles": [
            {
              "id": "p",
              "name": "Main",
              "pages": [
                {
                  "id": "page",
                  "name": "Main",
                  "rows": 3,
                  "columns": 4,
                  "buttons": [
                    {
                      "id": "btn-icon",
                      "row": 0,
                      "column": 0,
                      "label": "Editor",
                      "icon": { "kind": "localFile", "filePath": "C:\\Fotos\\a.png" },
                      "action": { "kind": "openUrl", "url": "https://panelos.invalid" }
                    },
                    {
                      "id": "btn-plain",
                      "row": 0,
                      "column": 1,
                      "label": "Mudo",
                      "icon": { "kind": "actionDefault" },
                      "action": { "kind": "volumeCommand", "command": "muteToggle" }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [TestMethod]
    public void CurrentSchemaIsVersionTwo()
    {
        Assert.AreEqual(2, StreamDeckConfiguration.CurrentSchemaVersion);
        Assert.AreEqual(
            2,
            StreamDeckJson.ReadVersion(
                StreamDeckJson.Serialize(StreamDeckDefaults.CreateConfiguration())));
    }

    [TestMethod]
    public void AVersionOneDocumentStillLoads()
    {
        var configuration = StreamDeckJson.Deserialize(VersionOneDocument);

        Assert.AreEqual(
            StreamDeckConfiguration.CurrentSchemaVersion,
            configuration.Version);
        var buttons = configuration.Profiles[0].Pages[0].Buttons;
        Assert.AreEqual(2, buttons.Count);
        Assert.AreEqual(
            "https://panelos.invalid",
            ((OpenUrlAction)buttons[0].Action!).Url);
    }

    [TestMethod]
    public void AVersionOneIconKeepsItsOriginAsACustomIcon()
    {
        var configuration = StreamDeckJson.Deserialize(VersionOneDocument);

        var icon = configuration.Profiles[0].Pages[0].Buttons[0].Icon;
        Assert.AreEqual(StreamDeckIconKind.Custom, icon.Kind);
        Assert.AreEqual(@"C:\Fotos\a.png", icon.SourcePath);
        Assert.IsNull(icon.IconId, "a imagem ainda precisa ser importada para o cache");
        Assert.IsFalse(icon.HasImage);
    }

    [TestMethod]
    public void ButtonsFromVersionOneAreEnabled()
    {
        var configuration = StreamDeckJson.Deserialize(VersionOneDocument);

        Assert.IsTrue(
            configuration.Profiles[0].Pages[0].Buttons.All(button => button.Enabled));
    }

    [TestMethod]
    public void TheMigratedDocumentIsValidAndSavesAsVersionTwo()
    {
        var configuration = StreamDeckJson.Deserialize(VersionOneDocument);

        var validation = StreamDeckValidator.Validate(configuration);

        Assert.IsTrue(validation.IsValid, string.Join(" ", validation.Errors));
        Assert.AreEqual(2, StreamDeckJson.ReadVersion(
            StreamDeckJson.Serialize(configuration)));
    }

    [TestMethod]
    public void DisabledButtonsAndIconIdsRoundTrip()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();
        var button = configuration.Profiles[0].Pages[0].Buttons[0];
        button.Enabled = false;
        button.Icon = new StreamDeckIcon
        {
            Kind = StreamDeckIconKind.Custom,
            IconId = "icon-abc123",
            SourcePath = @"C:\Fotos\b.png",
        };

        var restored = StreamDeckJson.Deserialize(
            StreamDeckJson.Serialize(configuration));

        var restoredButton = restored.Profiles[0].Pages[0].Buttons[0];
        Assert.IsFalse(restoredButton.Enabled);
        Assert.IsFalse(restoredButton.IsRunnable);
        Assert.AreEqual("icon-abc123", restoredButton.Icon.IconId);
        Assert.AreEqual(@"C:\Fotos\b.png", restoredButton.Icon.SourcePath);
        Assert.IsTrue(restoredButton.Icon.HasImage);
    }
}
