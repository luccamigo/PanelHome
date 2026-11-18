using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class KeyboardChordBuilderTests
{
    [TestMethod]
    public void ModifiersArePressedBeforeTheKeyAndReleasedAfterIt()
    {
        var events = KeyboardChordBuilder.Build(
            PanelKeyModifiers.Control | PanelKeyModifiers.Shift,
            PanelVirtualKey.S);

        CollectionAssert.AreEqual(
            new[]
            {
                "Control down",
                "Shift down",
                "S down",
                "S up",
                "Shift up",
                "Control up",
            },
            events.Select(entry => entry.ToString()).ToArray());
    }

    [TestMethod]
    public void ModifiersAreReleasedInReverseOrder()
    {
        var events = KeyboardChordBuilder.Build(
            PanelKeyModifiers.Windows | PanelKeyModifiers.Shift,
            PanelVirtualKey.S);

        // Shift is pressed first and released last: the sequence unwinds.
        CollectionAssert.AreEqual(
            new[]
            {
                "Shift down",
                "LeftWindows down",
                "S down",
                "S up",
                "LeftWindows up",
                "Shift up",
            },
            events.Select(entry => entry.ToString()).ToArray());
    }

    [TestMethod]
    public void AKeyWithoutModifiersIsJustPressedAndReleased()
    {
        var events = KeyboardChordBuilder.Build(
            PanelKeyModifiers.None,
            PanelVirtualKey.F5);

        Assert.AreEqual(2, events.Count);
        Assert.AreEqual(KeyTransition.Down, events[0].Transition);
        Assert.AreEqual(KeyTransition.Up, events[1].Transition);
    }

    [TestMethod]
    public void EveryKeyDownHasItsKeyUp()
    {
        var events = KeyboardChordBuilder.Build(
            PanelKeyModifiers.Control | PanelKeyModifiers.Shift |
                PanelKeyModifiers.Alt | PanelKeyModifiers.Windows,
            PanelVirtualKey.Escape);

        foreach (var group in events.GroupBy(entry => entry.Key))
        {
            Assert.AreEqual(
                1,
                group.Count(entry => entry.Transition == KeyTransition.Down),
                $"{group.Key} deveria ser pressionada uma vez");
            Assert.AreEqual(
                1,
                group.Count(entry => entry.Transition == KeyTransition.Up),
                $"{group.Key} deveria ser solta uma vez");
        }
    }

    [TestMethod]
    public void AChordNeedsAKey()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => KeyboardChordBuilder.Build(
                PanelKeyModifiers.Control,
                PanelVirtualKey.None));
    }

    [TestMethod]
    public void DomainKeysMapOntoVirtualKeys()
    {
        Assert.IsTrue(KeyboardChordBuilder.TryMap(PanelKey.D, out var d));
        Assert.AreEqual(PanelVirtualKey.D, d);
        Assert.IsTrue(KeyboardChordBuilder.TryMap(PanelKey.D0, out var zero));
        Assert.AreEqual(PanelVirtualKey.D0, zero);
        Assert.IsTrue(KeyboardChordBuilder.TryMap(PanelKey.D9, out var nine));
        Assert.AreEqual(PanelVirtualKey.D9, nine);
        Assert.IsTrue(KeyboardChordBuilder.TryMap(PanelKey.F12, out var f12));
        Assert.AreEqual(PanelVirtualKey.F12, f12);
        Assert.IsTrue(KeyboardChordBuilder.TryMap(PanelKey.PrintScreen, out var print));
        Assert.AreEqual(PanelVirtualKey.PrintScreen, print);
        Assert.IsTrue(KeyboardChordBuilder.TryMap(PanelKey.Left, out var left));
        Assert.AreEqual(PanelVirtualKey.ArrowLeft, left);
        Assert.IsFalse(KeyboardChordBuilder.TryMap(PanelKey.None, out _));
    }

    [TestMethod]
    public void EveryDomainKeyExceptNoneCanBePressed()
    {
        foreach (var key in Enum.GetValues<PanelKey>().Where(k => k != PanelKey.None))
        {
            Assert.IsTrue(
                KeyboardChordBuilder.TryMap(key, out _),
                $"{key} está no domínio e deveria ter um virtual key");
        }
    }

    [TestMethod]
    public void MediaAndVolumeCommandsMapOntoTheirKeys()
    {
        Assert.AreEqual(
            PanelVirtualKey.MediaPlayPause,
            KeyboardChordBuilder.ForMediaCommand(MediaCommand.PlayPause));
        Assert.AreEqual(
            PanelVirtualKey.MediaPreviousTrack,
            KeyboardChordBuilder.ForMediaCommand(MediaCommand.Previous));
        Assert.AreEqual(
            PanelVirtualKey.MediaNextTrack,
            KeyboardChordBuilder.ForMediaCommand(MediaCommand.Next));

        Assert.AreEqual(
            PanelVirtualKey.VolumeMute,
            KeyboardChordBuilder.ForVolumeCommand(VolumeCommand.MuteToggle));
        Assert.AreEqual(
            PanelVirtualKey.VolumeUp,
            KeyboardChordBuilder.ForVolumeCommand(VolumeCommand.VolumeUp));
        Assert.AreEqual(
            PanelVirtualKey.VolumeDown,
            KeyboardChordBuilder.ForVolumeCommand(VolumeCommand.VolumeDown));

        // An absolute level has no keyboard equivalent.
        Assert.AreEqual(
            PanelVirtualKey.None,
            KeyboardChordBuilder.ForVolumeCommand(VolumeCommand.SetLevel));
    }

    [TestMethod]
    public void VirtualKeyValuesMatchTheWin32Constants()
    {
        Assert.AreEqual(0x11, (int)PanelVirtualKey.Control);
        Assert.AreEqual(0x10, (int)PanelVirtualKey.Shift);
        Assert.AreEqual(0x12, (int)PanelVirtualKey.Alt);
        Assert.AreEqual(0x5B, (int)PanelVirtualKey.LeftWindows);
        Assert.AreEqual(0x44, (int)PanelVirtualKey.D);
        Assert.AreEqual(0x53, (int)PanelVirtualKey.S);
        Assert.AreEqual(0xAD, (int)PanelVirtualKey.VolumeMute);
        Assert.AreEqual(0xB3, (int)PanelVirtualKey.MediaPlayPause);
    }
}
