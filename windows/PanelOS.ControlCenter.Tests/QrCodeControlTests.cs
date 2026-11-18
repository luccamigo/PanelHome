using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.Controls;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class QrCodeControlTests
{
    // The invitation QR is version 23: 109 modules, 117 with the quiet zone.
    private const int Modules = 109;

    [DataTestMethod]
    [DataRow(585.0, 1.0, 5)]   // about 585 px: five physical pixels per module
    [DataRow(468.0, 1.0, 4)]   // 468 px is exactly four
    [DataRow(470.0, 1.25, 5)]  // 125% scaling: 470 DIP are 587 physical pixels, five per module
    [DataRow(380.0, 1.25, 4)]  // 475 physical pixels: four per module
    [DataRow(335.0, 1.75, 5)]  // 175%: 586 physical pixels
    [DataRow(200.0, 1.0, 4)]   // too small: never below four, the page scrolls instead of the QR going unreadable
    public void The_module_is_a_whole_number_of_physical_pixels_between_four_and_five(double availableDip, double scale, int expected) =>
        Assert.AreEqual(expected, QrCodeControl.ModulePixels(Modules, availableDip, scale, QrCodeControl.DefaultMaxModulePixels));

    [TestMethod]
    public void Without_finite_room_the_largest_size_is_used_and_the_full_screen_view_may_go_far_beyond_five()
    {
        Assert.AreEqual(5, QrCodeControl.ModulePixels(Modules, double.PositiveInfinity, 1.0, 5));
        Assert.AreEqual(9, QrCodeControl.ModulePixels(Modules, 1100, 1.0, 48), "1100 / 117 is 9 whole pixels.");
        Assert.AreEqual(48, QrCodeControl.ModulePixels(Modules, 100000, 1.0, 48));
    }

    [TestMethod]
    public void The_size_in_device_independent_units_always_lands_on_whole_physical_pixels()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            var pixels = QrCodeControl.ModulePixels(Modules, 500, scale, 5);
            var physicalSide = (Modules + 2 * QrCodeControl.QuietZoneModules) * pixels;
            Assert.AreEqual(physicalSide, Math.Round((double)physicalSide), $"scale {scale}");
            Assert.IsTrue(pixels >= QrCodeControl.MinModulePixels);
        }
    }
}