using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.StreamDeck;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// The revision must move whenever anything that could change what a button
/// does changes - including things the tablet cannot see.
/// </summary>
[TestClass]
public sealed class StreamDeckLayoutRevisionTests
{
    [TestMethod]
    public void TheSameConfigurationAlwaysProducesTheSameRevision()
    {
        Assert.AreEqual(
            StreamDeckLayoutRevision.Compute(Configuration()),
            StreamDeckLayoutRevision.Compute(Configuration()));
    }

    [TestMethod]
    public void TheRevisionRevealsNothingAboutTheConfiguration()
    {
        var configuration = Configuration();
        ((LaunchApplicationAction)configuration.Profiles[0].Pages[0].Buttons[1].Action!)
            .ExecutablePath = @"C:\Segredo\app.exe";

        var revision = StreamDeckLayoutRevision.Compute(configuration);

        Assert.AreEqual(16, revision.Length);
        Assert.IsTrue(revision.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
    }

    [DataTestMethod]
    [DataRow("label")]
    [DataRow("icon")]
    [DataRow("enabled")]
    [DataRow("position")]
    [DataRow("action")]
    [DataRow("page")]
    [DataRow("activeProfile")]
    public void EveryChangeThatAffectsBehaviourMovesTheRevision(string change)
    {
        var before = StreamDeckLayoutRevision.Compute(Configuration());
        var configuration = Configuration();
        var page = configuration.Profiles[0].Pages[0];
        var button = page.Buttons[0];

        switch (change)
        {
            case "label":
                button.Label = "Outro";
                break;
            case "icon":
                button.Icon = new StreamDeckIcon
                {
                    Kind = StreamDeckIconKind.Custom,
                    IconId = "icon-9999999999",
                };
                break;
            case "enabled":
                button.Enabled = false;
                break;
            case "position":
                button.Row = 1;
                break;
            case "action":
                // Nothing the tablet can see changes here, which is exactly why
                // the revision is computed from the whole document: the same id
                // would otherwise silently start doing something else.
                ((LaunchApplicationAction)page.Buttons[1].Action!).ExecutablePath =
                    @"C:\Outro\app.exe";
                break;
            case "page":
                configuration.Profiles[0].Pages.Add(new StreamDeckPage
                {
                    Id = "page-extra",
                    Name = "Extra",
                    Rows = 2,
                    Columns = 2,
                });
                break;
            case "activeProfile":
                configuration.ActiveProfileId = "profile-b";
                break;
            default:
                Assert.Fail($"unhandled change {change}");
                break;
        }

        Assert.AreNotEqual(before, StreamDeckLayoutRevision.Compute(configuration));
    }

    [TestMethod]
    public void AnEmptyConfigurationHasNoLayoutButStillHasARevision()
    {
        var snapshot = StreamDeckSnapshot.Create(new StreamDeckConfiguration());

        Assert.IsNull(snapshot.Layout);
        Assert.AreEqual(0, snapshot.PageCount);
        Assert.AreNotEqual(string.Empty, snapshot.Revision);
        Assert.AreEqual(0, snapshot.PublishedIconIds.Count);
    }

    [TestMethod]
    public void OnlyButtonsOfThePublishedProfileAreResolvable()
    {
        var snapshot = StreamDeckSnapshot.Create(Configuration());

        Assert.IsNotNull(snapshot.FindPublishedButton("btn-a"));

        // btn-c belongs to the profile that is not published; the tablet was
        // never shown it and may not invoke it.
        Assert.IsNull(snapshot.FindPublishedButton("btn-c"));
        Assert.IsNull(snapshot.FindPublishedButton("btn-inventado"));
        Assert.IsNull(snapshot.FindPublishedButton(null));
    }

    [TestMethod]
    public void PublishedIconIdsAreTakenFromTheActiveProfileOnly()
    {
        var configuration = Configuration();
        configuration.Profiles[0].Pages[0].Buttons[0].Icon = new StreamDeckIcon
        {
            Kind = StreamDeckIconKind.Custom,
            IconId = "icon-1111111111",
        };
        configuration.Profiles[1].Pages[0].Buttons[0].Icon = new StreamDeckIcon
        {
            Kind = StreamDeckIconKind.Custom,
            IconId = "icon-2222222222",
        };

        var icons = StreamDeckSnapshot.Create(configuration).PublishedIconIds;

        CollectionAssert.AreEquivalent(new[] { "icon-1111111111" }, icons.ToArray());
    }

    private static StreamDeckConfiguration Configuration() => new()
    {
        ActiveProfileId = "profile-a",
        Profiles =
        [
            new StreamDeckProfile
            {
                Id = "profile-a",
                Name = "Principal",
                Pages =
                [
                    new StreamDeckPage
                    {
                        Id = "page-a",
                        Name = "Principal",
                        Rows = 2,
                        Columns = 2,
                        Buttons =
                        [
                            new StreamDeckButton
                            {
                                Id = "btn-a",
                                Row = 0,
                                Column = 0,
                                Label = "Play",
                                Action = new MediaCommandAction
                                {
                                    Command = MediaCommand.PlayPause,
                                },
                            },
                            new StreamDeckButton
                            {
                                Id = "btn-b",
                                Row = 0,
                                Column = 1,
                                Label = "App",
                                Action = new LaunchApplicationAction
                                {
                                    ExecutablePath = @"C:\Apps\app.exe",
                                },
                            },
                        ],
                    },
                ],
            },
            new StreamDeckProfile
            {
                Id = "profile-b",
                Name = "Mídia",
                Pages =
                [
                    new StreamDeckPage
                    {
                        Id = "page-b",
                        Name = "Mídia",
                        Rows = 2,
                        Columns = 2,
                        Buttons =
                        [
                            new StreamDeckButton
                            {
                                Id = "btn-c",
                                Row = 0,
                                Column = 0,
                                Label = "Mute",
                                Action = new VolumeCommandAction
                                {
                                    Command = VolumeCommand.MuteToggle,
                                },
                            },
                        ],
                    },
                ],
            },
        ],
    };
}
