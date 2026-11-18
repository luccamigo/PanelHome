using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Serialization;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class StreamDeckSerializationTests
{
    [TestMethod]
    public void SerializedDocumentDeclaresSchemaVersion()
    {
        var json = StreamDeckJson.Serialize(StreamDeckDefaults.CreateConfiguration());

        Assert.AreEqual(2, StreamDeckConfiguration.CurrentSchemaVersion);
        Assert.AreEqual(2, StreamDeckJson.ReadVersion(json));
        StringAssert.Contains(json, "\"version\": 2");
    }

    [TestMethod]
    public void DefaultProfileRoundTripsWithEveryActionIntact()
    {
        var original = StreamDeckDefaults.CreateConfiguration();

        var restored = StreamDeckJson.Deserialize(StreamDeckJson.Serialize(original));

        var originalPage = original.Profiles[0].Pages[0];
        var restoredPage = restored.Profiles[0].Pages[0];
        Assert.AreEqual(original.ActiveProfileId, restored.ActiveProfileId);
        Assert.AreEqual(originalPage.Rows, restoredPage.Rows);
        Assert.AreEqual(originalPage.Columns, restoredPage.Columns);
        Assert.AreEqual(originalPage.Buttons.Count, restoredPage.Buttons.Count);
        for (var index = 0; index < originalPage.Buttons.Count; index++)
        {
            var expected = originalPage.Buttons[index];
            var actual = restoredPage.Buttons[index];
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Label, actual.Label);
            Assert.AreEqual(expected.Row, actual.Row);
            Assert.AreEqual(expected.Column, actual.Column);
            Assert.AreEqual(expected.Action?.Kind, actual.Action?.Kind);
        }
    }

    [TestMethod]
    public void EveryActionKindRoundTripsItsConfiguration()
    {
        var configuration = CreateConfiguration(
            Button("a", 0, 0, new LaunchApplicationAction
            {
                ExecutablePath = @"C:\Apps\demo.exe",
                Arguments = "--flag",
                WorkingDirectory = @"C:\Apps",
            }),
            Button("b", 0, 1, new OpenUrlAction { Url = "https://example.invalid" }),
            Button("c", 0, 2, new KeyboardShortcutAction
            {
                Modifiers = PanelKeyModifiers.Control | PanelKeyModifiers.Shift,
                Key = PanelKey.S,
            }),
            Button("d", 0, 3, new MediaCommandAction
            {
                Command = MediaCommand.Next,
            }),
            Button("e", 1, 0, new VolumeCommandAction
            {
                Command = VolumeCommand.SetLevel,
                Level = 35,
            }),
            Button("f", 1, 1, new PanelModeAction { Mode = PanelMode.StreamDeck }));

        var restored = StreamDeckJson.Deserialize(
            StreamDeckJson.Serialize(configuration));
        var buttons = restored.Profiles[0].Pages[0].Buttons;

        var launch = (LaunchApplicationAction)buttons[0].Action!;
        Assert.AreEqual(@"C:\Apps\demo.exe", launch.ExecutablePath);
        Assert.AreEqual("--flag", launch.Arguments);
        Assert.AreEqual(@"C:\Apps", launch.WorkingDirectory);
        Assert.AreEqual(
            "https://example.invalid",
            ((OpenUrlAction)buttons[1].Action!).Url);
        var shortcut = (KeyboardShortcutAction)buttons[2].Action!;
        Assert.AreEqual(
            PanelKeyModifiers.Control | PanelKeyModifiers.Shift,
            shortcut.Modifiers);
        Assert.AreEqual(PanelKey.S, shortcut.Key);
        Assert.AreEqual("Ctrl + Shift + S", shortcut.Describe());
        Assert.AreEqual(
            MediaCommand.Next,
            ((MediaCommandAction)buttons[3].Action!).Command);
        var volume = (VolumeCommandAction)buttons[4].Action!;
        Assert.AreEqual(VolumeCommand.SetLevel, volume.Command);
        Assert.AreEqual(35, volume.Level);
        Assert.AreEqual(
            PanelMode.StreamDeck,
            ((PanelModeAction)buttons[5].Action!).Mode);
    }

    [TestMethod]
    public void ActionKindsArePersistedAsStableNames()
    {
        var json = StreamDeckJson.Serialize(CreateConfiguration(
            Button("a", 0, 0, new KeyboardShortcutAction
            {
                Modifiers = PanelKeyModifiers.Windows,
                Key = PanelKey.D,
            })));

        StringAssert.Contains(json, "\"kind\": \"keyboardShortcut\"");
        StringAssert.Contains(json, "\"windows\"");
        StringAssert.Contains(json, "\"key\": \"d\"");
    }

    [TestMethod]
    public void ButtonWithoutActionRoundTripsAsUnconfigured()
    {
        var configuration = CreateConfiguration(Button("a", 0, 0, action: null));

        var restored = StreamDeckJson.Deserialize(
            StreamDeckJson.Serialize(configuration));

        var button = restored.Profiles[0].Pages[0].Buttons[0];
        Assert.IsNull(button.Action);
        Assert.IsFalse(button.IsExecutable);
    }

    [TestMethod]
    public void UnknownActionKindIsPreservedInsteadOfDiscarded()
    {
        const string json = """
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
                      "rows": 1,
                      "columns": 1,
                      "buttons": [
                        {
                          "id": "b",
                          "row": 0,
                          "column": 0,
                          "label": "Futuro",
                          "icon": { "kind": "actionDefault" },
                          "action": { "kind": "runScript", "script": "future.ps1" }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        var configuration = StreamDeckJson.Deserialize(json);

        var action = configuration.Profiles[0].Pages[0].Buttons[0].Action;
        var unknown = action as UnknownPanelAction;
        Assert.IsNotNull(unknown);
        Assert.AreEqual("runScript", unknown.RawKind);
        Assert.IsFalse(unknown.IsConfigured);
        StringAssert.Contains(StreamDeckJson.Serialize(configuration), "runScript");
    }

    [TestMethod]
    public void DocumentWithoutVersionIsRejected()
    {
        Assert.ThrowsException<JsonException>(
            () => StreamDeckJson.Deserialize("""{"profiles": []}"""));
    }

    [TestMethod]
    public void NewerSchemaVersionIsReportedInsteadOfParsed()
    {
        var exception = Assert.ThrowsException<StreamDeckSchemaException>(
            () => StreamDeckJson.Deserialize("""{"version": 99, "profiles": []}"""));

        Assert.AreEqual(99, exception.Version);
    }

    [TestMethod]
    public void MalformedJsonIsRejected()
    {
        try
        {
            // The reader throws a JsonException subtype, so this cannot use
            // Assert.ThrowsException, which matches the exact type.
            StreamDeckJson.Deserialize("{ not json");
            Assert.Fail("Malformed JSON must not parse.");
        }
        catch (JsonException)
        {
        }
    }

    internal static StreamDeckConfiguration CreateConfiguration(
        params StreamDeckButton[] buttons) => new()
        {
            Version = StreamDeckConfiguration.CurrentSchemaVersion,
            ActiveProfileId = "profile",
            Profiles =
            [
                new StreamDeckProfile
                {
                    Id = "profile",
                    Name = "Main",
                    Pages =
                    [
                        new StreamDeckPage
                        {
                            Id = "page",
                            Name = "Main",
                            Rows = 3,
                            Columns = 4,
                            Buttons = [.. buttons],
                        },
                    ],
                },
            ],
        };

    internal static StreamDeckButton Button(
        string id,
        int row,
        int column,
        PanelAction? action) => new()
        {
            Id = id,
            Row = row,
            Column = column,
            Label = $"Botão {id}",
            Action = action,
        };
}
