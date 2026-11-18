using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Protocol;

namespace PanelHost.Tests;

[TestClass]
public sealed class HandshakeValidatorTests
{
    [TestMethod]
    public void Supported_protocol_is_accepted()
    {
        var hello = new HelloMessage
        {
            ProtocolVersion = PanelProtocol.PROTOCOL_VERSION,
            DeviceName = "PanelOS",
        };

        var result = HandshakeValidator.Validate(hello);

        Assert.IsTrue(result.IsAccepted);
        Assert.IsNull(result.ErrorCode);
    }

    [TestMethod]
    public void Unsupported_protocol_is_rejected_with_expected_code()
    {
        var hello = new HelloMessage
        {
            ProtocolVersion = PanelProtocol.PROTOCOL_VERSION + 1,
            DeviceName = "PanelOS",
        };

        var result = HandshakeValidator.Validate(hello);

        Assert.IsFalse(result.IsAccepted);
        Assert.AreEqual(ErrorCodes.UnsupportedProtocol, result.ErrorCode);
    }
}
