using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Dto;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// The tablet must never receive anything executable. These tests guard the
/// projection that enforces it.
/// </summary>
[TestClass]
public sealed class StreamDeckLayoutDtoTests
{
    private const string SecretPath = @"C:\Secreto\ferramenta.exe";
    private const string SecretArguments = "--token abcdef";
    private const string SecretDirectory = @"C:\Secreto";
    private const string SecretUrl = "https://interno.invalid/painel";
    private const string SecretIconPath = @"C:\Secreto\icone.png";
    private const string CachedIconId = "icon-0123456789";

    [TestMethod]
    public void LayoutCarriesIdentityAndVisualMetadataOnly()
    {
        var layout = StreamDeckLayoutProjector.Project(CreateConfiguration())!;

        Assert.AreEqual(1, layout.Version);
        Assert.AreEqual("profile", layout.ProfileId);
        var page = layout.Pages[0];
        Assert.AreEqual("page", page.Id);
        Assert.AreEqual(3, page.Rows);
        Assert.AreEqual(4, page.Columns);
        var launcher = page.Buttons[0];
        Assert.AreEqual("launcher", launcher.Id);
        Assert.AreEqual("Ferramenta", launcher.Label);
        Assert.AreEqual(0, launcher.Row);
        Assert.AreEqual(0, launcher.Column);
        Assert.IsTrue(launcher.Enabled);
    }

    [TestMethod]
    public void SerializedLayoutLeaksNoPathArgumentOrUrl()
    {
        var layout = StreamDeckLayoutProjector.Project(CreateConfiguration())!;

        var json = StreamDeckLayoutProjector.Serialize(layout);

        StringAssert.Contains(json, "Ferramenta");
        foreach (var secret in new[]
                 {
                     SecretPath,
                     SecretArguments,
                     SecretDirectory,
                     SecretUrl,
                     SecretIconPath,
                     "ferramenta.exe",
                     "abcdef",
                 })
        {
            Assert.IsFalse(
                json.Contains(secret, StringComparison.OrdinalIgnoreCase),
                $"O layout enviado ao tablet não pode conter \"{secret}\": {json}");
        }
    }

    [TestMethod]
    public void DtoTypesDeclareNoActionConfigurationMembers()
    {
        string[] forbidden =
        [
            "executable",
            "path",
            "argument",
            "commandline",
            "command",
            "script",
            "url",
            "workingdirectory",
        ];

        foreach (var type in new[]
                 {
                     typeof(StreamDeckLayoutDto),
                     typeof(StreamDeckPageDto),
                     typeof(StreamDeckButtonDto),
                     typeof(StreamDeckIconDto),
                 })
        {
            foreach (var property in type.GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                var name = property.Name.ToLowerInvariant();
                Assert.IsFalse(
                    forbidden.Any(name.Contains),
                    $"{type.Name}.{property.Name} expõe configuração que deve " +
                    "permanecer no PC.");
            }
        }
    }

    [TestMethod]
    public void ACustomIconTravelsAsAnOpaqueCacheId()
    {
        var layout = StreamDeckLayoutProjector.Project(CreateConfiguration())!;

        var icon = layout.Pages[0].Buttons[2].Icon;

        Assert.AreEqual(StreamDeckIconReferenceKind.Image, icon.Kind);
        Assert.AreEqual(CachedIconId, icon.IconId);
        Assert.IsNull(icon.Glyph);
    }

    [TestMethod]
    public void ACustomIconWithoutAnImportedImageSendsNothing()
    {
        var configuration = StreamDeckSerializationTests.CreateConfiguration(
            new StreamDeckButton
            {
                Id = "pendente",
                Row = 0,
                Column = 0,
                Label = "Pendente",
                Icon = new StreamDeckIcon
                {
                    Kind = StreamDeckIconKind.Custom,
                    SourcePath = SecretIconPath,
                },
                Action = new MediaCommandAction(),
            });

        var layout = StreamDeckLayoutProjector.Project(configuration)!;

        Assert.AreEqual(
            StreamDeckIconReferenceKind.None,
            layout.Pages[0].Buttons[0].Icon.Kind);
        Assert.IsFalse(
            StreamDeckLayoutProjector.Serialize(layout)
                .Contains(SecretIconPath, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ADisabledButtonIsSentAsUnavailable()
    {
        var configuration = CreateConfiguration();
        configuration.Profiles[0].Pages[0].Buttons[2].Enabled = false;

        var layout = StreamDeckLayoutProjector.Project(configuration)!;

        Assert.IsFalse(layout.Pages[0].Buttons[2].Enabled);
    }

    [TestMethod]
    public void ActionDefaultIconBecomesAGlyph()
    {
        var layout = StreamDeckLayoutProjector.Project(CreateConfiguration())!;

        Assert.AreEqual(
            StreamDeckIconReferenceKind.Glyph,
            layout.Pages[0].Buttons[0].Icon.Kind);
        Assert.AreEqual("app", layout.Pages[0].Buttons[0].Icon.Glyph);
        Assert.AreEqual(
            StreamDeckIconReferenceKind.None,
            layout.Pages[0].Buttons[3].Icon.Kind);
    }

    [TestMethod]
    public void UnconfiguredButtonIsMarkedDisabled()
    {
        var layout = StreamDeckLayoutProjector.Project(CreateConfiguration())!;

        Assert.IsFalse(layout.Pages[0].Buttons[3].Enabled);
    }

    private static StreamDeckConfiguration CreateConfiguration()
    {
        var configuration = StreamDeckSerializationTests.CreateConfiguration(
            new StreamDeckButton
            {
                Id = "launcher",
                Row = 0,
                Column = 0,
                Label = "Ferramenta",
                Action = new LaunchApplicationAction
                {
                    ExecutablePath = SecretPath,
                    Arguments = SecretArguments,
                    WorkingDirectory = SecretDirectory,
                },
            },
            new StreamDeckButton
            {
                Id = "url",
                Row = 0,
                Column = 1,
                Label = "Painel interno",
                Action = new OpenUrlAction { Url = SecretUrl },
            },
            new StreamDeckButton
            {
                Id = "shortcut",
                Row = 0,
                Column = 2,
                Label = "Captura",
                Icon = new StreamDeckIcon
                {
                    Kind = StreamDeckIconKind.Custom,
                    IconId = CachedIconId,
                    SourcePath = SecretIconPath,
                },
                Action = new KeyboardShortcutAction
                {
                    Modifiers = PanelKeyModifiers.Windows | PanelKeyModifiers.Shift,
                    Key = PanelKey.S,
                },
            },
            new StreamDeckButton
            {
                Id = "empty",
                Row = 0,
                Column = 3,
                Label = "Sem ação",
                Icon = StreamDeckIcon.None(),
            });
        return configuration;
    }
}
