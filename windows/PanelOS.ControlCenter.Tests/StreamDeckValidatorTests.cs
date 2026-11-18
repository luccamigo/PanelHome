using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.StreamDeck;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class StreamDeckValidatorTests
{
    [TestMethod]
    public void DefaultConfigurationIsValid()
    {
        var result = StreamDeckValidator.Validate(
            StreamDeckDefaults.CreateConfiguration());

        Assert.IsTrue(
            result.IsValid,
            string.Join(" ", result.Errors));
    }

    [TestMethod]
    public void GridSizeMustStayWithinTheSupportedRange()
    {
        Assert.IsTrue(StreamDeckValidator.IsGridSizeValid(3, 4));
        Assert.IsTrue(StreamDeckValidator.IsGridSizeValid(2, 2));
        Assert.IsTrue(StreamDeckValidator.IsGridSizeValid(6, 6));
        Assert.IsFalse(StreamDeckValidator.IsGridSizeValid(1, 4));
        Assert.IsFalse(StreamDeckValidator.IsGridSizeValid(3, 1));
        Assert.IsFalse(StreamDeckValidator.IsGridSizeValid(7, 4));
        Assert.IsFalse(StreamDeckValidator.IsGridSizeValid(3, 7));
    }

    [TestMethod]
    public void OversizedGridIsReported()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();
        configuration.Profiles[0].Pages[0].Rows = 12;

        var result = StreamDeckValidator.Validate(configuration);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Contains("grid")));
    }

    [TestMethod]
    public void DuplicateButtonIdIsRejectedAcrossPages()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();
        var profile = configuration.Profiles[0];
        profile.Pages.Add(new StreamDeckPage
        {
            Id = "page-second",
            Name = "Segunda",
            Rows = 1,
            Columns = 1,
            Buttons =
            [
                new StreamDeckButton
                {
                    Id = profile.Pages[0].Buttons[0].Id,
                    Label = "Cópia",
                },
            ],
        });

        var result = StreamDeckValidator.Validate(configuration);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Contains("Botão id duplicado")));
    }

    [TestMethod]
    public void DuplicatePageIdIsRejected()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();
        configuration.Profiles[0].Pages.Add(new StreamDeckPage
        {
            Id = StreamDeckDefaults.MainPageId,
            Name = "Cópia",
            Rows = 1,
            Columns = 1,
        });

        var result = StreamDeckValidator.Validate(configuration);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Contains("Página id duplicado")));
    }

    [TestMethod]
    public void ButtonOutsideTheGridIsRejected()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();
        configuration.Profiles[0].Pages[0].Buttons[0].Column = 9;

        var result = StreamDeckValidator.Validate(configuration);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Contains("fora do grid")));
    }

    [TestMethod]
    public void TwoButtonsOnTheSamePositionAreRejected()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();
        var buttons = configuration.Profiles[0].Pages[0].Buttons;
        buttons[1].Row = buttons[0].Row;
        buttons[1].Column = buttons[0].Column;

        var result = StreamDeckValidator.Validate(configuration);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(
            result.Errors.Any(error => error.Contains("mais de um botão")));
    }

    [TestMethod]
    public void UnsupportedSchemaVersionIsRejected()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();
        configuration.Version = 99;

        var result = StreamDeckValidator.Validate(configuration);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Contains("Schema v99")));
    }

    [TestMethod]
    public void UnknownActiveProfileIsRejected()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();
        configuration.ActiveProfileId = "profile-missing";

        var result = StreamDeckValidator.Validate(configuration);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(
            result.Errors.Any(error => error.Contains("activeProfileId")));
    }

    [TestMethod]
    public void CustomIconWithoutAnImageIsRejected()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();
        configuration.Profiles[0].Pages[0].Buttons[0].Icon = new StreamDeckIcon
        {
            Kind = StreamDeckIconKind.Custom,
        };

        var result = StreamDeckValidator.Validate(configuration);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Contains("ícone próprio")));
    }

    [TestMethod]
    public void AMalformedIconIdIsRejected()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();
        configuration.Profiles[0].Pages[0].Buttons[0].Icon = new StreamDeckIcon
        {
            Kind = StreamDeckIconKind.Custom,
            IconId = @"..\..\windows\win.ini",
        };

        var result = StreamDeckValidator.Validate(configuration);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Contains("iconId")));
    }
}
