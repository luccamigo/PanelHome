using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows;
using PanelOS.Windows.Host;
using PanelOS.Windows.Development;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class LogRedactionTests
{
    [TestMethod]
    public void LogBufferRedactsUserProfileMachineAndUsbInstancePath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var raw = $@"{profile}\AppData on {Environment.MachineName} at \\?\usb#vid_1234&pid_5678#SERIAL#{{guid}}";
        var buffer = new PanelHostLogBuffer();

        buffer.Append(PanelHostLogStream.StandardError, raw);
        var text = buffer.Snapshot().Single().Text;

        StringAssert.Contains(text, "<user-profile>");
        StringAssert.Contains(text, "<machine>");
        StringAssert.Contains(text, "<usb-device>");
        Assert.IsFalse(text.Contains("SERIAL", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Development_session_token_is_redacted_from_tool_failures()
    {
        var session = new DevelopmentSessionToken(
            () => Enumerable.Repeat((byte)0xa5, DevelopmentSessionToken.TokenBytes).ToArray());
        var token = session.Rotate();

        var redacted = session.Redact($"adb failed while handling {token}");

        StringAssert.Contains(redacted, "<adb-session-token>");
        Assert.IsFalse(redacted.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Development_session_token_is_256_bits_and_rotates()
    {
        var session = new DevelopmentSessionToken();

        var first = session.Rotate();
        var second = session.Rotate();

        Assert.AreEqual(DevelopmentSessionToken.TokenBytes * 2, first.Length);
        Assert.AreEqual(DevelopmentSessionToken.TokenBytes * 2, second.Length);
        Assert.IsTrue(first.All(Uri.IsHexDigit));
        Assert.AreNotEqual(first, second);
    }
}
