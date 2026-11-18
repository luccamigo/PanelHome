namespace PanelHost.Protocol;

public sealed record HandshakeValidationResult(bool IsAccepted, string? ErrorCode)
{
    public static HandshakeValidationResult Accepted { get; } = new(true, null);

    public static HandshakeValidationResult Rejected(string errorCode) => new(false, errorCode);
}

public static class HandshakeValidator
{
    public static HandshakeValidationResult Validate(HelloMessage hello)
    {
        if (hello.Type != MessageTypes.Hello ||
            string.IsNullOrWhiteSpace(hello.DeviceName) ||
            hello.DeviceName.Length > PanelProtocolLimits.MaximumPeerNameLength)
        {
            return HandshakeValidationResult.Rejected(ErrorCodes.InvalidHandshake);
        }

        return hello.ProtocolVersion == PanelProtocol.PROTOCOL_VERSION
            ? HandshakeValidationResult.Accepted
            : HandshakeValidationResult.Rejected(ErrorCodes.UnsupportedProtocol);
    }
}
